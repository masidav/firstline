using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Organizations;

/// <summary>
/// The check in front of every <c>/organizations/{organizationId}</c> route: the caller is a member with at least the
/// given role, and the show or allocation the route names belongs to that organization. Someone who isn't a member
/// gets 404, as if the organization didn't exist; a member without the role gets 403.
/// </summary>
public static class OrganizationAccess
{
    private static readonly Dictionary<string, int> Rank = new(StringComparer.Ordinal) { ["analyst"] = 1, ["admin"] = 2, ["owner"] = 3 };

    /// <summary>The roles, least first.</summary>
    public static IReadOnlyList<string> Roles { get; } = ["analyst", "admin", "owner"];

    public static bool IsRole(string? role) => role is not null && Rank.ContainsKey(role);

    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(string role)
    {
        var needed = Rank[role];
        return async (context, next) =>
        {
            var http = context.HttpContext;
            var organizationId = Guid.Parse((string)http.GetRouteValue("organizationId")!);
            var userId = http.User.UserId();
            var db = http.RequestServices.GetRequiredService<NpgsqlDataSource>();
            await using (var connection = await db.OpenConnectionAsync(http.RequestAborted))
            {
                var mine = await connection.ExecuteScalarAsync<string?>(
                    """
                    SELECT m.role FROM organization_members m JOIN users u ON u.id = m.user_id AND u.deleted_at IS NULL
                    WHERE m.organization_id = @organizationId AND m.user_id = @userId
                    """,
                    new { organizationId, userId });
                if (mine is null)
                {
                    throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "No organization has this id.");
                }

                if (Rank[mine] < needed)
                {
                    throw new ApiProblemException(StatusCodes.Status403Forbidden, "forbidden", "Not allowed",
                        $"This needs the {role} role in the organization; your role is {mine}.");
                }

                // A show or allocation of another organization (or one staff manage) doesn't exist here.
                if (http.GetRouteValue("eventId") is string eventId && !await connection.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM events WHERE id = @eventId::uuid AND organization_id = @organizationId)",
                        new { eventId, organizationId }))
                {
                    throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "No show has this id.");
                }

                if (http.GetRouteValue("allocationId") is string allocationId && !await connection.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS (SELECT 1 FROM allocations a JOIN events e ON e.id = a.event_id
                                       WHERE a.id = @allocationId::uuid AND e.organization_id = @organizationId)
                        """,
                        new { allocationId, organizationId }))
                {
                    throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "No allocation has this id.");
                }
            }

            return await next(context);
        };
    }
}
