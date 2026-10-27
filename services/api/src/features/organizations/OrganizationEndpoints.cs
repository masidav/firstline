using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Users;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Organizations;

/// <summary>
/// Promoter organizations (migration 0037; README.md here). Staff create an organization with its first owner;
/// members list their organizations; owners add members by handle, change roles, and remove members. Every
/// membership change is written to <c>organization_audit</c> by the database, with the signed-in user as the actor.
/// The organization's shows and allocations are managed under the same prefix (AllocationEndpoints.Organizations.cs).
/// </summary>
public static class OrganizationEndpoints
{
    public static void MapOrganizations(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapPost("/organizations", CreateAsync);
        admin.MapGet("/organizations", ListAllAsync);

        v1.MapGet("/me/organizations", ListMineAsync);
        var organization = v1.MapGroup("/organizations/{organizationId:guid}");
        organization.MapGet("", GetAsync).AddEndpointFilter(OrganizationAccess.Require("analyst"));
        organization.MapPut("/members/{handle}", PutMemberAsync).AddEndpointFilter(OrganizationAccess.Require("owner"));
        organization.MapDelete("/members/{handle}", RemoveMemberAsync).AddEndpointFilter(OrganizationAccess.Require("owner"));
    }

    public sealed record Member(string Handle, string? DisplayName, string Role, DateTime AddedAt);

    /// <summary>openapi Organization. <c>MyRole</c>: the caller's role, null for staff who aren't members.</summary>
    public sealed record Organization(Guid Id, string Name, string Kind, DateTime CreatedAt, string? MyRole, IReadOnlyList<Member> Members);

    public sealed record OrganizationList(IReadOnlyList<Organization> Items);

    public sealed record MemberRequest(string? Role);

    // Staff: the organization and its first owner, by handle, in one transaction (an organization always has an owner).
    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var name = Field(body, "name")?.Trim();
        var ownerHandle = Field(body, "owner_handle")?.Trim().TrimStart('@');
        var errors = new List<FieldError>();
        if (name is not { Length: >= 1 and <= 200 })
        {
            errors.Add(new FieldError("name", "Send 1 to 200 characters."));
        }

        if (ownerHandle is not { Length: > 0 })
        {
            errors.Add(new FieldError("owner_handle", "Send the handle of the account that will own the organization."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var staffId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ownerId = await AccountAsync(connection, ownerHandle!, "owner_handle");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetActorAsync(connection, transaction, staffId);
        Guid organizationId;
        try
        {
            organizationId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO organizations (name, created_by) VALUES (@name, @staffId) RETURNING id", new { name, staffId }, transaction);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Name taken", "An organization already has this name.");
        }

        await connection.ExecuteAsync(
            "INSERT INTO organization_members (organization_id, user_id, role, added_by) VALUES (@organizationId, @ownerId, 'owner', @staffId)",
            new { organizationId, ownerId, staffId }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/v1/organizations/{organizationId}", await LoadAsync(connection, organizationId, staffId));
    }

    private static async Task<IResult> ListAllAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY lower(name) LIMIT 500");
        var items = new List<Organization>();
        foreach (var id in ids)
        {
            items.Add(await LoadAsync(connection, id, user.UserId()));
        }

        return Results.Ok(new OrganizationList(items));
    }

    private static async Task<IResult> ListMineAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(
            """
            SELECT o.id FROM organizations o JOIN organization_members m ON m.organization_id = o.id
            WHERE m.user_id = @userId ORDER BY lower(o.name)
            """,
            new { userId });
        var items = new List<Organization>();
        foreach (var id in ids)
        {
            items.Add(await LoadAsync(connection, id, userId));
        }

        return Results.Ok(new OrganizationList(items));
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, Guid organizationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadAsync(connection, organizationId, user.UserId()));
    }

    // Adds the account as a member with the role, or changes its role. Owners only.
    private static async Task<IResult> PutMemberAsync(
        ClaimsPrincipal user, Guid organizationId, string handle, MemberRequest request, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (!OrganizationAccess.IsRole(request.Role))
        {
            throw ApiProblemException.Validation(new FieldError("role", "Use owner, admin, or analyst."));
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var memberId = await AccountAsync(connection, handle.TrimStart('@'), "handle");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetActorAsync(connection, transaction, user.UserId());
        await GuardOwnerAsync(() => connection.ExecuteAsync(
            """
            INSERT INTO organization_members (organization_id, user_id, role, added_by)
            VALUES (@organizationId, @memberId, @role, @actor)
            ON CONFLICT (organization_id, user_id) DO UPDATE SET role = EXCLUDED.role
              WHERE organization_members.role IS DISTINCT FROM EXCLUDED.role
            """,
            new { organizationId, memberId, role = request.Role, actor = user.UserId() }, transaction));
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await LoadAsync(connection, organizationId, user.UserId()));
    }

    private static async Task<IResult> RemoveMemberAsync(
        ClaimsPrincipal user, Guid organizationId, string handle, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetActorAsync(connection, transaction, user.UserId());
        var removed = await GuardOwnerAsync(() => connection.ExecuteAsync(
            """
            DELETE FROM organization_members m USING users u
            WHERE u.id = m.user_id AND lower(u.handle) = lower(@handle) AND m.organization_id = @organizationId
            """,
            new { organizationId, handle = handle.TrimStart('@') }, transaction));
        await transaction.CommitAsync(cancellationToken);
        return removed == 0
            ? throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "No member has this handle.")
            : Results.NoContent();
    }

    /// <summary>The organization with its members, as the given user sees it.</summary>
    internal static async Task<Organization> LoadAsync(NpgsqlConnection connection, Guid organizationId, Guid viewer)
    {
        var organization = await connection.QuerySingleOrDefaultAsync<(Guid Id, string Name, string Kind, DateTime CreatedAt)>(
            "SELECT id, name, kind, created_at FROM organizations WHERE id = @organizationId", new { organizationId });
        if (organization.Id == Guid.Empty)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "No organization has this id.");
        }

        var members = (await connection.QueryAsync<(Guid UserId, string Handle, string? DisplayName, string Role, DateTime AddedAt)>(
            """
            SELECT u.id, u.handle::text, u.display_name, m.role, m.added_at
            FROM organization_members m JOIN users u ON u.id = m.user_id
            WHERE m.organization_id = @organizationId
            ORDER BY CASE m.role WHEN 'owner' THEN 0 WHEN 'admin' THEN 1 ELSE 2 END, lower(u.handle)
            """,
            new { organizationId })).ToList();
        return new Organization(organization.Id, organization.Name, organization.Kind, organization.CreatedAt,
            members.FirstOrDefault(m => m.UserId == viewer).Role,
            [.. members.Select(m => new Member(m.Handle, m.DisplayName, m.Role, m.AddedAt))]);
    }

    private static async Task<Guid> AccountAsync(NpgsqlConnection connection, string handle, string field) =>
        await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM users WHERE lower(handle) = lower(@handle) AND deleted_at IS NULL", new { handle })
        ?? throw ApiProblemException.Validation(new FieldError(field, "No account has this handle. They need to sign up first."));

    // organization_audit takes its actor from this setting (migration 0037).
    private static Task<int> SetActorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid actor) =>
        connection.ExecuteAsync("SELECT set_config('fandom.actor', @actor, true)", new { actor = actor.ToString() }, transaction);

    private static async Task<int> GuardOwnerAsync(Func<Task<int>> change)
    {
        try
        {
            return await change();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException && e.MessageText.Contains("must keep an owner", StringComparison.Ordinal))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "An owner is needed",
                "An organization must keep an owner. Make someone else an owner first.");
        }
    }

    private static string? Field(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
