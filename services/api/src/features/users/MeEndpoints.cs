using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Npgsql;

namespace FandomScore.Api.Features.Users;

/// <summary>openapi tag Me: the signed-in user's profile and account deletion.</summary>
public static class MeEndpoints
{
    public static void MapMe(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me", GetAsync);
        v1.MapPatch("/me", UpdateAsync);
        v1.MapDelete("/me", DeleteAsync);
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadAsync(connection, user.UserId()));
    }

    // MeUpdate: a missing field is left alone; display_name and avatar_upload_id accept null to clear.
    private static async Task<IResult> UpdateAsync(
        ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
        }

        var sets = new List<string>();
        var parameters = new DynamicParameters(new { id = user.UserId() });
        var errors = new List<FieldError>();

        if (body.TryGetProperty("handle", out var handle))
        {
            if (handle.ValueKind == JsonValueKind.String && UserProfiles.HandlePattern().IsMatch(handle.GetString()!))
            {
                sets.Add("handle = @handle");
                parameters.Add("handle", handle.GetString());
            }
            else
            {
                errors.Add(new FieldError("handle", UserProfiles.HandleRule));
            }
        }

        if (body.TryGetProperty("display_name", out var displayName))
        {
            // Rune count matches PostgreSQL char_length in the users_display_name_length constraint.
            if (displayName.ValueKind == JsonValueKind.Null)
            {
                sets.Add("display_name = NULL");
            }
            else if (displayName.ValueKind == JsonValueKind.String && displayName.GetString() is { } name
                     && !string.IsNullOrWhiteSpace(name) && name.EnumerateRunes().Count() <= UserProfiles.DisplayNameMaxLength)
            {
                sets.Add("display_name = @display_name");
                parameters.Add("display_name", name);
            }
            else
            {
                errors.Add(new FieldError("display_name",
                    $"Use 1 to {UserProfiles.DisplayNameMaxLength} characters, or null to clear it."));
            }
        }

        if (body.TryGetProperty("avatar_upload_id", out var avatar))
        {
            // Uploads (POST /uploads) arrive with object storage; until then avatars can only be cleared.
            if (avatar.ValueKind == JsonValueKind.Null)
            {
                sets.Add("avatar_url = NULL");
            }
            else
            {
                errors.Add(new FieldError("avatar_upload_id", "Avatar uploads are not available yet."));
            }
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (sets.Count > 0)
        {
            try
            {
                await connection.ExecuteAsync(
                    $"UPDATE users SET {string.Join(", ", sets)} WHERE id = @id AND deleted_at IS NULL", parameters);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "users_handle_key")
            {
                throw UserProfiles.HandleTaken();
            }
        }

        return Results.Ok(await LoadAsync(connection, user.UserId()));
    }

    public sealed record DeleteMeRequest(string? AppleAuthorizationCode);

    // The users_delete triggers scrub personal fields, Spotify and library data, sessions, and sign-in
    // identities in the same statement. The worker purges the account's content 30 days later
    // (workers/account-retention).
    // Apple sign-ins are then revoked with Apple, as the App Store requires.
    private static async Task<IResult> DeleteAsync(
        ClaimsPrincipal user, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteMeRequest? request, NpgsqlDataSource db, AppleClient apple, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        // Read the Apple identity first: deletion removes it.
        var appleSubject = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT subject FROM auth_identities WHERE provider = 'apple' AND user_id = @id", new { id = user.UserId() });
        await connection.ExecuteAsync(
            "UPDATE users SET deleted_at = now() WHERE id = @id AND deleted_at IS NULL", new { id = user.UserId() });

        if (appleSubject is not null)
        {
            await apple.RevokeAsync(request?.AppleAuthorizationCode, appleSubject, cancellationToken);
        }

        return Results.Accepted();
    }

    private static async Task<MeResponse> LoadAsync(NpgsqlConnection connection, Guid userId) =>
        await UserProfiles.LoadMeAsync(connection, userId)
        ?? throw ApiProblemException.Unauthorized("The account no longer exists.");
}
