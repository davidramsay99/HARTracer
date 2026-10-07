using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HarLens.Core.Http;

namespace HarLens.Core.Curl;

/// <summary>
/// Applies curl's option semantics (https://curl.se/docs/manpage.html) to the words of a command and builds the
/// request model. Where curl and the request model differ, the choices are documented on the members below.
/// </summary>
internal sealed partial class CurlCommandInterpreter
{
    private const string FormContentType = "application/x-www-form-urlencoded";

    private readonly CurlParseOptions _options;
    private readonly CurlImportResult _result;
    private readonly WarningSink _warnings;

    private readonly List<string> _urls = [];
    private readonly List<DataPiece> _data = [];
    private readonly List<MultipartPart> _parts = [];
    private readonly List<HeaderEvent> _headers = [];
    private readonly List<ConnectOverride> _overrides = [];
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    private string? _customMethod;
    private bool _head;
    private bool _get;
    private bool _follow;
    private bool _insecure;
    private bool _compressed;
    private bool _globoff;
    private bool _json;
    private bool _stop;
    private int? _maxRedirs;
    private TimeSpan? _timeout;
    private TimeSpan? _connectTimeout;
    private string? _proxy;
    private HttpVersionPreference _version = HttpVersionPreference.Default;
    private string? _certOption;
    private string? _certName;
    private string? _certPassword;
    private string? _certType;
    private string? _keyPath;

    public CurlCommandInterpreter(CurlParseOptions options, CurlImportResult result, WarningSink warnings)
    {
        _options = options;
        _result = result;
        _warnings = warnings;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://")]
    private static partial Regex SchemePrefix();

    [GeneratedRegex(@"^(CurrentUser|LocalMachine|CurrentService|Services|CurrentUserGroupPolicy|LocalMachineGroupPolicy|LocalMachineEnterprise)\\([^\\]+)\\([0-9A-Fa-f]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CertificateStorePath();

    /// <summary>A header from <c>-H</c> (custom) or produced by <c>-A</c>, <c>-e</c>, <c>-b</c>, <c>-u</c> (generated).</summary>
    private sealed class HeaderEvent
    {
        public required string Name { get; init; }

        /// <summary>Value to send. Null for a custom header that only suppresses (<c>Name:</c>) or for a cleared generated header.</summary>
        public string? Value { get; set; }

        public bool IsCustom { get; init; }
    }

    private sealed class DataPiece
    {
        public string Text { get; set; } = "";

        public bool IsJson { get; init; }

        public string? Option { get; init; }

        /// <summary>Raw path of <c>--data-binary @file</c>.</summary>
        public string? BinaryFile { get; init; }
    }

    public static bool IsCurlProgram(string word)
    {
        var name = word.Trim();
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name.Equals("curl", StringComparison.OrdinalIgnoreCase) || name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase);
    }

    public void Run(IReadOnlyList<ShellWord> words)
    {
        var start = 0;
        if (words.Count > 0 && IsCurlProgram(words[0].Text))
        {
            start = 1;
        }
        else if (words.Count > 0)
        {
            _warnings.Add("The command does not start with curl; its words were read as curl arguments.");
        }

        for (var i = start; i < words.Count && !_stop; i++)
        {
            var word = words[i];
            if (word.AfterLineBreak)
            {
                if (IsCurlProgram(word.Text))
                {
                    _warnings.Add("Only the first command was imported; the text from the next curl command on was ignored.");
                    break;
                }

                WarnLineBreak();
            }

            var text = word.Text;
            if (text == "--")
            {
                _warnings.Add("The argument '--' was ignored.");
                _result.IgnoredOptions.Add(text);
            }
            else if (text.StartsWith("--", StringComparison.Ordinal))
            {
                i = HandleLongOption(words, i);
            }
            else if (text.Length > 1 && text[0] == '-')
            {
                i = HandleShortOptions(words, i);
            }
            else
            {
                _urls.Add(text);
            }
        }

        Build();
    }

    private void WarnLineBreak() =>
        _warnings.Add("A line break without a line continuation character was read as a space.");

    private string TakeValue(IReadOnlyList<ShellWord> words, int index)
    {
        if (words[index].AfterLineBreak)
        {
            WarnLineBreak();
        }

        return words[index].Text;
    }

    private int HandleLongOption(IReadOnlyList<ShellWord> words, int i)
    {
        var written = words[i].Text;
        var name = written[2..];
        var negate = false;
        var expand = false;
        var option = CurlOptionTable.FindLong(name);
        if (option is null && name.StartsWith("no-", StringComparison.Ordinal) && CurlOptionTable.FindLong(name[3..]) is { IsBoolean: true } positive)
        {
            option = positive;
            negate = true;
        }
        else if (option is null && name.StartsWith("expand-", StringComparison.Ordinal) && CurlOptionTable.FindLong(name[7..]) is { TakesArgument: true } expanded)
        {
            option = expanded;
            expand = true;
        }

        if (option is null)
        {
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            _warnings.Add(eq > 0 && CurlOptionTable.FindLong(name[..eq]) is { TakesArgument: true }
                ? $"Unknown option '{written}' was ignored; curl has no --option=value form, write '--{name[..eq]} {name[(eq + 1)..]}'."
                : $"Unknown option '{written}' was ignored.");
            _result.IgnoredOptions.Add(written);
            return i;
        }

        string? value = null;
        if (option.TakesArgument)
        {
            if (i + 1 >= words.Count)
            {
                _warnings.Add($"Option '{written}' needs an argument and was ignored.");
                _result.IgnoredOptions.Add(written);
                return i;
            }

            value = TakeValue(words, ++i);
            if (expand)
            {
                value = Expand(value);
            }
        }

        Apply(option, written, value, negate);
        return i;
    }

    private int HandleShortOptions(IReadOnlyList<ShellWord> words, int i)
    {
        var text = words[i].Text;
        for (var k = 1; k < text.Length && !_stop; k++)
        {
            var written = "-" + text[k];
            var option = CurlOptionTable.FindShort(text[k]);
            if (option is null)
            {
                _warnings.Add($"Unknown option '{written}' was ignored.");
                _result.IgnoredOptions.Add(written);
                continue;
            }

            if (!option.TakesArgument)
            {
                Apply(option, written, null, negate: false);
                continue;
            }

            string value;
            if (k + 1 < text.Length)
            {
                value = text[(k + 1)..];
            }
            else if (i + 1 < words.Count)
            {
                value = TakeValue(words, ++i);
            }
            else
            {
                _warnings.Add($"Option '{written}' needs an argument and was ignored.");
                _result.IgnoredOptions.Add(written);
                return i;
            }

            Apply(option, written, value, negate: false);
            return i;
        }

        return i;
    }

    private void Apply(CurlOption option, string written, string? value, bool negate)
    {
        var arg = value ?? "";
        switch (option.Kind)
        {
            case CurlOptionKind.Request:
                _customMethod = arg;
                break;
            case CurlOptionKind.Header:
                AddHeaderArgument(written, arg);
                break;
            case CurlOptionKind.Data:
                if (arg.StartsWith('@'))
                {
                    _data.Add(new DataPiece { Text = ReadFile(written, arg[1..], stripNewlines: true) ?? "" });
                }
                else
                {
                    _data.Add(new DataPiece { Text = arg });
                }

                break;
            case CurlOptionKind.DataRaw:
                _data.Add(new DataPiece { Text = arg });
                break;
            case CurlOptionKind.DataBinary:
                if (arg.StartsWith('@') && arg != "@-")
                {
                    _data.Add(new DataPiece { Option = written, BinaryFile = arg[1..] });
                }
                else if (arg == "@-")
                {
                    _warnings.Add($"'{written} @-' reads standard input, which is not available; the data was left empty.");
                    _data.Add(new DataPiece());
                }
                else
                {
                    _data.Add(new DataPiece { Text = arg });
                }

                break;
            case CurlOptionKind.DataUrlEncode:
                _data.Add(new DataPiece { Text = UrlEncodeArgument(written, arg) });
                break;
            case CurlOptionKind.Json:
                _json = true;
                _data.Add(new DataPiece
                {
                    IsJson = true,
                    Text = arg.StartsWith('@') ? ReadFile(written, arg[1..], stripNewlines: false) ?? "" : arg,
                });
                break;
            case CurlOptionKind.Form:
            case CurlOptionKind.FormString:
                AddForm(written, arg, literal: option.Kind == CurlOptionKind.FormString);
                break;
            case CurlOptionKind.Get:
                _get = !negate;
                break;
            case CurlOptionKind.User:
                SetGenerated("Authorization", arg.Length == 0 ? null : BasicAuthorization(arg));
                break;
            case CurlOptionKind.OAuth2Bearer:
                SetGenerated("Authorization", arg.Length == 0 ? null : "Bearer " + arg);
                break;
            case CurlOptionKind.Cookie:
                if (arg.Contains('=', StringComparison.Ordinal))
                {
                    AppendCookie(arg);
                }
                else
                {
                    _warnings.Add($"'{written} {arg}' reads cookies from a cookie-jar file, which HarLens does not support; it was ignored.");
                    _result.IgnoredOptions.Add(written);
                }

                break;
            case CurlOptionKind.UserAgent:
                // curl: an empty argument removes the header.
                SetGenerated("User-Agent", arg.Length == 0 ? null : arg);
                break;
            case CurlOptionKind.Referer:
                var referer = arg.EndsWith(";auto", StringComparison.Ordinal) ? arg[..^5] : arg;
                SetGenerated("Referer", referer.Length == 0 ? null : referer);
                break;
            case CurlOptionKind.Head:
                _head = !negate;
                break;
            case CurlOptionKind.Location:
                _follow = !negate;
                break;
            case CurlOptionKind.MaxRedirs:
                if (int.TryParse(arg.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var redirs))
                {
                    _maxRedirs = redirs;
                }
                else
                {
                    _warnings.Add($"'{written} {arg}' is not a number and was ignored.");
                }

                break;
            case CurlOptionKind.Insecure:
                _insecure = !negate;
                break;
            case CurlOptionKind.Compressed:
                _compressed = !negate;
                break;
            case CurlOptionKind.MaxTime:
            case CurlOptionKind.ConnectTimeout:
                if (CurlText.TryParseSeconds(arg, out var seconds))
                {
                    // curl: zero means no limit.
                    TimeSpan? limit = seconds == TimeSpan.Zero ? null : seconds;
                    if (option.Kind == CurlOptionKind.MaxTime)
                    {
                        _timeout = limit;
                    }
                    else
                    {
                        _connectTimeout = limit;
                    }
                }
                else
                {
                    _warnings.Add($"'{written} {arg}' is not a number of seconds and was ignored.");
                }

                break;
            case CurlOptionKind.Proxy:
                _proxy = arg.Length == 0 ? null : SchemePrefix().IsMatch(arg) ? arg : "http://" + arg;
                break;
            case CurlOptionKind.Resolve:
                ParseResolve(written, arg);
                break;
            case CurlOptionKind.ConnectTo:
                ParseConnectTo(written, arg);
                break;
            case CurlOptionKind.Http10:
                _warnings.Add($"'{written}' (HTTP/1.0) is not supported; the default HTTP version is used.");
                _result.IgnoredOptions.Add(written);
                break;
            case CurlOptionKind.Http11:
                _version = HttpVersionPreference.Http11;
                break;
            case CurlOptionKind.Http2:
                _version = HttpVersionPreference.Http2;
                break;
            case CurlOptionKind.Http2PriorKnowledge:
                _version = HttpVersionPreference.Http2;
                _warnings.Add($"'{written}' was imported as HTTP/2; the request engine negotiates HTTP/2 instead of assuming it.");
                break;
            case CurlOptionKind.Http3:
                _warnings.Add($"'{written}' (HTTP/3) is not supported; the default HTTP version is used.");
                _result.IgnoredOptions.Add(written);
                break;
            case CurlOptionKind.Cert:
                (_certName, var password) = ParseCertParameter(arg);
                _certOption = written;
                if (password is not null)
                {
                    _certPassword = password;
                }

                break;
            case CurlOptionKind.CertType:
                _certType = arg;
                break;
            case CurlOptionKind.Key:
                _keyPath = arg;
                break;
            case CurlOptionKind.Pass:
                _certPassword = arg;
                break;
            case CurlOptionKind.Url:
                _urls.Add(arg);
                break;
            case CurlOptionKind.Globoff:
                _globoff = !negate;
                break;
            case CurlOptionKind.Variable:
                DefineVariable(written, arg);
                break;
            case CurlOptionKind.Next:
                _warnings.Add($"'{written}' starts another request; only the first request was imported.");
                _stop = true;
                break;
            case CurlOptionKind.OutputOnly:
                _result.IgnoredOptions.Add(written);
                break;
            default:
                _warnings.Add(option.TakesArgument
                    ? $"Unsupported option '{written}' and its argument were ignored."
                    : $"Unsupported option '{written}' was ignored.");
                _result.IgnoredOptions.Add(written);
                break;
        }
    }

    // ---- Headers -------------------------------------------------------------------------------------------

    private void AddHeaderArgument(string written, string value)
    {
        if (!value.StartsWith('@'))
        {
            AddHeaderLine(value);
            return;
        }

        var content = ReadFile(written, value[1..], stripNewlines: false);
        if (content is null)
        {
            return;
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                AddHeaderLine(trimmed);
            }
        }
    }

    /// <summary>
    /// Interprets one <c>-H</c> value like curl. <c>Name: value</c> adds a header; the single blank that
    /// conventionally follows the colon is removed and any further leading blanks stay in the value, which
    /// reproduces curl's HTTP/1.1 wire bytes exactly (curl sends the text verbatim). <c>Name:</c> with nothing but
    /// blanks after the colon sends nothing and suppresses the header curl would add. <c>Name;</c> sends an empty
    /// header.
    /// </summary>
    private void AddHeaderLine(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var name = text[..colon];
            var rest = text[(colon + 1)..];
            if (rest.All(IsCurlSpace))
            {
                _headers.Add(new HeaderEvent { Name = name, Value = null, IsCustom = true });
            }
            else
            {
                var value = rest[0] is ' ' or '\t' ? rest[1..] : rest;
                _headers.Add(new HeaderEvent { Name = name, Value = value, IsCustom = true });
            }

            return;
        }

        var semicolon = text.IndexOf(';', StringComparison.Ordinal);
        if (colon < 0 && semicolon > 0)
        {
            var name = text[..semicolon];
            var rest = text[(semicolon + 1)..];
            if (rest.Length == 0)
            {
                _headers.Add(new HeaderEvent { Name = name, Value = "", IsCustom = true });
            }
            else
            {
                // curl sends nothing for "Name;   " or "Name;junk", but still treats the name as set.
                _headers.Add(new HeaderEvent { Name = name, Value = null, IsCustom = true });
                if (!rest.All(IsCurlSpace))
                {
                    _warnings.Add($"The header '{text}' is not valid (text after ';'); curl does not send it.");
                }
            }

            return;
        }

        _warnings.Add(colon == 0
            ? $"The header '{text}' has no name (HTTP/2 pseudo-headers cannot be set with -H); it was ignored."
            : $"The header '{text}' has no ':' and was ignored.");
    }

    private void SetGenerated(string name, string? value)
    {
        var existing = _headers.Find(h => !h.IsCustom && h.Name == name);
        if (existing is null)
        {
            _headers.Add(new HeaderEvent { Name = name, Value = value });
        }
        else
        {
            existing.Value = value;
        }
    }

    /// <summary>Several <c>-b name=value</c> options are joined with ";" (no space), as curl 8 does.</summary>
    private void AppendCookie(string cookie)
    {
        var existing = _headers.Find(h => !h.IsCustom && h.Name == "Cookie");
        if (existing?.Value is { } value)
        {
            existing.Value = value + ";" + cookie;
        }
        else
        {
            SetGenerated("Cookie", cookie);
        }
    }

    private string BasicAuthorization(string userAndPassword)
    {
        if (!userAndPassword.Contains(':', StringComparison.Ordinal))
        {
            _warnings.Add("-u gives a user name without a password; curl would prompt for one. An empty password was used.");
            userAndPassword += ":";
        }

        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userAndPassword));
    }

    /// <summary>
    /// Builds the header list. Custom and generated headers keep their command-line order. A generated header is
    /// dropped when any <c>-H</c> names the same header (even to suppress it), as curl does. <c>--json</c> appends
    /// Content-Type and Accept unless an <c>-H</c> names them; a data body without a Content-Type gets curl's
    /// default form type.
    /// </summary>
    private List<HeaderEntry> BuildHeaders(bool hasDataBody)
    {
        var named = new HashSet<string>(_headers.Where(h => h.IsCustom).Select(h => h.Name), StringComparer.OrdinalIgnoreCase);
        var headers = new List<HeaderEntry>();
        foreach (var header in _headers)
        {
            if (header.Value is not null && (header.IsCustom || !named.Contains(header.Name)))
            {
                headers.Add(new HeaderEntry(header.Name, header.Value));
            }
        }

        if (_json)
        {
            if (!named.Contains("Content-Type"))
            {
                headers.Add(new HeaderEntry("Content-Type", "application/json"));
            }

            if (!named.Contains("Accept"))
            {
                headers.Add(new HeaderEntry("Accept", "application/json"));
            }
        }
        else if (hasDataBody && !named.Contains("Content-Type"))
        {
            headers.Add(new HeaderEntry("Content-Type", FormContentType));
        }

        return headers;
    }

    // ---- Data and forms ------------------------------------------------------------------------------------

    /// <summary>curl's <c>--data-urlencode</c> forms: <c>content</c>, <c>=content</c>, <c>name=content</c>, <c>@file</c>, <c>name@file</c>.</summary>
    private string UrlEncodeArgument(string written, string value)
    {
        var separator = value.IndexOf('=', StringComparison.Ordinal);
        var isFile = false;
        if (separator < 0)
        {
            separator = value.IndexOf('@', StringComparison.Ordinal);
            isFile = separator >= 0;
        }

        string name;
        string content;
        if (separator < 0)
        {
            name = "";
            content = value;
        }
        else
        {
            name = value[..separator];
            content = value[(separator + 1)..];
            if (isFile)
            {
                content = ReadFile(written, content, stripNewlines: false) ?? "";
            }
        }

        var encoded = CurlText.FormUrlEncode(content);
        return name.Length > 0 ? name + "=" + encoded : encoded;
    }

    private string JoinData()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _data.Count; i++)
        {
            var piece = _data[i];
            if (i > 0 && !piece.IsJson)
            {
                sb.Append('&');
            }

            if (piece.BinaryFile is not null)
            {
                // Mixed with other data, the file content has to be read now.
                piece.Text = ReadFile(piece.Option ?? "--data-binary", piece.BinaryFile, stripNewlines: false) ?? "";
            }

            sb.Append(piece.Text);
        }

        return sb.ToString();
    }

    private void AddForm(string written, string value, bool literal)
    {
        foreach (var field in CurlFormParser.Parse(value, literal, _warnings))
        {
            var part = new MultipartPart { Name = field.Name, ContentType = field.ContentType, FileName = field.FileName };
            if (field.FilePath is not null)
            {
                if (field.FilePath == "-")
                {
                    _warnings.Add($"The form field '{field.Name}' reads standard input, which is not available.");
                }

                part.FilePath = SendTimeFile(written, field.FilePath);
                part.FileContentAsValue = field.FileContentAsValue;
            }
            else
            {
                part.Value = field.Value;
            }

            _parts.Add(part);
        }
    }

    // ---- Files ---------------------------------------------------------------------------------------------

    private string? ResolvePath(string path)
    {
        if (_options.FileBaseDirectory is not { } baseDirectory)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Reads a file named by the command now, or reports why not. Returns null when nothing was read.</summary>
    private string? ReadFile(string option, string path, bool stripNewlines)
    {
        if (path == "-")
        {
            _warnings.Add($"'{option} @-' reads standard input, which is not available; the content was left empty.");
            return null;
        }

        var reference = new CurlFileReference { Option = option, Path = path, ResolvedPath = ResolvePath(path) };
        _result.FileReferences.Add(reference);
        if (_options.FileBaseDirectory is null)
        {
            _warnings.Add($"'{option}' refers to the file '{path}'. Choose a base directory to read it; the content was left empty.");
            return null;
        }

        if (reference.ResolvedPath is null || !File.Exists(reference.ResolvedPath))
        {
            _warnings.Add($"The file '{reference.ResolvedPath ?? path}' named by '{option}' was not found; the content was left empty.");
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(reference.ResolvedPath);
            if (stripNewlines)
            {
                bytes = bytes.Where(b => b is not ((byte)'\r' or (byte)'\n')).ToArray();
            }

            reference.Resolved = true;
            return CurlText.DecodeUtf8Lenient(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warnings.Add($"The file '{reference.ResolvedPath}' named by '{option}' could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Records a file the request engine reads at send time and returns the path to store in the model.</summary>
    private string SendTimeFile(string option, string path)
    {
        var resolved = ResolvePath(path);
        var exists = resolved is not null && File.Exists(resolved);
        _result.FileReferences.Add(new CurlFileReference
        {
            Option = option,
            Path = path,
            ResolvedPath = resolved,
            Resolved = exists,
            ReadAtSendTime = true,
        });
        if (_options.FileBaseDirectory is null)
        {
            _warnings.Add($"'{option}' refers to the file '{path}', which is read when the request is sent. Choose a base directory to resolve it.");
            return path;
        }

        if (!exists)
        {
            _warnings.Add($"The file '{resolved ?? path}' named by '{option}' was not found.");
        }

        return resolved ?? path;
    }

    // ---- Variables -----------------------------------------------------------------------------------------

    private void DefineVariable(string written, string value)
    {
        if (value.StartsWith('%'))
        {
            var rest = value[1..];
            var eq = rest.IndexOf('=', StringComparison.Ordinal);
            var envName = eq < 0 ? rest : rest[..eq];
            var fallback = eq < 0 ? null : rest[(eq + 1)..];
            _warnings.Add($"The environment variable '{envName}' was not imported; {(fallback is null ? "an empty value" : "its default value")} was used.");
            _variables[envName] = fallback ?? "";
            return;
        }

        var k = 0;
        while (k < value.Length && (char.IsAsciiLetterOrDigit(value[k]) || value[k] == '_'))
        {
            k++;
        }

        var name = value[..k];
        var tail = value[k..];
        if (tail.StartsWith('['))
        {
            _warnings.Add($"Byte ranges in '{written} {value}' are not supported; the whole value was used.");
            var close = tail.IndexOf(']', StringComparison.Ordinal);
            tail = close < 0 ? "" : tail[(close + 1)..];
        }

        if (name.Length == 0 || tail.Length == 0 || tail[0] is not ('=' or '@'))
        {
            _warnings.Add($"'{written} {value}' is not a valid variable definition and was ignored.");
            return;
        }

        _variables[name] = tail[0] == '='
            ? tail[1..]
            : ReadFile(written, tail[1..], stripNewlines: false) ?? "";
    }

    /// <summary>Expands <c>{{name}}</c> and <c>{{name:function:...}}</c> for <c>--expand-</c> options, as curl 8 does.</summary>
    private string Expand(string input)
    {
        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] == '\\' && i + 2 < input.Length && input[i + 1] == '{' && input[i + 2] == '{')
            {
                sb.Append("{{");
                i += 3;
                continue;
            }

            if (input[i] == '{' && i + 1 < input.Length && input[i + 1] == '{')
            {
                var close = input.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    sb.Append(input, i, input.Length - i);
                    break;
                }

                var spec = input[(i + 2)..close].Split(':');
                if (spec[0].Length == 0 || !spec[0].All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    sb.Append(input, i, close + 2 - i);
                    i = close + 2;
                    continue;
                }

                if (!_variables.TryGetValue(spec[0], out var value))
                {
                    _warnings.Add($"The variable '{spec[0]}' is not set; it expanded to an empty string.");
                    value = "";
                }

                foreach (var function in spec.Skip(1))
                {
                    value = ApplyFunction(function, value);
                }

                sb.Append(value);
                i = close + 2;
                continue;
            }

            sb.Append(input[i]);
            i++;
        }

        return sb.ToString();
    }

    private string ApplyFunction(string function, string value)
    {
        switch (function)
        {
            case "trim":
                return value.Trim(' ', '\t', '\n', '\v', '\f', '\r');
            case "json":
                return CurlText.JsonEscape(value);
            case "url":
                return CurlText.PercentEncode(value);
            case "b64":
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            case "64dec":
                try
                {
                    return CurlText.DecodeUtf8Lenient(Convert.FromBase64String(value));
                }
                catch (FormatException)
                {
                    return "[64dec-fail]";
                }

            default:
                _warnings.Add($"The variable function '{function}' is not supported and was skipped.");
                return value;
        }
    }

    // ---- Connection options --------------------------------------------------------------------------------

    private static string StripBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    private static List<string> SplitColons(string value)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']' && depth > 0)
            {
                depth--;
            }

            if (c == ':' && depth == 0)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port <= 65535;

    /// <summary><c>--resolve [+]host:port:addr[,addr]...</c>. IPv6 hosts and addresses are stored without brackets; <c>*</c> becomes an empty host (any).</summary>
    private void ParseResolve(string written, string value)
    {
        if (value.StartsWith('-'))
        {
            _warnings.Add($"'{written} {value}' removes a cached address, which has no meaning here; it was ignored.");
            return;
        }

        var fields = SplitColons(value.TrimStart('+'));
        if (fields.Count < 3 || !TryParsePort(fields[1], out var port))
        {
            _warnings.Add($"'{written} {value}' is not in the form host:port:address and was ignored.");
            return;
        }

        var addresses = string.Join(":", fields.Skip(2)).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length == 0)
        {
            _warnings.Add($"'{written} {value}' has no address and was ignored.");
            return;
        }

        if (addresses.Length > 1)
        {
            _warnings.Add($"'{written} {value}' lists several addresses; only the first, {addresses[0]}, is used.");
        }

        var host = StripBrackets(fields[0]);
        _overrides.Add(new ConnectOverride
        {
            Kind = ConnectOverrideKind.Resolve,
            Host = host == "*" ? "" : host,
            Port = port,
            TargetHost = StripBrackets(addresses[0]),
        });
    }

    /// <summary><c>--connect-to HOST1:PORT1:HOST2:PORT2</c>; empty fields mean any host or port, or no change.</summary>
    private void ParseConnectTo(string written, string value)
    {
        var fields = SplitColons(value);
        var port1 = 0;
        var port2 = 0;
        if (fields.Count != 4 ||
            (fields[1].Length > 0 && !TryParsePort(fields[1], out port1)) ||
            (fields[3].Length > 0 && !TryParsePort(fields[3], out port2)))
        {
            _warnings.Add($"'{written} {value}' is not in the form HOST1:PORT1:HOST2:PORT2 and was ignored.");
            return;
        }

        _overrides.Add(new ConnectOverride
        {
            Kind = ConnectOverrideKind.ConnectTo,
            Host = StripBrackets(fields[0]),
            Port = port1,
            TargetHost = StripBrackets(fields[2]),
            TargetPort = port2,
        });
    }

    /// <summary>
    /// Port of curl's parse_cert_parameter: <c>\\</c> and <c>\:</c> are escapes, the first other colon separates the
    /// password, and a drive-letter colon (<c>C:\</c> or <c>C:/</c>) is part of the name. curl applies the drive-letter
    /// rule only on Windows; HarLens always applies it.
    /// </summary>
    internal static (string Name, string? Password) ParseCertParameter(string value)
    {
        if (value.StartsWith("pkcs11:", StringComparison.OrdinalIgnoreCase) || value.IndexOfAny([':', '\\']) < 0)
        {
            return (value, null);
        }

        var sb = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (c == '\\')
            {
                i++;
                if (i >= value.Length)
                {
                    sb.Append('\\');
                    break;
                }

                if (value[i] is not ('\\' or ':'))
                {
                    sb.Append('\\');
                }

                sb.Append(value[i]);
                i++;
                continue;
            }

            if (c == ':')
            {
                if (i == 1 && value.Length > 2 && value[2] is '\\' or '/' && char.IsAsciiLetter(value[0]))
                {
                    sb.Append(':');
                    i++;
                    continue;
                }

                i++;
                return (sb.ToString(), i < value.Length ? value[i..] : null);
            }

            sb.Append(c);
            i++;
        }

        return (sb.ToString(), null);
    }

    private static bool IsPfxPath(string path) =>
        path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase);

    private ClientCertificateSpec? BuildClientCertificate()
    {
        if (_certName is null && _keyPath is null && _certPassword is null)
        {
            return null;
        }

        var spec = new ClientCertificateSpec { Password = _certPassword };
        var store = _certName is null ? Match.Empty : CertificateStorePath().Match(_certName);
        if (store.Success)
        {
            var location = store.Groups[1].Value;
            if (location.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase) || location.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase))
            {
                spec.Source = ClientCertificateSource.WindowsStore;
                spec.StoreLocation = location.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase) ? "CurrentUser" : "LocalMachine";
                spec.Thumbprint = store.Groups[3].Value;
                if (!store.Groups[2].Value.Equals("MY", StringComparison.OrdinalIgnoreCase))
                {
                    _warnings.Add($"The certificate store '{store.Groups[2].Value}' is not supported; the personal store (MY) is used.");
                }
            }
            else
            {
                _warnings.Add($"The certificate store location '{location}' is not supported; the client certificate was ignored.");
                return null;
            }
        }
        else
        {
            spec.Source = _certType?.ToUpperInvariant() switch
            {
                "P12" => ClientCertificateSource.PfxFile,
                "PEM" or "DER" => ClientCertificateSource.PemFile,
                _ => _certName is not null && IsPfxPath(_certName) ? ClientCertificateSource.PfxFile : ClientCertificateSource.PemFile,
            };
            if (_certType is not null && _certType.ToUpperInvariant() is not ("P12" or "PEM" or "DER"))
            {
                _warnings.Add($"The certificate type '{_certType}' is not supported; the type was taken from the file name.");
            }

            if (_certName is not null)
            {
                spec.Path = SendTimeFile(_certOption ?? "-E", _certName);
            }
        }

        if (_keyPath is not null)
        {
            spec.KeyPath = SendTimeFile("--key", _keyPath);
        }

        return spec;
    }

    // ---- Assembly ------------------------------------------------------------------------------------------

    private string BuildUrl()
    {
        if (_urls.Count == 0)
        {
            _warnings.Add("No URL was found in the command.");
            return "";
        }

        if (_urls.Count > 1)
        {
            _warnings.Add($"The command names {_urls.Count} URLs; only the first was imported. Ignored: {string.Join(" ", _urls.Skip(1))}");
        }

        var url = _globoff ? _urls[0] : UnescapeGlob(_urls[0]);
        if (url.Length > 0 && !SchemePrefix().IsMatch(url))
        {
            _warnings.Add($"The URL '{url}' has no scheme; http:// was added, as curl does.");
            url = "http://" + url;
        }

        return url;
    }

    /// <summary>curl treats <c>[ ] { }</c> in URLs as glob syntax; a backslash before one of them makes it literal.</summary>
    private string UnescapeGlob(string url)
    {
        var sb = new StringBuilder(url.Length);
        for (var i = 0; i < url.Length; i++)
        {
            var c = url[i];
            if (c == '\\' && i + 1 < url.Length && url[i + 1] is '[' or ']' or '{' or '}')
            {
                sb.Append(url[i + 1]);
                i++;
                continue;
            }

            var ipv6Host = c == '[' && (sb.ToString().EndsWith("://", StringComparison.Ordinal) || (i > 0 && url[i - 1] == '@'));
            if (c is '[' or '{' && !ipv6Host)
            {
                _warnings.Add($"The URL contains '{c}', which curl treats as a glob pattern unless -g is given; HarLens kept it literally.");
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string AppendQuery(string url, string query)
    {
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var fragment = hash >= 0 ? url[hash..] : "";
        var head = hash >= 0 ? url[..hash] : url;
        if (!head.Contains('?', StringComparison.Ordinal))
        {
            head += "?" + query;
        }
        else if (head.EndsWith('?'))
        {
            head += query;
        }
        else
        {
            head += "&" + query;
        }

        return head + fragment;
    }

    internal static BodyMode InferTextMode(string? contentType)
    {
        if (contentType is null)
        {
            return BodyMode.Raw;
        }

        var media = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (media is "application/json" or "text/json" || media.EndsWith("+json", StringComparison.Ordinal))
        {
            return BodyMode.Json;
        }

        return media == FormContentType ? BodyMode.FormUrlEncoded : BodyMode.Raw;
    }

    private static bool IsCurlSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private void Build()
    {
        var request = _result.Request;
        var url = BuildUrl();
        var body = new RequestBody();
        var hasBody = false;
        var hasDataBody = false;

        if (_parts.Count > 0)
        {
            if (_data.Count > 0)
            {
                _warnings.Add("curl refuses to combine -F with -d style data; the data options were ignored.");
            }

            if (_get)
            {
                _warnings.Add("-G cannot move a multipart form into the URL; -G was ignored.");
            }

            body.Mode = BodyMode.Multipart;
            body.Parts = _parts;
            hasBody = true;
        }
        else if (_data.Count > 0)
        {
            if (_get)
            {
                url = AppendQuery(url, JoinData());
            }
            else if (_data.Count == 1 && _data[0].BinaryFile is { } binaryFile)
            {
                body.Mode = BodyMode.BinaryFile;
                body.FilePath = SendTimeFile(_data[0].Option ?? "--data-binary", binaryFile);
                hasBody = hasDataBody = true;
            }
            else
            {
                body.Mode = BodyMode.Raw;
                body.Text = JoinData();
                hasBody = hasDataBody = true;
            }
        }

        var method = _head ? "HEAD" : hasBody ? "POST" : "GET";
        if (_head && hasBody)
        {
            _warnings.Add("curl refuses -I together with a request body; the method was set to HEAD and the body kept.");
        }

        request.Method = _customMethod ?? method;
        request.Url = url;
        request.HttpVersion = _version;
        request.Headers = BuildHeaders(hasDataBody);
        if (body.Mode == BodyMode.Raw)
        {
            body.Mode = InferTextMode(request.GetHeader("Content-Type"));
        }

        request.Body = body;
        request.Options = new RequestOptions
        {
            FollowRedirects = _follow,
            MaxRedirects = _maxRedirs ?? 50,
            Timeout = _timeout,
            ConnectTimeout = _connectTimeout,
            AutoDecompress = _compressed,
            Insecure = _insecure,
            Proxy = _proxy,
            ConnectOverrides = _overrides,
            ClientCertificate = BuildClientCertificate(),
        };
    }
}
