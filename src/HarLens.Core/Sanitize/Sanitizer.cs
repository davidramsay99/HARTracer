using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HarLens.Core.Filtering;
using HarLens.Core.Har;
using HarLens.Core.Text;

namespace HarLens.Core.Sanitize;

public sealed class SanitizeResult
{
    public List<Redaction> Redactions { get; } = [];

    /// <summary>Total redactions, which can exceed <see cref="Redactions"/> when the list was capped.</summary>
    public int TotalRedactions { get; set; }

    public int EntryCount { get; set; }

    public int DistinctSecretValues { get; set; }
}

/// <summary>
/// Produces a sanitized copy of a session (SPEC 8). Two passes: the first applies the name-based rules and
/// collects every secret value removed; the second applies all rules plus a sweep that removes any other
/// occurrence of those values anywhere in the file. Bodies are decoded in memory; nothing temporary is written,
/// and the source file is never modified.
/// </summary>
public sealed class Sanitizer
{
    public const string RedactedToken = "[REDACTED]";
    private const int MaxRecordedRedactions = 100_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex JwtRegex = new(@"\beyJ[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex BearerRegex = new(@"\b(?<scheme>Bearer)\s+(?<secret>[A-Za-z0-9\-._~+/]+=*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex BasicRegex = new(@"\b(?<scheme>Basic)\s+(?<secret>[A-Za-z0-9+/]{8,}={0,2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    private readonly SanitizeOptions _options;
    private readonly byte[] _salt;
    private readonly Regex? _urlParameterRegex;
    private readonly List<Regex> _userPatterns = [];
    private readonly HashSet<string> _knownValues = new(StringComparer.Ordinal);
    private Regex? _knownRegex;
    private SanitizeResult? _result;
    private bool _collecting;

    public Sanitizer(SanitizeOptions options)
    {
        _options = options.Clone();
        _salt = Encoding.UTF8.GetBytes(_options.Salt ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        if (_options.ParameterNames.Count > 0)
        {
            var names = string.Join("|", _options.ParameterNames.OrderByDescending(n => n.Length).Select(Regex.Escape));
            _urlParameterRegex = new Regex($@"(?<=^|[?&#;\s""'])(?<name>{names})=(?<secret>[^&#\s""'<>;]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
        }

        foreach (var pattern in _options.UserPatterns.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            _userPatterns.Add(new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout));
        }
    }

    /// <summary>Lists every redaction without writing anything.</summary>
    public SanitizeResult Preview(HarSession session, IReadOnlyCollection<HarEntry>? entries = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = SelectEntries(session, entries);
        CollectKnownValues(session, list, progress, cancellationToken);
        _result = new SanitizeResult { EntryCount = list.Count, DistinctSecretValues = _knownValues.Count };
        foreach (var page in session.Pages)
        {
            TransformLogProperty("page", page.RawJson);
        }

        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransformEntry(list[i], list[i].Source.ReadBytes(list[i].Offset, list[i].Length));
            Report(progress, 0.5 + 0.5 * i / Math.Max(1, list.Count));
        }

        var result = _result;
        _result = null;
        return result;
    }

    /// <summary>Writes the sanitized HAR. Refuses to write over a file the session reads from.</summary>
    public SanitizeResult ExportToFile(HarSession session, string path, IReadOnlyCollection<HarEntry>? entries = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        HarWriter.EnsureNotSource(session, path);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        return Export(session, stream, entries, progress, cancellationToken);
    }

    public SanitizeResult Export(HarSession session, Stream output, IReadOnlyCollection<HarEntry>? entries = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = SelectEntries(session, entries);
        CollectKnownValues(session, list, progress, cancellationToken);
        _result = new SanitizeResult { EntryCount = list.Count, DistinctSecretValues = _knownValues.Count };
        var written = 0;
        HarWriter.Write(session, output, new HarWriteOptions
        {
            Entries = entries,
            CancellationToken = cancellationToken,
            EntryTransform = (entry, raw) =>
            {
                Report(progress, 0.5 + 0.5 * written++ / Math.Max(1, list.Count));
                return TransformEntry(entry, raw);
            },
            LogPropertyTransform = TransformLogProperty,
        });
        var result = _result;
        _result = null;
        return result;
    }

    private static List<HarEntry> SelectEntries(HarSession session, IReadOnlyCollection<HarEntry>? entries) =>
        (entries ?? session.Entries).ToList();

    private void CollectKnownValues(HarSession session, List<HarEntry> entries, IProgress<double>? progress, CancellationToken ct)
    {
        _knownValues.Clear();
        _knownRegex = null;
        _collecting = true;
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                TransformEntry(entries[i], entries[i].Source.ReadBytes(entries[i].Offset, entries[i].Length));
                Report(progress, 0.5 * i / Math.Max(1, entries.Count));
            }

            foreach (var page in session.Pages)
            {
                TransformLogProperty("page", page.RawJson);
            }
        }
        finally
        {
            _collecting = false;
        }

        if (_options.RedactKnownValuesEverywhere && _knownValues.Count > 0)
        {
            var alternation = string.Join("|", _knownValues.OrderByDescending(v => v.Length).Select(Regex.Escape));
            _knownRegex = new Regex(alternation, RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
        }
    }

    private static void Report(IProgress<double>? progress, double value) => progress?.Report(Math.Clamp(value, 0, 1));

    // ---------------------------------------------------------------- entries

    private byte[]? TransformEntry(HarEntry entry, byte[] raw)
    {
        if (JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 4096 }) is not JsonObject node)
        {
            return null;
        }

        var ctx = new Ctx(entry.Id);
        if (node["request"] is JsonObject request)
        {
            RedactHeaders(request["headers"], "request.headers", ctx);
            RedactNameValues(request["queryString"], "request.queryString", RedactionKind.UrlParameter, ctx);
            if (_options.HeaderNames.Contains("Cookie"))
            {
                RedactCookieArray(request["cookies"], "request.cookies", ctx);
            }

            if (request["postData"] is JsonObject postData)
            {
                var mime = (string?)AsString(postData["mimeType"]) ?? entry.RequestMimeType ?? "";
                if (ShouldDropBody(mime, response: false))
                {
                    DropBody(postData, "request.postData", ctx);
                }
                else
                {
                    SanitizeBody(postData, mime, "request.postData", ctx);
                }

                RedactNameValues(postData["params"], "request.postData.params", RedactionKind.FormParameter, ctx);
            }
        }

        if (node["response"] is JsonObject response)
        {
            RedactHeaders(response["headers"], "response.headers", ctx);
            if (_options.HeaderNames.Contains("Set-Cookie"))
            {
                RedactCookieArray(response["cookies"], "response.cookies", ctx);
            }

            if (response["content"] is JsonObject content)
            {
                var mime = (string?)AsString(content["mimeType"]) ?? "";
                if (_options.DropAllResponseBodies || ShouldDropBody(mime, response: true))
                {
                    DropBody(content, "response.content", ctx);
                }
                else
                {
                    SanitizeBody(content, mime, "response.content", ctx);
                }
            }
        }

        // Every remaining string, vendor fields included (_initiator URLs, WebSocket frames, comments).
        SweepStrings(node, "", ctx);
        return _collecting ? null : JsonSerializer.SerializeToUtf8Bytes(node, HarWriter.NodeOptions);
    }

    private byte[]? TransformLogProperty(string name, byte[] raw)
    {
        if (name is not ("pages" or "page" or "comment"))
        {
            return null;
        }

        var node = JsonNode.Parse(raw);
        if (node is null)
        {
            return null;
        }

        var ctx = new Ctx(0);
        if (node is JsonValue)
        {
            var s = AsString(node);
            if (s is null)
            {
                return null;
            }

            var cleaned = SanitizeString(s, "log." + name, ctx);
            return cleaned == s || _collecting ? null : JsonSerializer.SerializeToUtf8Bytes(cleaned, HarWriter.NodeOptions);
        }

        SweepStrings(node, "log." + name, ctx);
        return _collecting ? null : JsonSerializer.SerializeToUtf8Bytes(node, HarWriter.NodeOptions);
    }

    private void RedactHeaders(JsonNode? headers, string path, Ctx ctx)
    {
        if (headers is not JsonArray array)
        {
            return;
        }

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject h || AsString(h["name"]) is not { } name || AsString(h["value"]) is not { } value)
            {
                continue;
            }

            if (!_options.HeaderNames.Contains(name))
            {
                continue;
            }

            var location = $"{path}[{i}] {name}";
            var lower = name.ToLowerInvariant();
            string redacted;
            if (lower is "authorization" or "proxy-authorization")
            {
                redacted = RedactAuthorization(value, location, ctx);
            }
            else if (lower == "cookie")
            {
                redacted = RedactCookieHeader(value, location, ctx);
            }
            else if (lower == "set-cookie")
            {
                redacted = string.Join("\n", value.Split('\n').Select(line => RedactSetCookie(line, location, ctx)));
            }
            else
            {
                redacted = Replace(value, location, RedactionKind.Header, name, ctx);
            }

            h["value"] = redacted;
        }
    }

    private string RedactAuthorization(string value, string location, Ctx ctx)
    {
        var space = value.IndexOf(' ');
        if (space > 0 && value[..space].All(char.IsAsciiLetter))
        {
            var scheme = value[..space];
            var credentials = value[(space + 1)..].Trim();
            if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            {
                CollectBasic(credentials);
            }

            return scheme + " " + Replace(credentials, location, RedactionKind.Header, "Authorization", ctx);
        }

        return Replace(value, location, RedactionKind.Header, "Authorization", ctx);
    }

    private void CollectBasic(string base64)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            Collect(decoded);
            var colon = decoded.IndexOf(':');
            if (colon >= 0)
            {
                Collect(decoded[(colon + 1)..]);
            }
        }
        catch (FormatException)
        {
        }
    }

    private string RedactCookieHeader(string value, string location, Ctx ctx)
    {
        var parts = value.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var cookieName = parts[i][..eq].Trim();
            var cookieValue = parts[i][(eq + 1)..].Trim();
            if (cookieValue.Length == 0)
            {
                continue;
            }

            parts[i] = (i == 0 ? "" : " ") + cookieName + "=" + Replace(cookieValue, $"{location} ({cookieName})", RedactionKind.Cookie, "Cookie", ctx);
        }

        return string.Join(";", parts);
    }

    private string RedactSetCookie(string line, string location, Ctx ctx)
    {
        var semi = line.IndexOf(';');
        var pair = semi < 0 ? line : line[..semi];
        var attributes = semi < 0 ? "" : line[semi..];
        var eq = pair.IndexOf('=');
        if (eq < 0)
        {
            return line;
        }

        var cookieName = pair[..eq].Trim();
        var cookieValue = pair[(eq + 1)..].Trim();
        return cookieValue.Length == 0
            ? line
            : cookieName + "=" + Replace(cookieValue, $"{location} ({cookieName})", RedactionKind.Cookie, "Set-Cookie", ctx) + attributes;
    }

    private void RedactCookieArray(JsonNode? cookies, string path, Ctx ctx)
    {
        if (cookies is not JsonArray array)
        {
            return;
        }

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject c && AsString(c["value"]) is { Length: > 0 } value)
            {
                c["value"] = Replace(value, $"{path}[{i}] {AsString(c["name"])}", RedactionKind.Cookie, "cookie", ctx);
            }
        }
    }

    private void RedactNameValues(JsonNode? array, string path, RedactionKind kind, Ctx ctx)
    {
        if (array is not JsonArray items)
        {
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is JsonObject p && AsString(p["name"]) is { } name && _options.ParameterNames.Contains(FormUrlDecode(name)) &&
                AsString(p["value"]) is { Length: > 0 } value)
            {
                p["value"] = Replace(value, $"{path}[{i}] {name}", kind, name, ctx);
            }
        }
    }

    private bool ShouldDropBody(string mime, bool response)
    {
        if (_options.DropBodyMimeTypes.Count == 0)
        {
            return false;
        }

        var baseType = MimeTypes.StripParameters(mime);
        return _options.DropBodyMimeTypes.Any(pattern => FilterExpression.Wildcard(pattern)(baseType));
    }

    private void DropBody(JsonObject holder, string path, Ctx ctx)
    {
        if (holder["text"] is null)
        {
            return;
        }

        holder.Remove("text");
        holder.Remove("encoding");
        holder.Remove("params");
        holder["comment"] = "Body removed by HarLens sanitized export.";
        Record(ctx, path + ".text", RedactionKind.BodyDropped, "drop body", "(body)", "(removed)");
    }

    private void SanitizeBody(JsonObject holder, string mime, string path, Ctx ctx)
    {
        if (AsString(holder["text"]) is not { } text || text.Length == 0)
        {
            return;
        }

        var isBase64 = string.Equals(AsString(holder["encoding"]), "base64", StringComparison.OrdinalIgnoreCase);
        if (isBase64)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return;
            }

            if (!MimeTypes.IsTextual(mime) && !IsUtf8Text(bytes))
            {
                return; // binary: nothing textual to redact
            }

            var decoded = Encoding.UTF8.GetString(bytes);
            var cleaned = SanitizeBodyText(decoded, mime, path + ".text (base64)", ctx);
            if (!ReferenceEquals(cleaned, decoded) && cleaned != decoded)
            {
                holder["text"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(cleaned));
            }

            return;
        }

        var result = SanitizeBodyText(text, mime, path + ".text", ctx);
        if (result != text)
        {
            holder["text"] = result;
        }
    }

    private string SanitizeBodyText(string text, string mime, string path, Ctx ctx)
    {
        var trimmed = text.AsSpan().TrimStart();
        var looksJson = MimeTypes.IsJson(mime) || (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['));
        if (looksJson && _options.RedactJsonProperties)
        {
            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
            }

            if (json is not null)
            {
                var before = _result?.TotalRedactions ?? 0;
                var changed = RedactJsonProperties(json, path, ctx);
                changed |= SweepStrings(json, path, ctx);
                return changed || (_result?.TotalRedactions ?? 0) != before
                    ? json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                    : text;
            }
        }

        return SanitizeString(text, path, ctx);
    }

    private bool RedactJsonProperties(JsonNode node, string path, Ctx ctx)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[name];
                    if (_options.ParameterNames.Contains(name) && child is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
                    {
                        var original = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
                        if (original.Length > 0 && !IsAlreadyRedacted(original))
                        {
                            obj[name] = Replace(original, $"{path} → {name}", RedactionKind.JsonProperty, name, ctx);
                            changed = true;
                        }
                    }
                    else if (child is not null)
                    {
                        changed |= RedactJsonProperties(child, path, ctx);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null)
                    {
                        changed |= RedactJsonProperties(item, path, ctx);
                    }
                }

                break;
        }

        return changed;
    }

    /// <summary>Applies the string rules to every string value under <paramref name="node"/>. Returns true when anything changed.</summary>
    private bool SweepStrings(JsonNode node, string path, Ctx ctx)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[name];
                    if (child is null)
                    {
                        continue;
                    }

                    var childPath = path.Length == 0 ? name : path + "." + name;
                    if (child is JsonValue && AsString(child) is { } s)
                    {
                        var cleaned = SanitizeString(s, childPath, ctx);
                        if (cleaned != s)
                        {
                            obj[name] = cleaned;
                            changed = true;
                        }
                    }
                    else
                    {
                        changed |= SweepStrings(child, childPath, ctx);
                    }
                }

                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is null)
                    {
                        continue;
                    }

                    var childPath = $"{path}[{i}]";
                    if (child is JsonValue && AsString(child) is { } s)
                    {
                        var cleaned = SanitizeString(s, childPath, ctx);
                        if (cleaned != s)
                        {
                            arr[i] = cleaned;
                            changed = true;
                        }
                    }
                    else
                    {
                        changed |= SweepStrings(child, childPath, ctx);
                    }
                }

                break;
        }

        return changed;
    }

    /// <summary>URL and form parameters by name, JWTs, Bearer and Basic credentials, user patterns, then known values.</summary>
    private string SanitizeString(string s, string path, Ctx ctx)
    {
        if (s.Length < 4)
        {
            return s;
        }

        var result = s;
        if (_urlParameterRegex is not null && result.Contains('=', StringComparison.Ordinal))
        {
            result = ReplaceGroup(result, _urlParameterRegex, path, RedactionKind.UrlParameter, m => m.Groups["name"].Value, ctx);
        }

        if (_options.RedactJwt && result.Contains("eyJ", StringComparison.Ordinal))
        {
            result = ReplaceWhole(result, JwtRegex, path, RedactionKind.Jwt, "JWT", ctx);
        }

        if (_options.RedactBearer && result.Contains("earer", StringComparison.OrdinalIgnoreCase))
        {
            result = ReplaceGroup(result, BearerRegex, path, RedactionKind.Bearer, _ => "Bearer", ctx);
        }

        if (_options.RedactBasic && result.Contains("asic", StringComparison.OrdinalIgnoreCase))
        {
            result = ReplaceGroup(result, BasicRegex, path, RedactionKind.BasicCredentials, _ => "Basic", ctx, collectBasic: true);
        }

        foreach (var pattern in _userPatterns)
        {
            result = pattern.GetGroupNumbers().Length > 1 && pattern.GroupNumberFromName("secret") >= 0
                ? ReplaceGroup(result, pattern, path, RedactionKind.Pattern, _ => pattern.ToString(), ctx)
                : ReplaceWhole(result, pattern, path, RedactionKind.Pattern, pattern.ToString(), ctx);
        }

        if (_knownRegex is not null && !_collecting)
        {
            result = ReplaceWhole(result, _knownRegex, path, RedactionKind.KnownValue, "value redacted elsewhere", ctx);
        }

        return result;
    }

    private string ReplaceGroup(string input, Regex regex, string path, RedactionKind kind, Func<Match, string> rule, Ctx ctx, bool collectBasic = false)
    {
        try
        {
            return regex.Replace(input, m =>
            {
                var g = m.Groups["secret"];
                if (!g.Success || IsAlreadyRedacted(g.Value))
                {
                    return m.Value;
                }

                if (collectBasic)
                {
                    CollectBasic(g.Value);
                }

                var replacement = Replace(g.Value, path, kind, rule(m), ctx);
                return m.Value[..(g.Index - m.Index)] + replacement + m.Value[(g.Index - m.Index + g.Length)..];
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    private string ReplaceWhole(string input, Regex regex, string path, RedactionKind kind, string rule, Ctx ctx)
    {
        try
        {
            return regex.Replace(input, m => IsAlreadyRedacted(m.Value) ? m.Value : Replace(m.Value, path, kind, rule, ctx));
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    /// <summary>Records the redaction, remembers the value for the known-value sweep, and returns the replacement.</summary>
    private string Replace(string value, string location, RedactionKind kind, string rule, Ctx ctx)
    {
        if (IsAlreadyRedacted(value))
        {
            return value;
        }

        Collect(value);
        var replacement = _options.HashValues ? Hash(value) : RedactedToken;
        Record(ctx, location, kind, rule, SecretMasker.Mask(value), replacement);
        return replacement;
    }

    private void Collect(string value)
    {
        if (_collecting && value.Length >= _options.MinimumKnownValueLength && !IsAlreadyRedacted(value))
        {
            _knownValues.Add(value);
            var decoded = FormUrlDecode(value);
            if (decoded != value && decoded.Length >= _options.MinimumKnownValueLength)
            {
                _knownValues.Add(decoded);
            }
        }
    }

    private void Record(Ctx ctx, string location, RedactionKind kind, string rule, string masked, string replacement)
    {
        if (_result is null)
        {
            return;
        }

        _result.TotalRedactions++;
        if (_result.Redactions.Count < MaxRecordedRedactions)
        {
            _result.Redactions.Add(new Redaction(ctx.EntryId, location, kind, rule, masked, replacement));
        }
    }

    private string Hash(string value)
    {
        var digest = HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(value));
        return "[sha256:" + Convert.ToHexStringLower(digest)[..16] + "]";
    }

    private static bool IsAlreadyRedacted(string value) =>
        value.StartsWith("[REDACTED", StringComparison.Ordinal) || value.StartsWith("[sha256:", StringComparison.Ordinal);

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static string FormUrlDecode(string s) => Http.FormUrlEncoding.Decode(s);

    private static bool IsUtf8Text(byte[] bytes)
    {
        if (!System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return false;
        }

        foreach (var b in bytes.AsSpan(0, Math.Min(bytes.Length, 4096)))
        {
            if (b < 0x09 || (b is > 0x0D and < 0x20))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Ctx(int EntryId);
}
