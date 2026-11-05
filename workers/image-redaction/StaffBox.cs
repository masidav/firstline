using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace FandomScore.Worker.ImageRedaction;

/// <summary>
/// A box staff drew over a file when archiving it (<c>submission_files.staff_boxes</c>, migration 0056), as fractions
/// of the image from its top-left corner, so it means the same thing whatever size the image was shown at. The
/// database checks the shape (1..20 boxes, each inside the image); the API validates the decision before storing it.
/// </summary>
public sealed record StaffBox(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double W,
    [property: JsonPropertyName("h")] double H)
{
    /// <summary>The boxes in a <c>staff_boxes</c> column value; none when it is null or empty.</summary>
    public static IReadOnlyList<StaffBox> Parse(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<StaffBox>>(json) ?? [];

    /// <summary>
    /// The box in a <paramref name="width"/>×<paramref name="height"/> image's pixels, rounded outward and grown by
    /// <paramref name="pad"/> pixels on each side so a tight box still covers the glyphs at its edges.
    /// </summary>
    public SKRect ToPixels(int width, int height, float pad = 2)
    {
        var left = Math.Clamp((float)Math.Floor(X * width) - pad, 0, width);
        var top = Math.Clamp((float)Math.Floor(Y * height) - pad, 0, height);
        var right = Math.Clamp((float)Math.Ceiling((X + W) * width) + pad, 0, width);
        var bottom = Math.Clamp((float)Math.Ceiling((Y + H) * height) + pad, 0, height);
        return new SKRect(left, top, right, bottom);
    }
}
