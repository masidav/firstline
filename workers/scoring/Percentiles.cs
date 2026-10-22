using System.Text.Json;
using FandomScore.Worker.ArtistOnboarding;

namespace FandomScore.Worker.Scoring;

/// <summary>A fan's percentile among an artist's fans, blended with the synthetic baseline.</summary>
public sealed record Percentile(double Value, double BaselineWeight, bool IsEstimated);

/// <summary>
/// Percentiles for one artist: the real fans' empirical distribution blended with the artist's synthetic baseline,
/// <c>p = w * real + (1 - w) * synthetic</c> with <c>w = n / (n + k)</c>. A score counts as an estimate while the
/// baseline carries more than half the weight. Ties share a rank (mid-rank), so equal scores get equal percentiles.
/// </summary>
public static class Percentiles
{
    public static IReadOnlyList<Percentile> Blend(IReadOnlyList<double> scores, Func<double, double> syntheticCdf, int blendK)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(syntheticCdf);
        if (blendK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blendK), "blend_k must be positive.");
        }

        var n = scores.Count;
        var sorted = scores.Order().ToArray();
        var realWeight = (double)n / (n + blendK);
        return [.. scores.Select(score =>
        {
            var below = LowerBound(sorted, score);
            var equal = UpperBound(sorted, score) - below;
            var real = (below + 0.5 * equal) / n;
            var synthetic = Math.Clamp(syntheticCdf(score), 0, 1);
            var value = 100 * (realWeight * real + (1 - realWeight) * synthetic);
            return new Percentile(Math.Round(Math.Clamp(value, 0, 100), 2), Math.Round(1 - realWeight, 3), 1 - realWeight > 0.5);
        })];
    }

    /// <summary>Lognormal CDF (the provisional baseline's family). A score of 0 or less is at 0.</summary>
    public static Func<double, double> Lognormal(double mu, double sigma) =>
        score => score <= 0 ? 0 : ProvisionalBaseline.NormalCdf((Math.Log(score) - mu) / sigma);

    /// <summary>
    /// The stored curve <c>[[score, cdf], ...]</c>, interpolated linearly; flat beyond its ends (0 below the first
    /// point's score, 1 above the last).
    /// </summary>
    public static Func<double, double> Curve(string curveJson)
    {
        var points = JsonSerializer.Deserialize<double[][]>(curveJson)?
            .Where(p => p.Length >= 2)
            .Select(p => (Score: p[0], Cdf: p[1]))
            .OrderBy(p => p.Score)
            .ToArray() ?? [];
        if (points.Length == 0)
        {
            throw new InvalidOperationException("The artist's baseline curve is empty.");
        }

        return score =>
        {
            if (score <= points[0].Score)
            {
                return score < points[0].Score ? 0 : points[0].Cdf;
            }

            if (score >= points[^1].Score)
            {
                return 1;
            }

            var i = Array.FindIndex(points, p => p.Score >= score);
            var (a, b) = (points[i - 1], points[i]);
            return a.Cdf + (b.Cdf - a.Cdf) * (score - a.Score) / (b.Score - a.Score);
        };
    }

    private static int LowerBound(double[] sorted, double value)
    {
        var (lo, hi) = (0, sorted.Length);
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1; else hi = mid;
        }

        return lo;
    }

    private static int UpperBound(double[] sorted, double value)
    {
        var (lo, hi) = (0, sorted.Length);
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] <= value) lo = mid + 1; else hi = mid;
        }

        return lo;
    }
}
