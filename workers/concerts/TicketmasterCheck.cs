using FandomScore.Worker.Hosting;

namespace FandomScore.Worker.Concerts;

/// <summary>
/// Is Ticketmaster usable with the configured key? One call to the Discovery API's classifications (the cheapest
/// request), made by <c>GET /healthz/integrations</c> after each deploy. Off when <c>Ticketmaster__ApiKey</c> is unset.
/// </summary>
public sealed class TicketmasterCheck(TicketmasterClient client) : IIntegrationCheck
{
    public string Name => "ticketmaster";

    public async Task<IntegrationStatus> CheckAsync(CancellationToken cancellationToken)
    {
        if (!client.IsEnabled)
        {
            return IntegrationStatus.Off;
        }

        try
        {
            await client.ProbeAsync(cancellationToken);
            return IntegrationStatus.Ok;
        }
        catch (TicketmasterUnavailableException e)
        {
            return new IntegrationStatus(e.KeyRefused ? IntegrationState.Refused : IntegrationState.Unavailable, e.Message);
        }
    }
}
