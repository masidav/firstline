using System.Net;
using System.Text;
using FandomScore.Worker.Concerts;
using FandomScore.Worker.Hosting;
using Microsoft.Extensions.Options;

namespace FandomScore.Tests;

/// <summary>The Ticketmaster integration check behind GET /healthz/integrations: one cheap call tells a working key from a wrong one.</summary>
public sealed class TicketmasterCheckTests
{
    [Fact]
    public async Task Off_without_a_key_and_makes_no_request()
    {
        var handler = new Handler(HttpStatusCode.OK);
        var status = await new TicketmasterCheck(Client(handler, apiKey: " ")).CheckAsync(CancellationToken.None);

        Assert.Equal(IntegrationState.Off, status.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Ok_when_the_classifications_call_succeeds()
    {
        var handler = new Handler(HttpStatusCode.OK);
        var status = await new TicketmasterCheck(Client(handler)).CheckAsync(CancellationToken.None);

        Assert.Equal(IntegrationState.Ok, status.State);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/discovery/v2/classifications.json", request.AbsolutePath);
        Assert.Contains("apikey=test-key", request.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refused_when_Ticketmaster_rejects_the_key(HttpStatusCode code)
    {
        var status = await new TicketmasterCheck(Client(new Handler(code))).CheckAsync(CancellationToken.None);

        Assert.Equal(IntegrationState.Refused, status.State);
        Assert.Contains("refused the API key", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Unavailable_on_rate_limits_and_server_errors(HttpStatusCode code)
    {
        var status = await new TicketmasterCheck(Client(new Handler(code))).CheckAsync(CancellationToken.None);

        Assert.Equal(IntegrationState.Unavailable, status.State);
        Assert.NotNull(status.Detail);
    }

    [Fact]
    public async Task Unavailable_when_Ticketmaster_cannot_be_reached()
    {
        var status = await new TicketmasterCheck(Client(new Handler(null))).CheckAsync(CancellationToken.None);

        Assert.Equal(IntegrationState.Unavailable, status.State);
        Assert.Equal("Ticketmaster could not be reached.", status.Detail);
    }

    private static TicketmasterClient Client(Handler handler, string apiKey = "test-key") =>
        new(new HttpClient(handler), Options.Create(new TicketmasterOptions { ApiKey = apiKey }));

    /// <summary>Answers every request with one status; a null status throws like a connection failure.</summary>
    private sealed class Handler(HttpStatusCode? status) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (status is null)
            {
                throw new HttpRequestException("connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(status.Value)
            {
                Content = new StringContent("""{"_embedded":{"classifications":[]},"page":{"totalPages":1}}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
