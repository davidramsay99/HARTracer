namespace HarLens.Core.Curl;

/// <summary>
/// Splits a POSIX shell command line into words: single quotes, double quotes, ANSI-C <c>$'...'</c> quoting,
/// backslash escapes and backslash-newline continuation. Expansions (<c>$VAR</c>, <c>$(...)</c>, backticks) are
/// not performed; they are kept literally with a warning. Unquoted shell operators end the command.
/// </summary>
internal static class BashLexer
{
    public static List<ShellWord> Lex(string text, WarningSink warnings)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var words = new List<ShellWord>();
        var word = new WordBuilder();
        var lineBreak = false;
        var n = text.Length;
        var i = 0;

        void EndWord()
        {
            if (word.Started)
            {
                words.Add(new ShellWord(word.Take(), lineBreak && words.Count > 0));
                lineBreak = false;
            }
        }

        while (i < n)
        {
            var c = text[i];
            switch (c)
            {
                case ' ':
                case '\t':
                    EndWord();
                    i++;
                    break;
                case '\n':
                    EndWord();
                    lineBreak = true;
                    i++;
                    break;
                case '\\':
                    if (ShellLexing.IsContinuation(text, i + 1, out var afterContinuation))
                    {
                        i = afterContinuation;
                    }
                    else
                    {
                        word.Append(text[i + 1]);
                        i += 2;
                    }

                    break;
                case '\'':
                    i = ReadSingleQuoted(text, i + 1, word, warnings);
                    break;
                case '"':
                    i = ReadDoubleQuoted(text, i + 1, word, warnings);
                    break;
                case '$' when i + 1 < n && text[i + 1] == '\'':
                    i = ReadAnsiC(text, i + 2, word, warnings);
                    break;
                case '$' when i + 1 < n && text[i + 1] == '"':
                    // $"..." is a locale-translated string; without a translation it is a plain double-quoted string.
                    i = ReadDoubleQuoted(text, i + 2, word, warnings);
                    break;
                case '$':
                    WarnExpansion(text, i, warnings);
                    word.Append('$');
                    i++;
                    break;
                case '`':
                    warnings.Add("A backtick command substitution was not run; the text was kept literally.");
                    word.Append('`');
                    i++;
                    break;
                case '#' when !word.Started:
                    while (i < n && text[i] != '\n')
                    {
                        i++;
                    }

                    break;
                case ';':
                case '&':
                case '|':
                case '<':
                case '>':
                    EndWord();
                    warnings.Add($"Everything after the unquoted shell operator '{c}' was ignored.");
                    i = n;
                    break;
                default:
                    word.Append(c);
                    i++;
                    break;
            }
        }

        EndWord();
        return words;
    }

    private static int ReadSingleQuoted(string text, int i, WordBuilder word, WarningSink warnings)
    {
        word.Start();
        var end = text.IndexOf('\'', i);
        if (end < 0)
        {
            warnings.Add("A single-quoted string is not terminated; it was read to the end of the command.");
            word.Append(text[i..]);
            return text.Length;
        }

        word.Append(text[i..end]);
        return end + 1;
    }

    private static int ReadDoubleQuoted(string text, int i, WordBuilder word, WarningSink warnings)
    {
        word.Start();
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '"')
            {
                return i + 1;
            }

            if (c == '\\' && i + 1 < n)
            {
                var d = text[i + 1];
                if (d is '$' or '`' or '"' or '\\')
                {
                    word.Append(d);
                    i += 2;
                    continue;
                }

                if (d == '\n')
                {
                    i += 2;
                    continue;
                }

                word.Append('\\');
                i++;
                continue;
            }

            if (c == '$')
            {
                WarnExpansion(text, i, warnings);
            }
            else if (c == '`')
            {
                warnings.Add("A backtick command substitution was not run; the text was kept literally.");
            }

            word.Append(c);
            i++;
        }

        warnings.Add("A double-quoted string is not terminated; it was read to the end of the command.");
        return n;
    }

    /// <summary>Reads the body of <c>$'...'</c> starting after the opening quote.</summary>
    private static int ReadAnsiC(string text, int i, WordBuilder word, WarningSink warnings)
    {
        word.Start();
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '\'')
            {
                return i + 1;
            }

            if (c != '\\' || i + 1 >= n)
            {
                word.Append(c);
                i++;
                continue;
            }

            var d = text[i + 1];
            i += 2;
            switch (d)
            {
                case 'a': word.Append('\a'); break;
                case 'b': word.Append('\b'); break;
                case 'e':
                case 'E': word.Append('\u001b'); break;
                case 'f': word.Append('\f'); break;
                case 'n': word.Append('\n'); break;
                case 'r': word.Append('\r'); break;
                case 't': word.Append('\t'); break;
                case 'v': word.Append('\v'); break;
                case '\\': word.Append('\\'); break;
                case '\'': word.Append('\''); break;
                case '"': word.Append('"'); break;
                case '?': word.Append('?'); break;
                case 'x':
                {
                    var value = ReadHex(text, ref i, 2, out var digits);
                    if (digits == 0)
                    {
                        word.Append("\\x");
                    }
                    else
                    {
                        word.AppendByte((byte)value);
                    }

                    break;
                }

                case 'u':
                case 'U':
                {
                    var value = ReadHex(text, ref i, d == 'u' ? 4 : 8, out var digits);
                    if (digits == 0)
                    {
                        word.Append('\\');
                        word.Append(d);
                    }
                    else
                    {
                        word.AppendCodePoint(value);
                    }

                    break;
                }

                case >= '0' and <= '7':
                {
                    var value = d - '0';
                    for (var k = 0; k < 2 && i < n && text[i] is >= '0' and <= '7'; k++)
                    {
                        value = (value * 8) + (text[i] - '0');
                        i++;
                    }

                    word.AppendByte((byte)(value & 0xFF));
                    break;
                }

                case 'c' when i < n:
                    word.Append((char)(text[i] & 0x1F));
                    i++;
                    break;
                default:
                    word.Append('\\');
                    word.Append(d);
                    break;
            }
        }

        warnings.Add("A $'...' string is not terminated; it was read to the end of the command.");
        return n;
    }

    private static int ReadHex(string text, ref int i, int maxDigits, out int digits)
    {
        var value = 0;
        digits = 0;
        while (digits < maxDigits && i < text.Length && CurlText.IsHexDigit(text[i]))
        {
            value = (value * 16) + CurlText.HexValue(text[i]);
            i++;
            digits++;
        }

        return value;
    }

    private static void WarnExpansion(string text, int i, WarningSink warnings)
    {
        var name = ShellLexing.ExpansionAt(text, i, allowColon: false);
        if (name is not null)
        {
            warnings.Add($"The shell expansion '{name}' was not performed; the text was kept literally.");
        }
    }
}

/// <summary>Helpers shared by the lexers.</summary>
internal static class ShellLexing
{
    /// <summary>
    /// True when position <paramref name="i"/> (just after a continuation character) is followed by optional
    /// blanks and then a line feed or the end of the text. Trailing blanks after the continuation character are
    /// tolerated because they are invisible and often added by copy and paste.
    /// </summary>
    public static bool IsContinuation(string text, int i, out int next)
    {
        var j = i;
        while (j < text.Length && text[j] is ' ' or '\t')
        {
            j++;
        }

        if (j >= text.Length)
        {
            next = text.Length;
            return true;
        }

        if (text[j] == '\n')
        {
            next = j + 1;
            return true;
        }

        next = i;
        return false;
    }

    /// <summary>Describes the variable or substitution starting at a <c>$</c>, or null when the dollar sign is literal.</summary>
    public static string? ExpansionAt(string text, int i, bool allowColon)
    {
        if (i + 1 >= text.Length)
        {
            return null;
        }

        var c = text[i + 1];
        if (c == '(')
        {
            return "$(...)";
        }

        if (c == '{')
        {
            var close = text.IndexOf('}', i + 2);
            return close < 0 ? "${...}" : text[i..(close + 1)];
        }

        if (char.IsAsciiLetter(c) || c == '_')
        {
            var j = i + 1;
            while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '_' || (allowColon && text[j] == ':')))
            {
                j++;
            }

            return text[i..j];
        }

        return char.IsAsciiDigit(c) || c is '?' or '#' or '@' or '*' or '!' or '$' or '-' ? text[i..(i + 2)] : null;
    }
}
