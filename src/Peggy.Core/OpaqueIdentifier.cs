using System.Text;

namespace Peggy.Core;

public static class OpaqueIdentifier
{
    public static string Encode(string prefix, string value)
        => prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static bool TryDecode(
        string opaqueValue,
        string prefix,
        out string value)
    {
        value = string.Empty;
        if (!opaqueValue.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = opaqueValue[prefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(
            encoded.Length + ((4 - encoded.Length % 4) % 4),
            '=');
        try
        {
            value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return value.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
