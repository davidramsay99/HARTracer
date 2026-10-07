using HarLens.Core.Curl;

namespace HarLens.Core.Tests.Curl;

public class ShellLexerTests
{
    private static List<string> Bash(string text) => BashLexer.Lex(text, new WarningSink()).Select(w => w.Text).ToList();

    private static List<string> Cmd(string text) => CmdLexer.Lex(text, new WarningSink()).Select(w => w.Text).ToList();

    private static List<string> Ps(string text) => PowerShellLexer.Lex(text, new WarningSink()).Select(w => w.Text).ToList();

    // ---- bash ----

    [Fact]
    public void BashQuotingForms()
    {
        Assert.Equal(["curl", "a b", "it's", "abc", "", "x y"], Bash("curl 'a b' it\\'s 'a'\"b\"$'c' '' x\\ y"));
    }

    [Fact]
    public void BashDoubleQuotesEscapeOnlyDollarBacktickQuoteBackslashNewline()
    {
        Assert.Equal(["a$b`c\"d\\e\\f\\n", "joined"], Bash("\"a\\$b\\`c\\\"d\\\\e\\f\\n\" \"join\\\ned\""));
    }

    [Fact]
    public void BashAnsiCEscapes()
    {
        var words = Bash("$'\\\\ \\' \\\" \\? \\a\\b\\e\\E\\f\\n\\r\\t\\v \\x41\\x4 \\101\\0 \\u00e9 \\U0001F600 \\cA \\q'");
        Assert.Equal(["\\ ' \" ? \a\b\u001b\u001b\f\n\r\t\v A\u0004 A\0 é 😀 \u0001 \\q"], words);
    }

    [Fact]
    public void BashAnsiCUtf8ByteEscapesDecodeAndStrayLatin1BytesAreKept()
    {
        Assert.Equal(["é", "é", "☃"], Bash("$'\\xc3\\xa9' $'\\xe9' $'\\u2603'"));
    }

    [Fact]
    public void BashAnsiCSurrogatePairEscapesCombine()
    {
        Assert.Equal(["😀"], Bash("$'\\ud83d\\ude00'"));
    }

    [Fact]
    public void BashLineContinuationJoinsAndToleratesTrailingBlanksAndCrlf()
    {
        Assert.Equal(["curl", "a", "b", "cd"], Bash("curl a \\\n  b \\  \r\n c\\\nd"));
    }

    [Fact]
    public void BashUnescapedLineBreakIsFlagged()
    {
        var words = BashLexer.Lex("curl a\n-b", new WarningSink());
        Assert.False(words[1].AfterLineBreak);
        Assert.True(words[2].AfterLineBreak);
    }

    [Fact]
    public void BashCommentsAndOperators()
    {
        var warnings = new WarningSink();
        var words = BashLexer.Lex("curl a#b # comment\n c | jq .", warnings).Select(w => w.Text).ToList();
        Assert.Equal(["curl", "a#b", "c"], words);
        Assert.Contains(warnings.Items, w => w.Contains("'|'", StringComparison.Ordinal));
    }

    [Fact]
    public void BashExpansionsAreKeptLiterallyWithWarning()
    {
        var warnings = new WarningSink();
        var words = BashLexer.Lex("curl $HOME \"${X}/$(id)\" '$NOT'", warnings).Select(w => w.Text).ToList();
        Assert.Equal(["curl", "$HOME", "${X}/$(id)", "$NOT"], words);
        Assert.Equal(3, warnings.Items.Count);
        Assert.Contains(warnings.Items, w => w.Contains("'$HOME'", StringComparison.Ordinal));
    }

    [Fact]
    public void BashUnterminatedQuoteIsReadToTheEnd()
    {
        var warnings = new WarningSink();
        Assert.Equal(["curl", "abc def"], BashLexer.Lex("curl 'abc def", warnings).Select(w => w.Text));
        Assert.Single(warnings.Items);
    }

    // ---- cmd.exe and the C runtime ----

    [Theory]
    [InlineData("curl \"a b c\" d e", new[] { "curl", "a b c", "d", "e" })]
    [InlineData("curl \"ab\\\"c\" \"\\\\\" d", new[] { "curl", "ab\"c", "\\", "d" })]
    [InlineData("curl a\\\\\\b d\"e f\"g h", new[] { "curl", "a\\\\\\b", "de fg", "h" })]
    [InlineData("curl a\\\\\\\"b c d", new[] { "curl", "a\\\"b", "c", "d" })]
    [InlineData("curl a\\\\\\\\\"b c\" d e", new[] { "curl", "a\\\\b c", "d", "e" })]
    [InlineData("curl a\"b\"\" c d", new[] { "curl", "ab\" c d" })]
    [InlineData("curl \"\" x", new[] { "curl", "", "x" })]
    public void CrtSplittingFollowsMicrosoftDocumentation(string line, string[] expected)
    {
        Assert.Equal(expected, Cmd(line));
    }

    [Fact]
    public void CmdCaretEscapesOutsideQuotesButNotInside()
    {
        Assert.Equal(["curl", "\"a^b\"&<>", "a^b"], Cmd("curl ^\"\\^\"a^^b\\^\"^&^<^>^\" \"a^b\""));
    }

    [Fact]
    public void CmdCaretNewlineContinuesAndCaretNewlineNewlineIsALiteralLineFeed()
    {
        Assert.Equal(["curl", "a", "line1\nline2"], Cmd("curl a ^\n  ^\"line1^\n\nline2^\""));
    }

    [Fact]
    public void CmdDropsCarriageReturns()
    {
        Assert.Equal(["curl", "a", "x\ny"], Cmd("curl a ^\r\n  ^\"x^\r\n\r\ny^\""));
    }

    [Fact]
    public void CmdChromePercentEscapeLeavesTheVariableUnexpanded()
    {
        var warnings = new WarningSink();
        Assert.Equal(["curl", "%PATH% 100%25 %%"], CmdLexer.Lex("curl ^\"^%^PATH^% 100^%^25 ^%^%^\"", warnings).Select(w => w.Text));
        Assert.Empty(warnings.Items);
    }

    [Fact]
    public void CmdWarnsAboutAnExpandableVariable()
    {
        var warnings = new WarningSink();
        Assert.Equal(["curl", "%TEMP%\\x"], CmdLexer.Lex("curl %TEMP%\\x", warnings).Select(w => w.Text));
        Assert.Contains(warnings.Items, w => w.Contains("%TEMP%", StringComparison.Ordinal));
    }

    [Fact]
    public void CmdChromeSplitSurrogateEscapesRejoin()
    {
        // Chrome escapes each UTF-16 code unit: ^<high surrogate>^<low surrogate>.
        Assert.Equal(["curl", "😀"], Cmd("curl ^\"^\ud83d^\ude00^\""));
    }

    [Fact]
    public void CmdUnescapedOperatorEndsTheCommandOutsideQuotesAndIsKeptInsideChromeQuotes()
    {
        var warnings = new WarningSink();
        Assert.Equal(["curl", "a"], CmdLexer.Lex("curl a & echo b", warnings).Select(w => w.Text));
        Assert.Contains(warnings.Items, w => w.Contains("was ignored", StringComparison.Ordinal));

        warnings = new WarningSink();
        Assert.Equal(["curl", "a&b"], CmdLexer.Lex("curl ^\"a&b^\"", warnings).Select(w => w.Text));
        Assert.Contains(warnings.Items, w => w.Contains("kept it as a literal", StringComparison.Ordinal));
    }

    [Fact]
    public void CmdProgramNameMayBeQuotedPath()
    {
        Assert.Equal(["C:\\Program Files\\curl\\curl.exe", "x"], Cmd("\"C:\\Program Files\\curl\\curl.exe\" x"));
    }

    // ---- PowerShell ----

    [Fact]
    public void PowerShellSingleQuotes()
    {
        Assert.Equal(["curl.exe", "it's", "$x `n \"", "‘smart’"], Ps("curl.exe 'it''s' '$x `n \"' '‘‘smart’’'"));
    }

    [Fact]
    public void PowerShellDoubleQuotesAndBacktickEscapes()
    {
        Assert.Equal(["a\"b", "tab\there\r\n\0", "\"q\" `", "u😀"], Ps("\"a\"\"b\" \"tab`there`r`n`0\" \"`\"q`\" ``\" \"u`u{1F600}\""));
    }

    [Fact]
    public void PowerShellBacktickContinuationAndBarewordEscapes()
    {
        Assert.Equal(["curl.exe", "a b", "c"], Ps("curl.exe a` b `\n  c"));
    }

    [Fact]
    public void PowerShellWarnsAboutVariablesInDoubleQuotes()
    {
        var warnings = new WarningSink();
        Assert.Equal(["curl.exe", "Bearer $token", "$env:HOME"], PowerShellLexer.Lex("curl.exe \"Bearer $token\" $env:HOME", warnings).Select(w => w.Text));
        Assert.Contains(warnings.Items, w => w.Contains("'$token'", StringComparison.Ordinal));
        Assert.Contains(warnings.Items, w => w.Contains("'$env:HOME'", StringComparison.Ordinal));
    }

    [Fact]
    public void PowerShellCallOperatorAndStopParsingToken()
    {
        var warnings = new WarningSink();
        var words = PowerShellLexer.Lex("& 'C:\\tools\\curl.exe' --% -H \"a: \\\"b\\\"\" x", warnings).Select(w => w.Text).ToList();
        Assert.Equal(["C:\\tools\\curl.exe", "-H", "a: \"b\"", "x"], words);
        Assert.Contains(warnings.Items, w => w.Contains("--%", StringComparison.Ordinal));
    }

    // ---- dialect detection ----

    [Theory]
    [InlineData("curl 'https://x' \\\n  -H 'a: b'", CurlDialect.Bash)]
    [InlineData("curl 'https://x' -H 'a: ^\"b'", CurlDialect.Bash)]
    [InlineData("curl https://x", CurlDialect.Bash)]
    [InlineData("curl ^\"https://x^\" ^\n  -H ^\"a: b^\"", CurlDialect.Cmd)]
    [InlineData("curl ^\"https://x^\" --compressed", CurlDialect.Cmd)]
    [InlineData("curl.exe -d \"{\\\"a\\\":1}\" https://x", CurlDialect.Cmd)]
    [InlineData("curl.exe 'https://x' `\n  -H 'a: b'", CurlDialect.PowerShell)]
    [InlineData("curl.exe 'https://x' -H 'a: b'", CurlDialect.PowerShell)]
    [InlineData("curl.exe \"https://x/`ttab\"", CurlDialect.PowerShell)]
    [InlineData("& 'C:\\Program Files\\curl.exe' https://x", CurlDialect.PowerShell)]
    public void DetectsDialectFromStructure(string text, CurlDialect expected)
    {
        Assert.Equal(expected, CurlParser.DetectDialect(text));
    }
}
