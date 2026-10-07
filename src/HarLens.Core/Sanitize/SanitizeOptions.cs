namespace HarLens.Core.Sanitize;

/// <summary>Redaction rules for the sanitized export. Name lists are case-insensitive.</summary>
public sealed class SanitizeOptions
{
    public static readonly IReadOnlyList<string> DefaultHeaderNames =
        ["Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key"];

    public static readonly IReadOnlyList<string> DefaultParameterNames =
    [
        "access_token", "id_token", "refresh_token", "code", "client_secret", "password", "sig", "signature",
        "SAMLResponse", "SAMLRequest",
    ];

    /// <summary>Headers whose values are redacted. Authorization keeps its scheme word; cookies keep their names.</summary>
    public HashSet<string> HeaderNames { get; } = new(DefaultHeaderNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>Query, fragment, form and JSON property names whose values are redacted.</summary>
    public HashSet<string> ParameterNames { get; } = new(DefaultParameterNames, StringComparer.OrdinalIgnoreCase);

    public bool RedactJwt { get; set; } = true;

    public bool RedactBearer { get; set; } = true;

    /// <summary>Also redacts <c>Basic &lt;base64&gt;</c> credentials found in text.</summary>
    public bool RedactBasic { get; set; } = true;

    /// <summary>User-defined regular expressions applied to every string. A capture group named <c>secret</c> limits the redaction to that group.</summary>
    public List<string> UserPatterns { get; } = [];

    /// <summary>Apply the parameter names to JSON property names inside JSON bodies (for example OAuth token responses).</summary>
    public bool RedactJsonProperties { get; set; } = true;

    /// <summary>After name-based redaction, replace every other occurrence of the removed values anywhere in the file.</summary>
    public bool RedactKnownValuesEverywhere { get; set; } = true;

    public bool DropAllResponseBodies { get; set; }

    /// <summary>MIME types (wildcards allowed, for example <c>image/*</c>) whose bodies are dropped, request and response.</summary>
    public HashSet<string> DropBodyMimeTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replace values with a stable salted SHA-256 so equal values stay correlatable, instead of removing them.</summary>
    public bool HashValues { get; set; }

    /// <summary>Salt for <see cref="HashValues"/>. A random salt is generated per export when null.</summary>
    public string? Salt { get; set; }

    /// <summary>Values shorter than this are not swept as known values, to avoid redacting common words.</summary>
    public int MinimumKnownValueLength { get; set; } = 8;

    public SanitizeOptions Clone()
    {
        var c = new SanitizeOptions
        {
            RedactJwt = RedactJwt,
            RedactBearer = RedactBearer,
            RedactBasic = RedactBasic,
            RedactJsonProperties = RedactJsonProperties,
            RedactKnownValuesEverywhere = RedactKnownValuesEverywhere,
            DropAllResponseBodies = DropAllResponseBodies,
            HashValues = HashValues,
            Salt = Salt,
            MinimumKnownValueLength = MinimumKnownValueLength,
        };
        c.HeaderNames.Clear();
        c.HeaderNames.UnionWith(HeaderNames);
        c.ParameterNames.Clear();
        c.ParameterNames.UnionWith(ParameterNames);
        c.UserPatterns.AddRange(UserPatterns);
        c.DropBodyMimeTypes.UnionWith(DropBodyMimeTypes);
        return c;
    }
}

public enum RedactionKind
{
    Header,
    Cookie,
    UrlParameter,
    FormParameter,
    JsonProperty,
    Jwt,
    Bearer,
    BasicCredentials,
    Pattern,
    KnownValue,
    BodyDropped,
}

/// <summary>One redaction, listed in the preview before anything is written. The original is shown masked.</summary>
public sealed record Redaction(int EntryId, string Location, RedactionKind Kind, string Rule, string MaskedOriginal, string Replacement);
