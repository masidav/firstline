using Dapper;
using Npgsql;

namespace FandomScore.Api.Features.Fraud;

/// <summary>
/// The client signals the fraud rules read (migration 0054, <c>docs/security/bot-and-shared-device-detection.md</c>):
/// the client address at sign-up and at each registration, kept only as a salted hash for the day, and how often a fan
/// checks their standing for an allocation. The address is passed to the database as a bound parameter (not logged)
/// and never stored; a request with no known address records nothing.
/// </summary>
public static class ClientSignals
{
    public const string SignUp = "signup";
    public const string Registration = "registration";

    public static Task RecordAddressAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, HttpContext http, Guid userId, string kind, Guid? allocationId) =>
        connection.ExecuteAsync("SELECT record_client_address(@userId, @kind, @allocationId, @address)",
            new { userId, kind, allocationId, address = http.Connection.RemoteIpAddress?.ToString() }, transaction);

    public static Task RecordEligibilityViewAsync(NpgsqlConnection connection, Guid userId, Guid allocationId) =>
        connection.ExecuteAsync("SELECT record_eligibility_view(@userId, @allocationId)", new { userId, allocationId });
}
