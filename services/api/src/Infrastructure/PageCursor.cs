using System.Globalization;
using System.Text;

namespace FandomScore.Api.Infrastructure;

/// <summary>
/// The opaque <c>cursor</c> / <c>next_cursor</c> of list endpoints: an offset, base64-encoded. Fine while lists are
/// small; switch to keyset cursors if a list grows large.
/// </summary>
public static class PageCursor
{
    public static string Write(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    public static bool TryRead(string cursor, out int offset)
    {
        offset = 0;
        try
        {
            return int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), NumberStyles.None,
                CultureInfo.InvariantCulture, out offset);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
