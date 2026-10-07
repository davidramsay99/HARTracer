namespace Harborer.Core.Curl;

/// <summary>
/// Imports a pasted curl command into an <see cref="Http.HttpRequestSpec"/>. Parsing never throws for
/// malformed input: anything that cannot be represented is reported in <see cref="CurlImportResult.Warnings"/>.
/// Referenced files are read only when <see cref="CurlParseOptions.FileBaseDirectory"/> is set.
/// </summary>
public static class CurlParser
{
    public static CurlImportResult Parse(string commandText, CurlParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        options ??= new CurlParseOptions();

        var text = commandText.Trim().TrimStart((char)0xFEFF).Trim();
        var dialect = options.Dialect == CurlDialect.Auto ? DetectDialect(text) : options.Dialect;
        var warnings = new WarningSink();
        var words = dialect switch
        {
            CurlDialect.Cmd => CmdLexer.Lex(text, warnings),
            CurlDialect.PowerShell => PowerShellLexer.Lex(text, warnings),
            _ => BashLexer.Lex(text, warnings),
        };

        var result = new CurlImportResult { Dialect = dialect };
        new CurlCommandInterpreter(options, result, warnings).Run(words);
        result.Warnings.AddRange(warnings.Items);
        return result;
    }

    /// <summary>
    /// Guesses the quoting dialect from the structure of the command rather than its content, so that quoted
    /// values cannot mislead it:
    /// <list type="number">
    /// <item>A line continuation decides: a line ending in <c>^</c> means cmd, in a backtick PowerShell, in a backslash bash.</item>
    /// <item>A first argument starting with <c>^"</c> means cmd (Chrome's single-line cmd output).</item>
    /// <item>A program named <c>*.exe</c> or a leading <c>&amp;</c> call operator means PowerShell when the
    /// command contains a single quote or a backtick, and cmd otherwise.</item>
    /// <item>Anything else is bash.</item>
    /// </list>
    /// </summary>
    internal static CurlDialect DetectDialect(string text)
    {
        var lineStart = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                break;
            }

            var end = newline - 1;
            while (end >= lineStart && text[end] is ' ' or '\t' or '\r')
            {
                end--;
            }

            if (end >= lineStart)
            {
                switch (text[end])
                {
                    case '^': return CurlDialect.Cmd;
                    case '`': return CurlDialect.PowerShell;
                    case '\\': return CurlDialect.Bash;
                }
            }

            lineStart = newline + 1;
        }

        var callOperator = text.StartsWith('&');
        var rest = text.TrimStart('&', ' ', '\t');
        string program;
        if (rest.Length > 0 && rest[0] is '"' or '\'')
        {
            var close = rest.IndexOf(rest[0], 1);
            program = close < 0 ? rest[1..] : rest[1..close];
            rest = close < 0 ? "" : rest[(close + 1)..];
        }
        else
        {
            var space = rest.IndexOfAny([' ', '\t', '\r', '\n']);
            program = space < 0 ? rest : rest[..space];
            rest = space < 0 ? "" : rest[space..];
        }

        if (rest.TrimStart().StartsWith("^\"", StringComparison.Ordinal))
        {
            return CurlDialect.Cmd;
        }

        if (callOperator || program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return text.AsSpan().IndexOfAny('\'', '`') >= 0 ? CurlDialect.PowerShell : CurlDialect.Cmd;
        }

        return CurlDialect.Bash;
    }
}
