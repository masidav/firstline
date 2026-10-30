using FandomScore.Tests.Contract;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FandomScore.Tests;

/// <summary>
/// For tests that expect the API to refuse to start: <c>CreateClient()</c> throws the real startup error, every time.
/// </summary>
/// <remarks>
/// WebApplicationFactory runs Program's entry point on its own thread. Once the app has built its host, the test
/// thread's <c>DeferredHost.StartAsync</c> resolves <c>IHostApplicationLifetime</c> from the host's services and waits
/// for the entry point, while the entry point thread carries on into <c>app.Run()</c>. When startup fails,
/// <c>app.Run()</c> disposes the host before the error reaches the test thread; if the test thread is slow (a loaded
/// machine, the full suite in parallel), it then resolves from a disposed provider and the test sees an
/// <c>ObjectDisposedException</c> instead of the startup error. Here the app's start waits until the test thread is
/// already waiting for it, so the startup error always arrives through the factory.
/// </remarks>
public sealed class StartupFailureFactory : ContractCheckedFactory
{
    private readonly ManualResetEventSlim _testThreadWaiting = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IHostLifetime>(new GatedLifetime(_testThreadWaiting))));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = builder.Build();
        Task started;
        try
        {
            // Runs synchronously up to the point where it waits on the entry point, so it is already listening.
            started = host.StartAsync();
        }
        finally
        {
            _testThreadWaiting.Set();
        }

        started.GetAwaiter().GetResult();
        return host;
    }

    public override async ValueTask DisposeAsync()
    {
        _testThreadWaiting.Set();
        await base.DisposeAsync();
        _testThreadWaiting.Dispose();
    }

    /// <summary>Holds the host's start (and so its startup validation) until the test thread is waiting for it.</summary>
    private sealed class GatedLifetime(ManualResetEventSlim gate) : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken)
        {
            if (!gate.Wait(TimeSpan.FromMinutes(1), cancellationToken))
            {
                throw new TimeoutException("The test never started the host.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
