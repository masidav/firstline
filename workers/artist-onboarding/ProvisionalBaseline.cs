using System.Text.Json;

namespace FandomScore.Worker.ArtistOnboarding;

/// <summary>
/// Configuration section <c>Baseline</c>: the synthetic prior every new artist starts with. These are
/// provisional defaults until the baseline parameters are decided (open decision in PROJECT_TODO.md).
/// </summary>
public sealed class BaselineOptions
{
    public const string Section = "Baseline";

    /// <summary>Raw score of the median fan under the prior (about ten liked tracks at 0.5 points each).</summary>
    public double MedianScore { get; set; } = 5;

    /// <summary>Spread of log(score): 1.0 puts about two thirds of fans within a factor e of the median.</summary>
    public double Sigma { get; set; } = 1;

    /// <summary>Real fans needed before real data carries half the weight (w = n / (n + k)).</summary>
    public int BlendK { get; set; } = 200;
}

/// <summary>
/// The lognormal prior stored in <c>artist_baselines</c> when an artist is activated. The curve is
/// [[score, cdf], ...] for the scoring worker to look percentiles up in; dist_params lets it be regenerated.
/// Spotify no longer exposes followers or popularity, so the only proxy metrics are the catalog size (ADR 0006).
/// </summary>
public static class ProvisionalBaseline
{
    public sealed record Baseline(string DistParams, string ProxyMetrics, string Curve, int BlendK);

    public static Baseline Create(BaselineOptions options, CatalogLoad catalog, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        if (options.MedianScore <= 0 || options.Sigma <= 0 || options.BlendK <= 0)
        {
            throw new InvalidOperationException("Baseline:MedianScore, Baseline:Sigma, and Baseline:BlendK must be positive.");
        }

        var mu = Math.Log(options.MedianScore);
        // 61 points from -3σ to +3σ: enough for a percentile lookup by interpolation.
        var curve = Enumerable.Range(-30, 61).Select(i =>
        {
            var z = i / 10.0;
            return new[] { Math.Round(Math.Exp(mu + options.Sigma * z), 3), Math.Round(NormalCdf(z), 5) };
        }).ToArray();

        return new Baseline(
            JsonSerializer.Serialize(new { mu, sigma = options.Sigma, provisional = true, source = "defaults" }),
            JsonSerializer.Serialize(new { source = "spotify", albums = catalog.Albums, tracks = catalog.Tracks, loaded_at = now }),
            JsonSerializer.Serialize(curve),
            options.BlendK);
    }

    /// <summary>Standard normal CDF (Abramowitz and Stegun 7.1.26, error under 1e-7).</summary>
    public static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * x);
        var poly = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));
        var erf = 1 - poly * Math.Exp(-x * x);
        return 0.5 * (1 + (z < 0 ? -erf : erf));
    }
}
