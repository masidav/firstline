using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace FandomScore.Worker.ImageRedaction;

/// <summary>A printed order number: where it is (padded, in image pixels) and its normalized value for hashing.</summary>
public sealed record OrderNumber(SKRect Box, string Normalized);

/// <summary>
/// Picks the order, confirmation, and booking numbers out of OCR words. Two rules, both deliberately broad (a box
/// over a harmless number costs nothing; a number left showing is the fan's order on a staff-only archive):
/// <list type="bullet">
/// <item>Any run of digits and capitals of 8 or more characters with at least 5 digits (an order number, a barcode's
/// printed digits, a confirmation code), unless it reads as a date, a time, or a price.</item>
/// <item>The one or two tokens that follow a label such as ORDER, CONFIRMATION, REF, BOOKING, TICKET #, PNR on the
/// same line, when they are 4 or more letters and digits.</item>
/// </list>
/// The normalized value (capitals and digits only) is what <c>submissions.order_ref_hash</c> hashes; the value itself
/// is never stored or logged.
/// </summary>
public static partial class OrderNumberFinder
{
    private static readonly HashSet<string> Labels = new(StringComparer.Ordinal)
    {
        "ORDER", "ORDERNO", "ORDERNUMBER", "ORDERID", "CONFIRMATION", "CONFIRMATIONNO", "CONFIRMATIONNUMBER", "CONF",
        "CONFNO", "REF", "REFERENCE", "REFNO", "BOOKING", "BOOKINGREF", "BOOKINGNO", "TICKETNO", "TICKETNUMBER", "TICKETID",
        "TRANSACTION", "TRANSACTIONID", "RECEIPT", "RECEIPTNO", "INVOICE", "INVOICENO", "PNR", "BARCODE", "SERIAL",
    };

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal) { "NO", "NUMBER", "NUM", "ID", "CODE", "#" };

    public static IReadOnlyList<OrderNumber> Find(IReadOnlyList<OcrWord> words)
    {
        var found = new List<OrderNumber>();
        var byLine = words.GroupBy(w => w.Line).Select(g => g.OrderBy(w => w.Box.Left).ToList());
        foreach (var line in byLine)
        {
            for (var i = 0; i < line.Count; i++)
            {
                var word = line[i];
                var token = Normalize(word.Text);
                if (LooksLikeNumber(word.Text, token))
                {
                    found.Add(new OrderNumber(Pad(word.Box), token));
                    continue;
                }

                if (!Labels.Contains(token))
                {
                    continue;
                }

                // The value is the next token or two, skipping "no", "#", and a second label ("Booking ref").
                var taken = 0;
                for (var j = i + 1; j < line.Count && taken < 2; j++)
                {
                    var next = Normalize(line[j].Text);
                    if (next.Length == 0 || Fillers.Contains(next) || Labels.Contains(next))
                    {
                        continue;
                    }

                    if (next.Length >= 4 && next.Any(char.IsDigit))
                    {
                        found.Add(new OrderNumber(Pad(line[j].Box), next));
                        taken++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        return found.DistinctBy(n => (n.Normalized, n.Box.Left, n.Box.Top)).ToList();
    }

    /// <summary>SHA-256 of the normalized number, as <c>submissions.order_ref_hash</c> stores it.</summary>
    public static byte[] Hash(string normalized) => SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

    /// <summary>Capitals and digits only: "ORD-99887 766" and "ord99887766" are the same order.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
            else if (c == '#')
            {
                builder.Append('#');
            }
        }

        var value = builder.ToString();
        return value == "#" ? "#" : value.Replace("#", "", StringComparison.Ordinal);
    }

    private static bool LooksLikeNumber(string raw, string token)
    {
        if (token.Length < 8 || token.Count(char.IsDigit) < 5 || !token.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
        {
            return false;
        }

        return !(DateLike().IsMatch(raw) || TimeLike().IsMatch(raw) || PriceLike().IsMatch(raw));
    }

    // Grown by a quarter of the text height each way (OCR boxes hug the glyphs), plus a few pixels.
    private static SKRect Pad(SKRect box)
    {
        var pad = (box.Height * 0.25f) + 4;
        return new SKRect(box.Left - pad, box.Top - pad, box.Right + pad, box.Bottom + pad);
    }

    [GeneratedRegex(@"^\d{1,4}[./-]\d{1,2}[./-]\d{1,4}$")]
    private static partial Regex DateLike();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    private static partial Regex TimeLike();

    [GeneratedRegex(@"^[$€£]?\d{1,3}([,.]\d{3})*[.,]\d{2}$")]
    private static partial Regex PriceLike();
}
