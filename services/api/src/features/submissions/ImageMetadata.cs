using System.Buffers.Binary;

namespace FandomScore.Api.Features.Submissions;

/// <summary>
/// Checks that an upload really is the JPEG or PNG it claims to be, and strips metadata before it is stored: EXIF
/// (GPS location, camera serials, timestamps), XMP, comments, and PNG text chunks. Photos can end up on the public
/// feed, so a fan's location must never ride along. Pixels are untouched. The app re-encodes photos before
/// uploading (which also bakes in the orientation that EXIF would have carried); this is the server's guarantee.
/// </summary>
public static class ImageMetadata
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    // Chunks needed to draw the image (and APNG frames); everything else, including all text and eXIf, is dropped.
    private static readonly HashSet<string> PngKeep =
        ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs", "acTL", "fcTL", "fdAT"];

    /// <summary>The bytes without metadata, or null when they are not a well-formed image of <paramref name="mimeType"/>.</summary>
    public static byte[]? Strip(ReadOnlySpan<byte> data, string mimeType) => mimeType switch
    {
        "image/jpeg" => StripJpeg(data),
        "image/png" => StripPng(data),
        _ => null,
    };

    private static byte[]? StripJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        using var output = new MemoryStream(data.Length);
        output.Write([0xFF, 0xD8]);
        var i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF)
            {
                return null;
            }

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if (length < 2 || i + 2 + length > data.Length)
            {
                return null;
            }

            if (marker == 0xDA)
            {
                // Start of scan: the rest is image data up to the end marker. Copy it as is.
                output.Write(data[i..]);
                return output.ToArray();
            }

            // Keep APP0 (JFIF) and APP14 (Adobe, needed for colour); drop APP1–APP13, APP15 (EXIF, XMP, ICC,
            // maker notes) and COM. Keep every non-metadata segment (tables, frame headers).
            var isApp = marker is >= 0xE0 and <= 0xEF;
            var keep = isApp ? marker is 0xE0 or 0xEE : marker != 0xFE;
            if (keep)
            {
                output.Write(data.Slice(i, 2 + length));
            }

            i += 2 + length;
        }

        return null; // no image data
    }

    private static byte[]? StripPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < PngSignature.Length || !data[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return null;
        }

        using var output = new MemoryStream(data.Length);
        output.Write(PngSignature);
        var i = PngSignature.Length;
        var sawEnd = false;
        while (i + 12 <= data.Length && !sawEnd)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
            if (length > int.MaxValue || i + 12 + (long)length > data.Length)
            {
                return null;
            }

            var type = System.Text.Encoding.ASCII.GetString(data.Slice(i + 4, 4));
            var size = 12 + (int)length;
            if (PngKeep.Contains(type))
            {
                output.Write(data.Slice(i, size));
            }

            sawEnd = type == "IEND";
            i += size;
        }

        return sawEnd ? output.ToArray() : null;
    }
}
