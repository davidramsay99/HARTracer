namespace HarLens.Core.Curl;

internal enum CurlOptionKind
{
    Request,
    Header,
    Data,
    DataRaw,
    DataBinary,
    DataUrlEncode,
    Json,
    Form,
    FormString,
    Get,
    User,
    OAuth2Bearer,
    Cookie,
    UserAgent,
    Referer,
    Head,
    Location,
    MaxRedirs,
    Insecure,
    Compressed,
    MaxTime,
    ConnectTimeout,
    Proxy,
    Resolve,
    ConnectTo,
    Http10,
    Http11,
    Http2,
    Http2PriorKnowledge,
    Http3,
    Cert,
    CertType,
    Key,
    Pass,
    Url,
    Globoff,
    Variable,
    Next,

    /// <summary>Affects only what curl prints or saves; accepted silently (SPEC 7.2).</summary>
    OutputOnly,

    /// <summary>A real curl option that HarLens does not model; ignored with a warning.</summary>
    Unsupported,
}

/// <summary>A curl command-line option.</summary>
/// <param name="LongName">Long name without the leading dashes.</param>
/// <param name="Short">Short letter, if any.</param>
/// <param name="Kind">What the parser does with it.</param>
/// <param name="TakesArgument">True when the next argument (or the rest of a short-option bundle) is its value.</param>
internal sealed record CurlOption(string LongName, char? Short, CurlOptionKind Kind, bool TakesArgument)
{
    /// <summary>Boolean options accept a <c>--no-</c> prefix.</summary>
    public bool IsBoolean => !TakesArgument;
}

/// <summary>
/// The curl options HarLens knows. Unsupported options are listed too, so that an option's argument is skipped
/// rather than mistaken for the URL. Options missing from this table are reported as unknown and assumed to take no
/// argument. curl has no <c>--option=value</c> form and long names must match exactly.
/// </summary>
internal static class CurlOptionTable
{
    private static readonly Dictionary<string, CurlOption> ByLong = new(StringComparer.Ordinal);
    private static readonly Dictionary<char, CurlOption> ByShort = [];

    static CurlOptionTable()
    {
        void Add(string longName, char? shortName, CurlOptionKind kind, bool arg)
        {
            var option = new CurlOption(longName, shortName, kind, arg);
            ByLong[longName] = option;
            if (shortName is char s)
            {
                ByShort[s] = option;
            }
        }

        // Supported (SPEC 7.2), plus --form-string, --oauth2-bearer, --cert-type, --pass, --globoff and --variable.
        Add("request", 'X', CurlOptionKind.Request, true);
        Add("header", 'H', CurlOptionKind.Header, true);
        Add("data", 'd', CurlOptionKind.Data, true);
        Add("data-ascii", null, CurlOptionKind.Data, true);
        Add("data-raw", null, CurlOptionKind.DataRaw, true);
        Add("data-binary", null, CurlOptionKind.DataBinary, true);
        Add("data-urlencode", null, CurlOptionKind.DataUrlEncode, true);
        Add("json", null, CurlOptionKind.Json, true);
        Add("form", 'F', CurlOptionKind.Form, true);
        Add("form-string", null, CurlOptionKind.FormString, true);
        Add("get", 'G', CurlOptionKind.Get, false);
        Add("user", 'u', CurlOptionKind.User, true);
        Add("oauth2-bearer", null, CurlOptionKind.OAuth2Bearer, true);
        Add("cookie", 'b', CurlOptionKind.Cookie, true);
        Add("user-agent", 'A', CurlOptionKind.UserAgent, true);
        Add("referer", 'e', CurlOptionKind.Referer, true);
        Add("head", 'I', CurlOptionKind.Head, false);
        Add("location", 'L', CurlOptionKind.Location, false);
        Add("max-redirs", null, CurlOptionKind.MaxRedirs, true);
        Add("insecure", 'k', CurlOptionKind.Insecure, false);
        Add("compressed", null, CurlOptionKind.Compressed, false);
        Add("max-time", 'm', CurlOptionKind.MaxTime, true);
        Add("connect-timeout", null, CurlOptionKind.ConnectTimeout, true);
        Add("proxy", 'x', CurlOptionKind.Proxy, true);
        Add("resolve", null, CurlOptionKind.Resolve, true);
        Add("connect-to", null, CurlOptionKind.ConnectTo, true);
        Add("http1.0", '0', CurlOptionKind.Http10, false);
        Add("http1.1", null, CurlOptionKind.Http11, false);
        Add("http2", null, CurlOptionKind.Http2, false);
        Add("http2-prior-knowledge", null, CurlOptionKind.Http2PriorKnowledge, false);
        Add("http3", null, CurlOptionKind.Http3, false);
        Add("http3-only", null, CurlOptionKind.Http3, false);
        Add("cert", 'E', CurlOptionKind.Cert, true);
        Add("cert-type", null, CurlOptionKind.CertType, true);
        Add("key", null, CurlOptionKind.Key, true);
        Add("pass", null, CurlOptionKind.Pass, true);
        Add("url", null, CurlOptionKind.Url, true);
        Add("globoff", 'g', CurlOptionKind.Globoff, false);
        Add("variable", null, CurlOptionKind.Variable, true);
        Add("next", ':', CurlOptionKind.Next, false);

        // Output only: accepted and ignored without a warning.
        Add("silent", 's', CurlOptionKind.OutputOnly, false);
        Add("verbose", 'v', CurlOptionKind.OutputOnly, false);
        Add("include", 'i', CurlOptionKind.OutputOnly, false);
        Add("output", 'o', CurlOptionKind.OutputOnly, true);
        Add("write-out", 'w', CurlOptionKind.OutputOnly, true);
        Add("show-error", 'S', CurlOptionKind.OutputOnly, false);

        // Unsupported options that take an argument.
        foreach (var (name, letter) in new (string, char?)[]
        {
            ("abstract-unix-socket", null), ("alt-svc", null), ("aws-sigv4", null), ("cacert", null), ("capath", null),
            ("ciphers", null), ("config", 'K'), ("continue-at", 'C'), ("cookie-jar", 'c'), ("create-file-mode", null),
            ("crlfile", null), ("curves", null), ("delegation", null), ("dns-interface", null), ("dns-ipv4-addr", null),
            ("dns-ipv6-addr", null), ("dns-servers", null), ("doh-url", null), ("dump-header", 'D'), ("ech", null),
            ("egd-file", null), ("engine", null), ("etag-compare", null), ("etag-save", null), ("expect100-timeout", null),
            ("ftp-account", null), ("ftp-alternative-to-user", null), ("ftp-method", null), ("ftp-port", 'P'),
            ("ftp-ssl-ccc-mode", null), ("happy-eyeballs-timeout-ms", null), ("haproxy-clientip", null),
            ("hostpubmd5", null), ("hostpubsha256", null), ("hsts", null), ("interface", null), ("ip-tos", null),
            ("ipfs-gateway", null), ("keepalive-cnt", null), ("keepalive-time", null), ("key-type", null), ("krb", null),
            ("libcurl", null), ("limit-rate", null), ("local-port", null), ("login-options", null), ("mail-auth", null),
            ("mail-from", null), ("mail-rcpt", null), ("max-filesize", null), ("netrc-file", null), ("noproxy", null),
            ("output-dir", null), ("parallel-max", null), ("pinnedpubkey", null), ("preproxy", null), ("proto", null),
            ("proto-default", null), ("proto-redir", null), ("proxy-cacert", null), ("proxy-capath", null),
            ("proxy-cert", null), ("proxy-cert-type", null), ("proxy-ciphers", null), ("proxy-crlfile", null),
            ("proxy-header", null), ("proxy-key", null), ("proxy-key-type", null), ("proxy-pass", null),
            ("proxy-pinnedpubkey", null), ("proxy-service-name", null), ("proxy-tls13-ciphers", null),
            ("proxy-tlsauthtype", null), ("proxy-tlspassword", null), ("proxy-tlsuser", null), ("proxy-user", 'U'),
            ("proxy1.0", null), ("quote", 'Q'), ("random-file", null), ("range", 'r'), ("rate", null),
            ("request-target", null), ("retry", null), ("retry-delay", null), ("retry-max-time", null),
            ("sasl-authzid", null), ("service-name", null), ("sigalgs", null), ("socks4", null), ("socks4a", null),
            ("socks5", null), ("socks5-gssapi-service", null), ("socks5-hostname", null), ("speed-limit", 'Y'),
            ("speed-time", 'y'), ("ssl-sessions", null), ("stderr", null), ("telnet-option", 't'),
            ("tftp-blksize", null), ("time-cond", 'z'), ("tls-max", null), ("tls13-ciphers", null),
            ("tlsauthtype", null), ("tlspassword", null), ("tlsuser", null), ("trace", null), ("trace-ascii", null),
            ("trace-config", null), ("unix-socket", null), ("upload-file", 'T'), ("url-query", null),
            ("vlan-priority", null), ("knownhosts", null), ("upload-flags", null),
        })
        {
            Add(name, letter, CurlOptionKind.Unsupported, true);
        }

        // Unsupported boolean options.
        foreach (var (name, letter) in new (string, char?)[]
        {
            ("anyauth", null), ("append", 'a'), ("help", 'h'), ("basic", null), ("ca-native", null), ("cert-status", null),
            ("compressed-ssh", null), ("create-dirs", null), ("crlf", null), ("digest", null), ("disable", 'q'),
            ("disable-eprt", null), ("disable-epsv", null), ("disallow-username-in-url", null), ("doh-cert-status", null),
            ("doh-insecure", null), ("eprt", null), ("epsv", null), ("fail", 'f'), ("fail-early", null),
            ("fail-with-body", null), ("false-start", null), ("form-escape", null), ("ftp-create-dirs", null),
            ("ftp-pasv", null), ("ftp-pret", null), ("ftp-skip-pasv-ip", null), ("ftp-ssl-ccc", null),
            ("ftp-ssl-control", null), ("http0.9", null), ("ignore-content-length", null), ("ipv4", '4'), ("ipv6", '6'),
            ("junk-session-cookies", 'j'), ("keepalive", null), ("list-only", 'l'), ("location-trusted", null),
            ("mail-rcpt-allowfails", null), ("manual", 'M'), ("metalink", null), ("mptcp", null), ("negotiate", null),
            ("netrc", 'n'), ("netrc-optional", null), ("no-buffer", 'N'), ("no-clobber", null), ("no-keepalive", null),
            ("no-progress-meter", null), ("no-sessionid", null), ("ntlm", null), ("ntlm-wb", null), ("parallel", 'Z'),
            ("parallel-immediate", null), ("path-as-is", null), ("progress-bar", '#'), ("proxy-anyauth", null),
            ("proxy-basic", null), ("proxy-ca-native", null), ("proxy-digest", null), ("proxy-http2", null),
            ("proxy-insecure", null), ("proxy-negotiate", null), ("proxy-ntlm", null), ("proxy-ssl-allow-beast", null),
            ("proxy-ssl-auto-client-cert", null), ("proxy-tlsv1", null), ("proxytunnel", 'p'), ("raw", null),
            ("remote-header-name", 'J'), ("remote-name", 'O'), ("remote-name-all", null), ("remote-time", 'R'),
            ("remove-on-error", null), ("retry-all-errors", null), ("retry-connrefused", null), ("sasl-ir", null),
            ("sessionid", null), ("skip-existing", null), ("socks5-basic", null), ("socks5-gssapi", null),
            ("socks5-gssapi-nec", null), ("ssl", null), ("ssl-allow-beast", null), ("ssl-auto-client-cert", null),
            ("ssl-no-revoke", null), ("ssl-reqd", null), ("ssl-revoke-best-effort", null), ("sslv2", '2'),
            ("sslv3", '3'), ("styled-output", null), ("suppress-connect-headers", null), ("tcp-fastopen", null),
            ("tcp-nodelay", null), ("tftp-no-options", null), ("tlsv1", '1'), ("tlsv1.0", null), ("tlsv1.1", null),
            ("tlsv1.2", null), ("tlsv1.3", null), ("tr-encoding", null), ("trace-ids", null), ("trace-time", null),
            ("use-ascii", 'B'), ("version", 'V'), ("xattr", null), ("out-null", null), ("dump-ca-embed", null),
        })
        {
            Add(name, letter, CurlOptionKind.Unsupported, false);
        }
    }

    public static CurlOption? FindLong(string name) => ByLong.GetValueOrDefault(name);

    public static CurlOption? FindShort(char letter) => ByShort.GetValueOrDefault(letter);
}
