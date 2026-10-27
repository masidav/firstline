using System.Globalization;
using SkiaSharp;

namespace FandomScore.Api.Features.Sharing;

/// <summary>What a score card shows: nothing beyond what the fan chooses to share (no score breakdown, no email).</summary>
public sealed record ScoreCardContent(string ArtistName, decimal Percentile, bool IsEstimated, string? Handle, DateOnly Day);

/// <summary>
/// Draws a 1080×1080 PNG score card ("Top 3% of Geese fans"), square so it fits every share target. Fonts are Lato
/// (SIL Open Font License), embedded because the runtime image has none.
/// </summary>
public static class ScoreCard
{
    public const int Size = 1080;
    private const float Margin = 96;

    private static readonly SKColor Background = SKColor.Parse("#0f1115");
    private static readonly SKColor Surface = SKColor.Parse("#1d1840");
    private static readonly SKColor Accent = SKColor.Parse("#8f82ff");
    private static readonly SKColor Text = SKColor.Parse("#f2f3f5");
    private static readonly SKColor Muted = SKColor.Parse("#9aa1ad");

    private static readonly Lazy<SKTypeface> Regular = new(() => Load("fonts/Lato-Regular.ttf"));
    private static readonly Lazy<SKTypeface> Black = new(() => Load("fonts/Lato-Black.ttf"));

    /// <summary>"Top 3%": 100 minus the percentile, rounded, at least 1 (the app says the same).</summary>
    public static string TopLabel(decimal percentile) =>
        $"Top {Math.Max(1, (int)Math.Round(100 - percentile, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)}%";

    /// <summary>The sentence that goes with the image in a share sheet.</summary>
    public static string ShareText(ScoreCardContent card) =>
        $"I'm in the {TopLabel(card.Percentile).ToLowerInvariant()} of {card.ArtistName} fans on Fandom Score.";

    public static byte[] Render(ScoreCardContent card)
    {
        using var bitmap = new SKBitmap(Size, Size);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(Size, Size), [Surface, Background], SKShaderTileMode.Clamp);
            using var background = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, Size, Size, background);

            using var accent = new SKPaint { Color = Accent, IsAntialias = true };
            using var text = new SKPaint { Color = Text, IsAntialias = true };
            using var muted = new SKPaint { Color = Muted, IsAntialias = true };

            Draw(canvas, "FANDOM SCORE", Margin, Margin + 40, Black.Value, 40, accent);

            // The middle: "Top 3%", then "of fans of", then the artist's name (shrunk or wrapped to fit).
            var top = TopLabel(card.Percentile);
            var topSize = FitSize(top, Black.Value, 260, Size - (2 * Margin));
            Draw(canvas, top, Margin, 470, Black.Value, topSize, accent);
            Draw(canvas, "of fans of", Margin, 590, Regular.Value, 56, text);
            var y = 690f;
            foreach (var line in Wrap(card.ArtistName, Black.Value, 96, Size - (2 * Margin), maxLines: 2))
            {
                Draw(canvas, line.Text, Margin, y + line.Size - 96, Black.Value, line.Size, text);
                y += line.Size + 12;
            }

            if (card.IsEstimated)
            {
                Draw(canvas, "Estimated: few fans of this artist are here yet", Margin, Size - Margin - 70, Regular.Value, 32, muted);
            }

            var footer = card.Handle is { Length: > 0 } handle ? $"@{handle}" : "";
            Draw(canvas, footer, Margin, Size - Margin, Black.Value, 40, text);
            var day = card.Day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
            using var dayFont = new SKFont(Regular.Value, 36);
            canvas.DrawText(day, Size - Margin, Size - Margin, SKTextAlign.Right, dayFont, muted);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static void Draw(SKCanvas canvas, string value, float x, float y, SKTypeface face, float size, SKPaint paint)
    {
        using var font = new SKFont(face, size);
        canvas.DrawText(value, x, y, SKTextAlign.Left, font, paint);
    }

    private static float Width(string value, SKTypeface face, float size)
    {
        using var font = new SKFont(face, size);
        return font.MeasureText(value);
    }

    /// <summary>The largest size up to <paramref name="max"/> at which <paramref name="value"/> fits <paramref name="width"/>.</summary>
    private static float FitSize(string value, SKTypeface face, float max, float width)
    {
        var size = max;
        while (size > 24 && Width(value, face, size) > width)
        {
            size -= 4;
        }

        return size;
    }

    /// <summary>
    /// Words into at most <paramref name="maxLines"/> lines at <paramref name="size"/>; if they still don't fit, the size
    /// shrinks, and a last line too long at the smallest size is cut with an ellipsis.
    /// </summary>
    private static List<(string Text, float Size)> Wrap(string value, SKTypeface face, float size, float width, int maxLines)
    {
        for (var s = size; s >= 48; s -= 6)
        {
            var lines = new List<string>();
            var current = "";
            foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = current.Length == 0 ? word : $"{current} {word}";
                if (Width(next, face, s) <= width || current.Length == 0)
                {
                    current = next;
                }
                else
                {
                    lines.Add(current);
                    current = word;
                }
            }

            lines.Add(current);
            if (lines.Count <= maxLines && lines.All(l => Width(l, face, s) <= width))
            {
                return [.. lines.Select(l => (l, s))];
            }
        }

        var cut = value;
        while (cut.Length > 1 && Width(cut + "…", face, 48) > width)
        {
            cut = cut[..^1];
        }

        return [(cut == value ? value : cut.TrimEnd() + "…", 48)];
    }

    private static SKTypeface Load(string resource)
    {
        using var stream = typeof(ScoreCard).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Missing embedded font {resource}.");
        // Copied into Skia's own buffer: the typeface reads glyphs lazily, after the stream is gone.
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException($"Unreadable font {resource}.");
    }
}
