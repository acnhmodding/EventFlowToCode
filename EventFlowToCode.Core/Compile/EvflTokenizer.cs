using System.Text;
using System.Text.RegularExpressions;

namespace EventFlowToCode.Core.Compile;

internal enum EvflTokenKind
{
    Identifier,
    QuotedIdentifier,
    Integer,
    Float,
    String,
    NewLine,
    Indent,
    Dedent,
    At,
    Colon,
    DoubleColon,
    LeftParen,
    RightParen,
    LeftSquare,
    RightSquare,
    Comma,
    Dot,
    Equals,
    PlusEquals,
    Compare,
    End,
}

internal sealed record EvflToken(EvflTokenKind Kind, string Text, int Line, int Column);

internal static partial class EvflTokenizer
{
    public static List<EvflToken> Tokenize(string source)
    {
        if (source.Contains('\r'))
            source = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        if (source.Length > 0 && source[^1] != '\n')
            source += "\n";

        List<EvflToken> tokens = new(Math.Max(source.Length / 8, 16));
        List<EvflToken> buffered = [];
        Stack<string> indents = new();
        indents.Push(string.Empty);
        var lineNumber = 1;
        var buffering = 0;
        Stack<EvflToken> delimiters = new();

        var lines = source.Split('\n');
        foreach (var rawLine in lines)
        {
            if (lineNumber == lines.Length && rawLine.Length == 0)
                break;

            var line = StripComment(rawLine);
            if (IsBlank(line))
            {
                lineNumber++;
                continue;
            }

            var indentLength = GetIndentLength(line);
            if (delimiters.Count > 0)
            {
                // Continuation indentation does not open or close statement blocks.
                TokenizeLine(line, indentLength, lineNumber, indentLength + 1, tokens, delimiters);
                if (delimiters.Count == 0)
                    tokens.Add(new EvflToken(EvflTokenKind.NewLine, string.Empty, lineNumber, line.Length + 1));
                
                lineNumber++;
                continue;
            }
            var indent = line.AsSpan(0, indentLength);
            var content = line.AsSpan(indentLength);
            var isBlockLabel = StartsWithKeyword(content, "entrypoint")
                || StartsWithKeyword(content, "label");
            var currentIndent = indents.Peek();
            if (isBlockLabel && buffered.Count > 0)
            {
                tokens.AddRange(buffered);
                buffered.Clear();
            }

            if (!isBlockLabel && buffering == 1)
                buffering = 0;

            if (!isBlockLabel && !indent.SequenceEqual(currentIndent))
            {
                if (indent.StartsWith(currentIndent, StringComparison.Ordinal))
                {
                    indents.Push(line[..indentLength]);
                    tokens.Add(new EvflToken(EvflTokenKind.Indent, string.Empty, lineNumber, 1));
                }
                else
                {
                    while (indents.Count > 1 && !indent.SequenceEqual(indents.Peek()))
                    {
                        indents.Pop();
                        tokens.Add(new EvflToken(EvflTokenKind.Dedent, string.Empty, lineNumber, 1));
                    }

                    if (!indent.SequenceEqual(indents.Peek()))
                        throw new InvalidDataException($"Mixed or unknown indentation at line {lineNumber}.");
                }
            }

            if (buffering == 0 && buffered.Count > 0)
            {
                tokens.AddRange(buffered);
                buffered.Clear();
            }

            var target = isBlockLabel ? buffered : tokens;
            if (isBlockLabel)
                buffering = 2;
            

            TokenizeLine(line, indentLength, lineNumber, indentLength + 1, target, delimiters);
            if (delimiters.Count == 0)
                target.Add(new EvflToken(EvflTokenKind.NewLine, string.Empty, lineNumber, line.Length + 1));
            
            if (isBlockLabel)
                buffering = 1;
            
            lineNumber++;
        }

        if (delimiters.TryPeek(out var opening))
            throw new InvalidDataException($"Unclosed '{opening.Text}' at line {opening.Line}, column {opening.Column}.");
        

        if (buffered.Count > 0)
            tokens.AddRange(buffered);

        while (indents.Count > 1)
        {
            indents.Pop();
            tokens.Add(new EvflToken(EvflTokenKind.Dedent, string.Empty, lineNumber, 1));
        }

        tokens.Add(new EvflToken(EvflTokenKind.End, string.Empty, lineNumber, 1));
        return tokens;
    }

    private static void TokenizeLine(string line, int startIndex, int lineNumber, int baseColumn, List<EvflToken> tokens, Stack<EvflToken> delimiters)
    {
        var index = startIndex;
        while (index < line.Length)
        {
            var ch = line[index];
            if (char.IsWhiteSpace(ch))
            {
                index++;
                continue;
            }

            var column = baseColumn + index - startIndex;
            if (char.IsLetter(ch))
            {
                var start = index++;
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '-'))
                {
                    index++;
                }
                tokens.Add(new EvflToken(EvflTokenKind.Identifier, line[start..index], lineNumber, column));
                continue;
            }

            if (ch is '`' or '\'' or '"')
            {
                var start = index++;
                var closed = false;
                while (index < line.Length)
                {
                    var current = line[index++];
                    if (current == '\\')
                    {
                        if (index < line.Length)
                        {
                            index++;
                        }
                    }
                    else if (current == ch)
                    {
                        closed = true;
                        break;
                    }
                }
                if (!closed)
                {
                    throw new InvalidDataException($"Unterminated literal at line {lineNumber}, column {column}.");
                }
                tokens.Add(new EvflToken(ch == '`' ? EvflTokenKind.QuotedIdentifier : EvflTokenKind.String,
                    line[start..index], lineNumber, column));
                continue;
            }

            if (char.IsAsciiDigit(ch) || ch is '+' or '-' or '.')
            {
                var number = NumberRegex().Match(line, index);
                if (number.Success)
                {
                    var text = number.Value.Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Replace("\t", string.Empty, StringComparison.Ordinal);
                    var isFloat = text.IndexOfAny(['.', 'e', 'E']) >= 0;
                    tokens.Add(new EvflToken(isFloat ? EvflTokenKind.Float : EvflTokenKind.Integer, text, lineNumber, column));
                    index += number.Length;
                    continue;
                }
            }

            if (index + 1 < line.Length)
            {
                var next = line[index + 1];
                if (ch == ':' && next == ':')
                {
                    tokens.Add(new EvflToken(EvflTokenKind.DoubleColon, "::", lineNumber, column));
                    index += 2;
                    continue;
                }
                if ((ch is '=' or '!' or '<' or '>') && next == '=')
                {
                    tokens.Add(new EvflToken(EvflTokenKind.Compare, line.Substring(index, 2), lineNumber, column));
                    index += 2;
                    continue;
                }
                if (ch == '+' && next == '=')
                {
                    tokens.Add(new EvflToken(EvflTokenKind.PlusEquals, "+=", lineNumber, column));
                    index += 2;
                    continue;
                }
            }

            var symbol = ch switch
            {
                '@' => new EvflToken(EvflTokenKind.At, "@", lineNumber, column),
                ':' => new EvflToken(EvflTokenKind.Colon, ":", lineNumber, column),
                '(' => new EvflToken(EvflTokenKind.LeftParen, "(", lineNumber, column),
                ')' => new EvflToken(EvflTokenKind.RightParen, ")", lineNumber, column),
                '[' => new EvflToken(EvflTokenKind.LeftSquare, "[", lineNumber, column),
                ']' => new EvflToken(EvflTokenKind.RightSquare, "]", lineNumber, column),
                ',' => new EvflToken(EvflTokenKind.Comma, ",", lineNumber, column),
                '.' => new EvflToken(EvflTokenKind.Dot, ".", lineNumber, column),
                '=' => new EvflToken(EvflTokenKind.Equals, "=", lineNumber, column),
                '<' => new EvflToken(EvflTokenKind.Compare, "<", lineNumber, column),
                '>' => new EvflToken(EvflTokenKind.Compare, ">", lineNumber, column),
                _ => throw new InvalidDataException($"Unexpected character '{ch}' at line {lineNumber}, column {column}."),
            };
            if (symbol.Kind is EvflTokenKind.LeftParen or EvflTokenKind.LeftSquare)
            {
                delimiters.Push(symbol);
            }
            else if (symbol.Kind is EvflTokenKind.RightParen or EvflTokenKind.RightSquare)
            {
                var expected = symbol.Kind == EvflTokenKind.RightParen ? EvflTokenKind.LeftParen : EvflTokenKind.LeftSquare;
                if (!delimiters.TryPop(out var start) || start.Kind != expected)
                {
                    throw new InvalidDataException($"Unmatched '{symbol.Text}' at line {lineNumber}, column {column}.");
                }
            }
            tokens.Add(symbol);
            index++;
        }
    }

    [GeneratedRegex(@"\G[+-]?[ \t]*(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?")]
    private static partial Regex NumberRegex();

    private static bool StartsWithKeyword(ReadOnlySpan<char> content, string keyword) =>
        content.StartsWith(keyword, StringComparison.Ordinal)
        && (content.Length == keyword.Length || char.IsWhiteSpace(content[keyword.Length]));

    private static string StripComment(string line)
    {
        if (line.IndexOf('#') < 0)
            return line;

        var inString = false;
        var quote = '\0';
        StringBuilder result = new();
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inString)
            {
                result.Append(ch);
                if (ch == '\\' && i + 1 < line.Length)
                    result.Append(line[++i]);
                
                else if (ch == quote)
                    inString = false;
            }
            else if (ch is '\'' or '"' or '`')
            {
                inString = true;
                quote = ch;
                result.Append(ch);
            }
            else if (ch == '#')
            {
                break;
            }
            else
            {
                result.Append(ch);
            }
        }

        return result.ToString();
    }

    private static bool IsBlank(string line)
    {
        foreach (var ch in line)
            if (!char.IsWhiteSpace(ch))
                return false;

        return true;
    }

    private static int GetIndentLength(string line)
    {
        var length = 0;
        while (length < line.Length && line[length] is ' ' or '\t')
            length++;

        return length;
    }
}
