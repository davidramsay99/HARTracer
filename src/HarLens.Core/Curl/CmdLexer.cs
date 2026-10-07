using System.Text;
using System.Text.RegularExpressions;

namespace HarLens.Core.Curl;

/// <summary>
/// Splits a Windows cmd.exe command line the way it reaches curl.exe, in two stages:
/// <list type="number">
/// <item>cmd.exe: carriage returns are dropped; outside double quotes <c>^X</c> yields a literal X, and a caret
/// before a line feed removes the line feed and takes the next character literally (so <c>^</c>, LF, LF is a
/// literal line feed); an unescaped <c>"</c> toggles quote mode, in which carets are literal.</item>
/// <item>The Microsoft C runtime argument splitter (UCRT rules): blanks split outside quotes; 2n backslashes and a
/// quote give n backslashes and toggle quoting; 2n+1 backslashes and a quote give n backslashes and a literal
/// quote; inside quotes <c>""</c> is a literal quote; other backslashes are literal.</item>
/// </list>
/// Environment variables (<c>%NAME%</c>) are not expanded.
/// </summary>
internal static partial class CmdLexer
{
    [GeneratedRegex("%[A-Za-z_][A-Za-z0-9_]*%")]
    private static partial Regex PercentVariable();

    public static List<ShellWord> Lex(string text, WarningSink warnings)
    {
        var match = PercentVariable().Match(text);
        if (match.Success)
        {
            warnings.Add($"cmd.exe would expand the environment variable {match.Value}; HarLens kept it literally.");
        }

        var line = RunCmdStage(text.Replace("\r", "", StringComparison.Ordinal), warnings, out var breaks);
        return SplitCrt(line, breaks, firstIsProgram: true);
    }

    /// <summary>Stage 1. Returns the command line as cmd.exe would pass it to CreateProcess.</summary>
    private static string RunCmdStage(string text, WarningSink warnings, out List<int> breaks)
    {
        breaks = [];
        var sb = new StringBuilder(text.Length);
        var cmdQuote = false;

        // CRT quote state of the output so far, used only to decide whether an unescaped & | < > was meant as data.
        var crtQuote = false;
        var backslashes = 0;
        void Emit(char c)
        {
            sb.Append(c);
            if (c == '\\')
            {
                backslashes++;
                return;
            }

            if (c == '"' && backslashes % 2 == 0)
            {
                crtQuote = !crtQuote;
            }

            backslashes = 0;
        }

        var n = text.Length;
        var i = 0;
        while (i < n)
        {
            var c = text[i];
            if (cmdQuote)
            {
                if (c == '"')
                {
                    cmdQuote = false;
                }
                else if (c == '\n')
                {
                    warnings.Add("cmd.exe ends a command at a line break inside double quotes; HarLens kept the line break as part of the argument.");
                }

                Emit(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '^':
                    if (i + 1 >= n)
                    {
                        i++;
                    }
                    else if (text[i + 1] == '\n')
                    {
                        // Continuation: the line feed is removed and the next character is taken literally.
                        i += 2;
                        if (i < n)
                        {
                            Emit(text[i]);
                            i++;
                        }
                    }
                    else if (text[i + 1] is ' ' or '\t' && ShellLexing.IsContinuation(text, i + 1, out var next))
                    {
                        // A caret followed by invisible trailing blanks: treat it as the continuation it was meant to be.
                        Emit(' ');
                        i = next;
                    }
                    else
                    {
                        Emit(text[i + 1]);
                        i += 2;
                    }

                    break;
                case '"':
                    cmdQuote = true;
                    Emit(c);
                    i++;
                    break;
                case '\n':
                    if (crtQuote)
                    {
                        warnings.Add("cmd.exe ends a command at an unescaped line break; HarLens kept the line break as part of the quoted argument.");
                        Emit('\n');
                    }
                    else
                    {
                        breaks.Add(sb.Length);
                        Emit(' ');
                    }

                    i++;
                    break;
                case '&':
                case '|':
                case '<':
                case '>':
                    if (crtQuote)
                    {
                        warnings.Add($"cmd.exe treats an unescaped '{c}' as an operator even inside a ^\"...^\" argument; HarLens kept it as a literal character (older Chrome versions emit commands like this).");
                        Emit(c);
                        i++;
                    }
                    else
                    {
                        warnings.Add($"Everything after the unescaped cmd.exe operator '{c}' was ignored.");
                        i = n;
                    }

                    break;
                default:
                    Emit(c);
                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Stage 2: splits a command line with the UCRT rules. When <paramref name="firstIsProgram"/> is true the first
    /// word uses the program-name rule (quotes toggle, backslashes are literal).
    /// </summary>
    public static List<ShellWord> SplitCrt(string line, IReadOnlyList<int>? breaks, bool firstIsProgram)
    {
        var words = new List<ShellWord>();
        var sb = new StringBuilder();
        var n = line.Length;
        var pos = 0;
        var lastEnd = 0;

        bool BreakBetween(int from, int to)
        {
            if (breaks is null || words.Count == 0)
            {
                return false;
            }

            foreach (var b in breaks)
            {
                if (b >= from && b < to)
                {
                    return true;
                }
            }

            return false;
        }

        while (true)
        {
            while (pos < n && line[pos] is ' ' or '\t')
            {
                pos++;
            }

            if (pos >= n)
            {
                break;
            }

            var start = pos;
            sb.Clear();
            if (firstIsProgram && words.Count == 0)
            {
                var inQuote = false;
                while (pos < n && (inQuote || line[pos] is not (' ' or '\t')))
                {
                    if (line[pos] == '"')
                    {
                        inQuote = !inQuote;
                    }
                    else
                    {
                        sb.Append(line[pos]);
                    }

                    pos++;
                }
            }
            else
            {
                var inQuote = false;
                while (pos < n)
                {
                    var c = line[pos];
                    if (!inQuote && c is ' ' or '\t')
                    {
                        break;
                    }

                    if (c == '\\')
                    {
                        var count = 0;
                        while (pos < n && line[pos] == '\\')
                        {
                            count++;
                            pos++;
                        }

                        if (pos < n && line[pos] == '"')
                        {
                            sb.Append('\\', count / 2);
                            if (count % 2 == 1)
                            {
                                sb.Append('"');
                                pos++;
                            }

                            // With an even count the quote is handled as an ordinary quote on the next iteration.
                        }
                        else
                        {
                            sb.Append('\\', count);
                        }

                        continue;
                    }

                    if (c == '"')
                    {
                        if (inQuote && pos + 1 < n && line[pos + 1] == '"')
                        {
                            sb.Append('"');
                            pos += 2;
                        }
                        else
                        {
                            inQuote = !inQuote;
                            pos++;
                        }

                        continue;
                    }

                    sb.Append(c);
                    pos++;
                }
            }

            words.Add(new ShellWord(sb.ToString(), BreakBetween(lastEnd, start)));
            lastEnd = pos;
        }

        return words;
    }
}
