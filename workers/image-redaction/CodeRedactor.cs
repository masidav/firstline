using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.SkiaSharp;

namespace FandomScore.Worker.ImageRedaction;

/// <summary>What <see cref="CodeRedactor.Redact"/> did to an image.</summary>
/// <param name="Content">The redacted image (re-encoded in the same format), or null when it couldn't be read.</param>
/// <param name="CodesRedacted">Distinct codes (format and content) found and blacked out, over every pass.</param>
/// <param name="CodesRemaining">Distinct codes a final scan still found. Zero does not prove the image is clean.</param>
public sealed record RedactionResult(byte[]? Content, int CodesRedacted, int CodesRemaining)
{
    public bool Unreadable => Content is null;
}

/// <summary>
/// Finds barcodes and QR codes (every format ZXing reads: QR, Aztec, Data Matrix, PDF417, MaxiCode, and the 1D codes)
/// and paints a padded black box over each, then scans again, up to <see cref="MaxPasses"/> times.
/// <para>
/// ZXing finds a code reliably only when it is roughly upright and, for Aztec and Data Matrix, near the middle of what
/// it scans. So each pass scans the whole image, overlapping sections of it, and copies turned by ±22°, ±45°, ±68°
/// and 90°. A scan that finds nothing is not proof: a blurred, cropped, or tiny code can still be missed, which is why
/// tickets and screenshots with no code found go to staff (<see cref="RedactSubmissionJob"/>).
/// </para>
/// Printed digits under a barcode are covered by the box's padding when they sit just below it; text elsewhere (an
/// order number in the corner) is not detected: there is no OCR.
/// </summary>
public static class CodeRedactor
{
    public const int MaxPasses = 3;

    /// <summary>Images are scanned at most this many pixels on the long side (boxes are mapped back to full size).</summary>
    private const int ScanSide = 1600;

    private const int JpegQuality = 90;

    private static readonly float[] Angles = [0f, 22f, -22f, 45f, -45f, 68f, -68f, 90f];

    private static readonly List<BarcodeFormat> AllFormats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.AZTEC, BarcodeFormat.DATA_MATRIX, BarcodeFormat.PDF_417, BarcodeFormat.MAXICODE,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.CODABAR, BarcodeFormat.ITF,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E, BarcodeFormat.RSS_14,
        BarcodeFormat.RSS_EXPANDED,
    ];

    // Aztec and Data Matrix detectors look outward from the middle of the area they're given, so they get a denser
    // set of small sections of their own.
    private static readonly List<BarcodeFormat> CenteredFormats = [BarcodeFormat.AZTEC, BarcodeFormat.DATA_MATRIX];

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear);

    public static RedactionResult Redact(byte[] content, string mimeType)
    {
        using var bitmap = TryDecode(content);
        if (bitmap is null)
        {
            return new RedactionResult(null, 0, 0);
        }

        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
        var redacted = new HashSet<string>(StringComparer.Ordinal);
        var found = Find(bitmap);
        for (var pass = 0; pass < MaxPasses && found.Count > 0; pass++)
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                foreach (var code in found)
                {
                    canvas.DrawRect(code.Box, paint);
                }
            }

            redacted.UnionWith(found.Select(c => c.Code));
            found = Find(bitmap);
        }

        var format = mimeType == "image/png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, JpegQuality);
        return new RedactionResult(data.ToArray(), redacted.Count, found.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The decoded image, or null when the bytes aren't an image Skia can read.</summary>
    public static SKBitmap? TryDecode(byte[] content)
    {
        using var stream = new SKMemoryStream(content);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width == 0 || codec.Info.Height == 0)
        {
            return null;
        }

        var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null || bitmap.Width == 0)
        {
            bitmap?.Dispose();
            return null;
        }

        return bitmap;
    }

    /// <summary>A found code: a padded box in the original image's pixels, and the code's format and content.</summary>
    public sealed record FoundCode(SKRect Box, string Code);

    /// <summary>Every code found; one code is usually found several times (sections overlap, turned copies).</summary>
    public static List<FoundCode> Find(SKBitmap original)
    {
        var scale = Math.Min(1f, ScanSide / (float)Math.Max(original.Width, original.Height));
        using var resized = scale < 1
            ? original.Resize(new SKImageInfo((int)(original.Width * scale), (int)(original.Height * scale)), Sampling)
            : null;
        var image = resized ?? original;
        var all = Reader(AllFormats);
        var centered = Reader(CenteredFormats);
        var boxes = new List<FoundCode>();
        using var source = SKImage.FromBitmap(image);

        foreach (var angle in Angles)
        {
            using var turned = angle == 0 ? null : new SKBitmap(image.Width, image.Height);
            var back = SKMatrix.Identity;
            if (turned is not null)
            {
                var turn = SKMatrix.CreateRotationDegrees(angle, image.Width / 2f, image.Height / 2f);
                using var canvas = new SKCanvas(turned);
                canvas.Clear(SKColors.White);
                canvas.SetMatrix(turn);
                canvas.DrawImage(source, 0, 0, Sampling);
                turn.TryInvert(out back);
            }

            var view = turned ?? image;
            Scan(view, back, scale, 1, all, boxes);
            Scan(view, back, scale, 2, all, boxes);
            if (angle == 0)
            {
                Scan(view, back, scale, 3, all, boxes);
                Scan(view, back, scale, 5, centered, boxes);
                Scan(view, back, scale, 7, centered, boxes);
            }
        }

        return boxes;
    }

    private static BarcodeReader Reader(List<BarcodeFormat> formats) =>
        new() { Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = formats } };

    /// <summary>Scans a grid×grid set of half-overlapping sections of <paramref name="view"/>.</summary>
    private static void Scan(SKBitmap view, SKMatrix back, float scale, int grid, BarcodeReader reader, List<FoundCode> boxes)
    {
        var width = grid == 1 ? view.Width : (int)(view.Width * 2f / (grid + 1));
        var height = grid == 1 ? view.Height : (int)(view.Height * 2f / (grid + 1));
        for (var gx = 0; gx < grid; gx++)
        {
            for (var gy = 0; gy < grid; gy++)
            {
                var section = SKRectI.Create(gx * width / 2, gy * height / 2, width, height);
                // A copy, not a view: ZXing reads the pixels as if each row ended at the section's edge.
                using var subset = new SKBitmap();
                if (!view.ExtractSubset(subset, section))
                {
                    continue;
                }

                using var tile = subset.Copy();
                foreach (var result in reader.DecodeMultiple(tile) ?? [])
                {
                    var points = result.ResultPoints.Where(p => p is not null)
                        .Select(p => back.MapPoint(p.X + section.Left, p.Y + section.Top)).ToList();
                    if (points.Count > 0)
                    {
                        boxes.Add(new FoundCode(Box(points, result.BarcodeFormat, scale), $"{result.BarcodeFormat}:{result.Text}"));
                    }
                }
            }
        }
    }

    /// <summary>
    /// A box around the code's reported points, padded generously: QR points are finder-pattern centres, not corners,
    /// and a 1D code reports only a line through its bars (so its box grows up and down to cover the bars and the
    /// digits printed under them).
    /// </summary>
    private static SKRect Box(List<SKPoint> points, BarcodeFormat format, float scale)
    {
        float left = points.Min(p => p.X), right = points.Max(p => p.X), top = points.Min(p => p.Y), bottom = points.Max(p => p.Y);
        var size = Math.Max(right - left, bottom - top);
        var linear = format is not (BarcodeFormat.QR_CODE or BarcodeFormat.AZTEC or BarcodeFormat.DATA_MATRIX
            or BarcodeFormat.PDF_417 or BarcodeFormat.MAXICODE);
        var pad = (size * 0.3f) + 8;
        var padAcross = linear ? (size * 0.45f) + 8 : pad;
        // A 1D code read at an angle reports a slanted line; pad both ways by the larger amount.
        var padX = linear ? Math.Max(pad, padAcross) : pad;
        return new SKRect((left - padX) / scale, (top - padAcross) / scale, (right + padX) / scale, (bottom + padAcross) / scale);
    }
}
