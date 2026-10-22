using System.Globalization;
using System.Text;

namespace EventFlowToCode.Core.Compile;

internal static class EvflStringLiteral
{
    public static string Decode(EvflToken token)
    {
        var text = token.Text;
        StringBuilder result = new(text.Length - 2);
        for (var index = 1; index < text.Length - 1; index++)
        {
            var ch = text[index];
            if (ch != '\\')
            {
                result.Append(ch);
                continue;
            }
            var escape = text[++index];
            switch (escape)
            {
                case '\\': case '\'': case '"': result.Append(escape); break;
                case 'a': result.Append('\a'); break;
                case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break;
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 't': result.Append('\t'); break;
                case 'v': result.Append('\v'); break;
                case 'x':
                case 'u':
                case 'U':
                    var digits = escape == 'x' ? 2 : escape == 'u' ? 4 : 8;
                    if (index + digits >= text.Length - 1
                        || !uint.TryParse(text.AsSpan(index + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var scalar)
                        || scalar > 0x10FFFF || scalar is >= 0xD800 and <= 0xDFFF)
                    {
                        throw Error(token, index, $"Invalid \\{escape} escape");
                    }
                    result.Append(char.ConvertFromUtf32((int)scalar));
                    index += digits;
                    break;
                case >= '0' and <= '7':
                    var octal = escape - '0';
                    for (var count = 1; count < 3 && index + 1 < text.Length - 1 && text[index + 1] is >= '0' and <= '7'; count++)
                    {
                        octal = octal * 8 + text[++index] - '0';
                    }
                    result.Append((char)octal);
                    break;
                case 'N':
                    throw Error(token, index, "Named Unicode escapes are not supported; use \\u or \\U");
                default:
                    // Python retains an unrecognized escape, including its backslash.
                    result.Append('\\').Append(escape);
                    break;
            }
        }
        return result.ToString();
    }

    private static InvalidDataException Error(EvflToken token, int index, string message) =>
        new($"{message} at line {token.Line}, column {token.Column + index - 1}.");
}
