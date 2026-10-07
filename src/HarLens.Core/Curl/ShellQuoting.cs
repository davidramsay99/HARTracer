using System.Globalization;
using System.Text;

namespace HarLens.Core.Curl;

/// <summary>Quotes one argument so that the matching lexer (and the real shell) reads back exactly the same text.</summary>
internal static class ShellQuoting
{
    private static bool IsAsciiControl(char c) => c < ' ' || c == '\x7f';

    /// <summary>
    /// POSIX shells. Plain single quotes when possible. A value with a single quote or an ASCII control character
    /// uses <c>$'...'</c> with <c>\\ \' \n \r \t</c> and <c>\xHH</c> escapes; <c>!</c> is written as <c>\x21</c>
    /// there to avoid history expansion. Non-ASCII text stays literal. Only escapes that bash 3.2 understands are used.
    /// </summary>
    public static string Bash(string value)
    {
        if (!value.Any(c => c == '\'' || IsAsciiControl(c)))
        {
            return "'" + value + "'";
        }

        var sb = new StringBuilder(value.Length + 8).Append("$'");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '!': sb.Append("\\x21"); break;
                default:
                    if (IsAsciiControl(c))
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('\'').ToString();
    }

    private static bool IsCmdSafe(char c) =>
        char.IsAsciiLetterOrDigit(c) || IsJsWhitespace(c) || "_-:=+~'/.,?;()*`".Contains(c, StringComparison.Ordinal);

    /// <summary>The characters JavaScript's <c>\s</c> matches, which Chrome's escaper leaves unescaped.</summary>
    private static bool IsJsWhitespace(char c) =>
        c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' ||
        (int)c is 0xA0 or 0x1680 or (>= 0x2000 and <= 0x200A) or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF;

    /// <summary>
    /// Windows cmd.exe, following Chrome's "Copy as cURL (cmd)" escaper with one correction. The value is first
    /// quoted for the C runtime (<c>"..."</c>, with <c>\"</c> for a quote and backslashes doubled only where they
    /// precede a quote; Chrome doubles every backslash, which curl.exe then receives doubled). Then every character
    /// outside Chrome's safe set, the quotes included, gets a caret so cmd.exe never enters quote mode; a <c>%</c>
    /// followed by a name character is written <c>^%^X</c> so no variable is expanded; a line feed becomes
    /// <c>^</c>, LF, LF. A carriage return cannot be passed through cmd.exe at all; callers must avoid it.
    /// </summary>
    public static string Cmd(string value)
    {
        var crt = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                crt.Append('\\', (2 * backslashes) + 1).Append('"');
            }
            else
            {
                crt.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        crt.Append('\\', 2 * backslashes).Append('"');

        var sb = new StringBuilder(crt.Length * 2);
        for (var i = 0; i < crt.Length; i++)
        {
            var c = crt[i];
            if (c == '\n')
            {
                sb.Append("^\n\n");
            }
            else if (c == '%')
            {
                sb.Append("^%");
                if (i + 1 < crt.Length && (char.IsAsciiLetterOrDigit(crt[i + 1]) || crt[i + 1] == '_'))
                {
                    sb.Append('^');
                }
            }
            else if (IsCmdSafe(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('^').Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// PowerShell. Single quotes, with every single-quote character (typographic ones included) doubled. A value
    /// with an ASCII control character uses double quotes with backtick escapes (<c>`n `r `t `0 `a `b `f `v</c>),
    /// and backticks, dollar signs and double-quote characters escaped with a backtick.
    /// </summary>
    public static string PowerShell(string value)
    {
        if (!value.Any(IsAsciiControl))
        {
            var single = new StringBuilder(value.Length + 2).Append('\'');
            foreach (var c in value)
            {
                single.Append(c);
                if (PowerShellLexer.IsSingleQuote(c))
                {
                    single.Append(c);
                }
            }

            return single.Append('\'').ToString();
        }

        var sb = new StringBuilder(value.Length + 8).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\0': sb.Append("`0"); break;
                case '\a': sb.Append("`a"); break;
                case '\b': sb.Append("`b"); break;
                case '\f': sb.Append("`f"); break;
                case '\n': sb.Append("`n"); break;
                case '\r': sb.Append("`r"); break;
                case '\t': sb.Append("`t"); break;
                case '\v': sb.Append("`v"); break;
                case '`':
                case '$':
                    sb.Append('`').Append(c);
                    break;
                default:
                    if (PowerShellLexer.IsDoubleQuote(c))
                    {
                        sb.Append('`');
                    }

                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>Quotes for a PowerShell double-quoted string the way Chrome's "Copy as PowerShell" does.</summary>
    public static string PowerShellChromeStyle(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            if (c is '`' or '$' or '"')
            {
                sb.Append('`').Append(c);
            }
            else if (c is < ' ' or > '~')
            {
                sb.Append("$([char]").Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(')');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append('"').ToString();
    }
}
