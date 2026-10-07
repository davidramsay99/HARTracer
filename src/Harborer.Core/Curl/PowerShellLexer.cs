namespace Harborer.Core.Curl;

/// <summary>
/// Splits a PowerShell command that calls curl.exe into the arguments PowerShell 7.3 and later pass to it:
/// backtick line continuation; single quotes with doubled quotes for a literal quote; double quotes with backtick
/// escapes and doubled quotes; backtick escapes in bare words. Typographic quotes count as quotes, as in PowerShell.
/// Variables and subexpressions are not evaluated; they are kept literally with a warning.
/// </summary>
internal static class PowerShellLexer
{
    public static bool IsSingleQuote(char c) => c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B';

    public static bool IsDoubleQuote(char c) => c is '"' or '\u201C' or '\u201D' or '\u201E';

    public static List<ShellWord> Lex(string text, WarningSink warnings)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var words = new List<ShellWord>();
        var word = new WordBuilder();
        var lineBreak = false;
        var wordQuoted = false;
        var n = text.Length;
        var i = 0;

        bool EndWord()
        {
            if (!word.Started)
            {
                return false;
            }

            var value = word.Take();
            if (!wordQuoted && value == "--%")
            {
                // Stop-parsing token: the rest of the line goes to curl.exe verbatim and the C runtime splits it.
                warnings.Add("The PowerShell stop-parsing token --% was found; the rest of the line was split with Windows C runtime rules.");
                var end = text.IndexOf('\n', i);
                var rest = end < 0 ? text[i..] : text[i..end];
                words.AddRange(CmdLexer.SplitCrt(rest, null, firstIsProgram: false));
                i = end < 0 ? n : end;
                wordQuoted = false;
                return true;
            }

            words.Add(new ShellWord(value, lineBreak && words.Count > 0));
            lineBreak = false;
            wordQuoted = false;
            return true;
        }

        while (i < n)
        {
            var c = text[i];
            if (c is ' ' or '\t')
            {
                i++;
                EndWord();
                continue;
            }

            if (c == '\n')
            {
                EndWord();
                lineBreak = true;
                i++;
                continue;
            }

            if (c == '`')
            {
                if (ShellLexing.IsContinuation(text, i + 1, out var next))
                {
                    i = next;
                    EndWord();
                }
                else
                {
                    i = AppendEscape(text, i + 1, word);
                }

                continue;
            }

            if (IsSingleQuote(c))
            {
                wordQuoted = true;
                i = ReadSingleQuoted(text, i + 1, word, warnings);
                continue;
            }

            if (IsDoubleQuote(c))
            {
                wordQuoted = true;
                i = ReadDoubleQuoted(text, i + 1, word, warnings);
                continue;
            }

            if (c == '$')
            {
                WarnExpansion(text, i, warnings);
                word.Append(c);
                i++;
                continue;
            }

            if (c == '#' && !word.Started)
            {
                while (i < n && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '@' && !word.Started)
            {
                warnings.Add("PowerShell reads a bare word starting with '@' as splatting; quote it, as in '@file'. HARborer read it literally.");
            }

            if (c == '&' && words.Count == 0 && !word.Started)
            {
                // Call operator in front of the program: & 'C:\tools\curl.exe' ...
                i++;
                continue;
            }

            if (c is ';' or '|' or '&' or '<' or '>')
            {
                EndWord();
                warnings.Add($"Everything after the unquoted PowerShell operator '{c}' was ignored.");
                break;
            }

            word.Append(c);
            i++;
        }

        EndWord();
        return words;
    }

    /// <summary>Handles a backtick escape; <paramref name="i"/> is the index after the backtick.</summary>
    private static int AppendEscape(string text, int i, WordBuilder word)
    {
        if (i >= text.Length)
        {
            return i;
        }

        var d = text[i];
        switch (d)
        {
            case '0': word.Append('\0'); return i + 1;
            case 'a': word.Append('\a'); return i + 1;
            case 'b': word.Append('\b'); return i + 1;
            case 'e': word.Append('\u001b'); return i + 1;
            case 'f': word.Append('\f'); return i + 1;
            case 'n': word.Append('\n'); return i + 1;
            case 'r': word.Append('\r'); return i + 1;
            case 't': word.Append('\t'); return i + 1;
            case 'v': word.Append('\v'); return i + 1;
            case 'u' when i + 1 < text.Length && text[i + 1] == '{':
            {
                var close = text.IndexOf('}', i + 2);
                if (close > i + 2 && close - (i + 2) <= 6)
                {
                    var hex = text[(i + 2)..close];
                    if (hex.All(CurlText.IsHexDigit))
                    {
                        word.AppendCodePoint(Convert.ToInt32(hex, 16));
                        return close + 1;
                    }
                }

                word.Append('u');
                return i + 1;
            }

            default:
                word.Append(d);
                return i + 1;
        }
    }

    private static int ReadSingleQuoted(string text, int i, WordBuilder word, WarningSink warnings)
    {
        word.Start();
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (IsSingleQuote(c))
            {
                if (i + 1 < n && IsSingleQuote(text[i + 1]))
                {
                    word.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            word.Append(c);
            i++;
        }

        warnings.Add("A single-quoted string is not terminated; it was read to the end of the command.");
        return n;
    }

    private static int ReadDoubleQuoted(string text, int i, WordBuilder word, WarningSink warnings)
    {
        word.Start();
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (IsDoubleQuote(c))
            {
                if (i + 1 < n && IsDoubleQuote(text[i + 1]))
                {
                    word.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (c == '`')
            {
                i = AppendEscape(text, i + 1, word);
                continue;
            }

            if (c == '$')
            {
                WarnExpansion(text, i, warnings);
            }

            word.Append(c);
            i++;
        }

        warnings.Add("A double-quoted string is not terminated; it was read to the end of the command.");
        return n;
    }

    private static void WarnExpansion(string text, int i, WarningSink warnings)
    {
        var name = ShellLexing.ExpansionAt(text, i, allowColon: true);
        if (name is not null)
        {
            warnings.Add($"PowerShell would expand '{name}'; HARborer kept it literally.");
        }
    }
}
