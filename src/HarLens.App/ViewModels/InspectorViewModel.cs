using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarLens.App.Services;
using HarLens.Core.Composer;
using HarLens.Core.Har;
using HarLens.Core.Http;
using HarLens.Core.Model;
using HarLens.Core.Sanitize;

namespace HarLens.App.ViewModels;

public sealed record HeaderRow(int Index, string Name, string Value)
{
    public string Both => $"{Name}: {Value}";
}

public sealed record GeneralRow(string Label, string Value);

public sealed record TimingRow(string Phase, string Value, double Offset, double Duration, string BrushKey, string Explanation)
{
    public double BarLeft { get; init; }

    public double BarWidth { get; init; }
}

/// <summary>The inspector for the selected entry: request side, response side and entry-level tabs (SPEC 6.3).</summary>
public sealed partial class InspectorViewModel : ObservableObject, IDisposable
{
    private const double TimingBarWidth = 360;
    private readonly SessionViewModel _owner;
    private CancellationTokenSource? _cts;

    public InspectorViewModel(SessionViewModel owner)
    {
        _owner = owner;
    }

    public MessageViewModel Request { get; } = new(isRequest: true);

    public MessageViewModel Response { get; } = new(isRequest: false);

    [ObservableProperty]
    public partial EntryRowViewModel? Row { get; set; }

    [ObservableProperty]
    public partial bool HasEntry { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<GeneralRow> General { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TimingRow> Timings { get; set; } = [];

    [ObservableProperty]
    public partial string TimingSummary { get; set; } = "";

    [ObservableProperty]
    public partial string? InitiatorText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<WebSocketMessage> WebSocketFrames { get; set; } = [];

    [ObservableProperty]
    public partial string? CacheText { get; set; }

    [ObservableProperty]
    public partial string RawEntryJson { get; set; } = "";

    [ObservableProperty]
    public partial bool IsComposerEntry { get; set; }

    public bool HasInitiator => InitiatorText is not null;

    public bool HasWebSocket => WebSocketFrames.Count > 0;

    public string? Comment
    {
        get => Row?.Comment;
        set
        {
            if (Row is not null)
            {
                Row.Comment = value;
                OnPropertyChanged();
            }
        }
    }

    public string? ColorMark
    {
        get => Row?.ColorMark;
        set
        {
            if (Row is not null)
            {
                Row.ColorMark = value == "none" ? null : value;
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyList<string> ColorChoices { get; } = ["none", .. ColorMarks.All];

    public async Task ShowAsync(EntryRowViewModel? row)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Row = row;
        HasEntry = row is not null;
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(ColorMark));
        if (row is null)
        {
            Clear();
            return;
        }

        var entry = row.Entry;
        var mask = AppServices.Current.Settings.MaskSecrets;
        try
        {
            var loaded = await Task.Run(() => Load(entry, mask), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Apply(entry, loaded);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            RawEntryJson = "The entry could not be read: " + ex.Message;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(ColorMark));
        if (Row is not null)
        {
            _ = ShowAsync(Row);
        }
    }

    [RelayCommand]
    private void CopyHeader(HeaderRow? row) => Ui.Copy(row?.Both);

    [RelayCommand]
    private void CopyHeaderName(HeaderRow? row) => Ui.Copy(row?.Name);

    [RelayCommand]
    private void CopyHeaderValue(HeaderRow? row) => Ui.Copy(row?.Value);

    [RelayCommand]
    private void DiffAgainstOrigin()
    {
        if (Row?.Entry is { } entry && _owner.IsComposer)
        {
            MainViewModel.Instance?.DiffComposerEntryAgainstOrigin(entry);
        }
    }

    private sealed record Loaded(
        DecodedBody? RequestBody,
        DecodedBody? ResponseBody,
        List<HarHeader> Query,
        List<CookieInfo> RequestCookies,
        List<CookieInfo> ResponseCookies,
        List<HarHeader> PostParams,
        string? Initiator,
        List<WebSocketMessage> Frames,
        string? Cache,
        string RawJson,
        string RequestRaw,
        string ResponseRaw,
        bool IsComposer,
        string? Tls,
        string? Notices);

    private static Loaded Load(HarEntry entry, bool mask)
    {
        var cache = AppServices.Current.BodyCache;
        var requestBody = cache.Get(entry, BodySide.Request);
        var responseBody = cache.Get(entry, BodySide.Response);
        using var detail = EntryDetail.Load(entry);
        string? cacheText = null;
        if (detail.CacheBeforeRequest is { } before)
        {
            cacheText = "beforeRequest\n" + EntryDetail.FormatJson(before);
        }

        if (detail.CacheAfterRequest is { } after)
        {
            cacheText = (cacheText is null ? "" : cacheText + "\n\n") + "afterRequest\n" + EntryDetail.FormatJson(after);
        }

        Func<string, string, string>? maskValue = mask ? SecretMasker.MaskHeaderValue : null;
        var requestRaw = RawMessage.Request(entry, requestBody, maskValue);
        var responseRaw = RawMessage.Response(entry, responseBody, maskValue);
        var harlens = EntryDetail.Child(detail.Root, "_harlens");
        var isComposer = EntryDetail.Child(harlens, "composer") is { ValueKind: System.Text.Json.JsonValueKind.True };
        if (isComposer && ExchangeConverter.ReadWire(entry) is { } wire)
        {
            // Composer entries show the exact bytes that crossed the wire (SPEC 7.4).
            requestRaw = WireText(wire.Sent);
            responseRaw = WireText(wire.Received);
        }

        var tls = EntryDetail.Child(harlens, "tls") is { } t ? EntryDetail.FormatJson(t) : null;
        var notices = EntryDetail.Child(harlens, "notices") is { } n ? string.Join("\n", n.EnumerateArray().Select(x => x.GetString())) : null;
        var raw = detail.FormatRawJson();
        return new Loaded(
            requestBody,
            responseBody,
            detail.QueryString,
            detail.RequestCookies,
            detail.ResponseCookies,
            detail.PostParams,
            detail.Initiator is { } i ? EntryDetail.FormatJson(i) : null,
            detail.WebSocketMessages,
            cacheText,
            mask ? SecretMasker.MaskText(raw) : raw,
            mask ? SecretMasker.MaskText(requestRaw) : requestRaw,
            mask ? SecretMasker.MaskText(responseRaw) : responseRaw,
            isComposer,
            tls,
            notices);
    }

    private static string WireText(byte[] bytes) =>
        System.Text.Unicode.Utf8.IsValid(bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : System.Text.Encoding.Latin1.GetString(bytes);

    private void Apply(HarEntry entry, Loaded loaded)
    {
        var mask = AppServices.Current.Settings.MaskSecrets;
        string M(string name, string value) => mask ? SecretMasker.MaskHeaderValue(name, value) : value;

        var general = new List<GeneralRow>
        {
            new("URL", mask ? SecretMasker.MaskText(entry.Url) : entry.Url),
            new("Method", entry.Method),
            new("Status", entry.Status == 0 ? $"(failed){(entry.Error is null ? "" : "  " + entry.Error)}" : $"{entry.Status} {entry.StatusText}".Trim()),
            new("Remote address", entry.ServerIPAddress ?? ""),
            new("HTTP version", RawMessage.Http1Version(string.IsNullOrEmpty(entry.ResponseHttpVersion) ? entry.RequestHttpVersion : entry.ResponseHttpVersion)),
            new("Connection ID", entry.Connection ?? ""),
            new("Started", DisplayFormat.Timestamp(entry.StartedDateTime)),
        };
        if (entry.ResourceType is not null)
        {
            general.Add(new GeneralRow("Resource type", entry.ResourceType));
        }

        if (entry.Priority is not null)
        {
            general.Add(new GeneralRow("Priority", entry.Priority));
        }

        if (entry.FromCache is not null)
        {
            general.Add(new GeneralRow("From cache", entry.FromCache));
        }

        if (entry.SecurityState is not null)
        {
            general.Add(new GeneralRow("Security state", entry.SecurityState));
        }

        if (entry.SourceTag is not null)
        {
            general.Add(new GeneralRow("Source file", entry.SourceTag));
        }

        if (loaded.Notices is not null)
        {
            general.Add(new GeneralRow("Notices", loaded.Notices));
        }

        General = general;
        IsComposerEntry = loaded.IsComposer;

        Request.Show(
            entry.RequestHeaders.Select((h, i) => new HeaderRow(i, h.Name, M(h.Name, h.Value))).ToList(),
            loaded.Query.Select(q => new HarHeader(q.Name, mask ? SecretMasker.MaskText(q.Name + "=" + q.Value)[(q.Name.Length + 1)..] : q.Value)).ToList(),
            loaded.RequestCookies.Select(c => mask ? Masked(c) : c).ToList(),
            loaded.RequestBody,
            entry.RequestMimeType ?? entry.GetRequestHeader("Content-Type") ?? "",
            loaded.RequestRaw,
            loaded.PostParams,
            null);
        Response.Show(
            entry.ResponseHeaders.Select((h, i) => new HeaderRow(i, h.Name, M(h.Name, h.Value))).ToList(),
            [],
            loaded.ResponseCookies.Select(c => mask ? Masked(c) : c).ToList(),
            loaded.ResponseBody,
            entry.MimeType,
            loaded.ResponseRaw,
            [],
            loaded.Tls);

        BuildTimings(entry);
        InitiatorText = loaded.Initiator;
        WebSocketFrames = loaded.Frames;
        CacheText = loaded.Cache;
        RawEntryJson = loaded.RawJson;
        OnPropertyChanged(nameof(HasInitiator));
        OnPropertyChanged(nameof(HasWebSocket));
        EntryShown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the inspector has shown an entry (search hits highlight once content is in place).</summary>
    public event EventHandler? EntryShown;

    private static CookieInfo Masked(CookieInfo c) => new()
    {
        Name = c.Name,
        Value = SecretMasker.Mask(c.Value),
        Domain = c.Domain,
        Path = c.Path,
        Expires = c.Expires,
        MaxAge = c.MaxAge,
        HttpOnly = c.HttpOnly,
        Secure = c.Secure,
        SameSite = c.SameSite,
        Partitioned = c.Partitioned,
        Other = c.Other,
    };

    private void BuildTimings(HarEntry entry)
    {
        var t = entry.Timings;
        var explanations = new (string Phase, double Value, string Brush, string Text)[]
        {
            ("Blocked", t.Blocked, "PhaseBlocked", "Time spent in a queue waiting for a network connection (connection limits, priority, proxy negotiation)."),
            ("DNS", t.Dns, "PhaseDns", "DNS resolution of the host name. -1 when no lookup happened (reused connection, IP literal, cache)."),
            ("Connect", t.TcpConnect, "PhaseConnect", "TCP connection setup. HAR includes TLS inside 'connect'; it is shown separately here without double counting."),
            ("TLS", t.Ssl, "PhaseTls", "TLS handshake, part of the HAR 'connect' time."),
            ("Send", t.Send, "PhaseSend", "Time to send the request to the server."),
            ("Wait", t.Wait, "PhaseWait", "Waiting for the first byte of the response (time to first byte): server processing plus one round trip."),
            ("Receive", t.Receive, "PhaseReceive", "Time to read the entire response from the network."),
        };
        var total = Math.Max(1, entry.TotalTime);
        var scale = TimingBarWidth / total;
        double offset = 0;
        var rows = new List<TimingRow>();
        foreach (var (phase, value, brush, text) in explanations)
        {
            var applicable = value >= 0;
            rows.Add(new TimingRow(phase, applicable ? $"{value.ToString("0.###", CultureInfo.CurrentCulture)} ms" : "n/a", offset, Math.Max(0, value), brush, text)
            {
                BarLeft = Math.Min(TimingBarWidth, offset * scale),
                BarWidth = applicable ? Math.Max(value > 0 ? 1 : 0, Math.Min(TimingBarWidth - (offset * scale), value * scale)) : 0,
            });
            if (applicable)
            {
                offset += value;
            }
        }

        Timings = rows;
        TimingSummary = $"Total {DisplayFormat.Duration(entry.TotalTime)}" + (entry.Time >= 0 && Math.Abs(entry.Time - entry.Timings.Total) > 1
            ? $"  (HAR 'time' {entry.Time:0.###} ms; sum of phases {entry.Timings.Total:0.###} ms)"
            : "");
    }

    private void Clear()
    {
        General = [];
        Timings = [];
        InitiatorText = null;
        WebSocketFrames = [];
        CacheText = null;
        RawEntryJson = "";
        Request.Clear();
        Response.Clear();
        OnPropertyChanged(nameof(HasInitiator));
        OnPropertyChanged(nameof(HasWebSocket));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Request.Body.Dispose();
        Response.Body.Dispose();
    }
}

/// <summary>Request side or response side tabs: Headers, Query, Cookies, Body, Raw (SPEC 6.3).</summary>
public sealed partial class MessageViewModel : ObservableObject
{
    public MessageViewModel(bool isRequest)
    {
        IsRequest = isRequest;
    }

    public bool IsRequest { get; }

    public BodyViewModel Body { get; } = new();

    [ObservableProperty]
    public partial IReadOnlyList<HeaderRow> Headers { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<HarHeader> Query { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<CookieInfo> Cookies { get; set; } = [];

    [ObservableProperty]
    public partial string RawText { get; set; } = "";

    [ObservableProperty]
    public partial string? TlsText { get; set; }

    [ObservableProperty]
    public partial string HeaderFilter { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<HeaderRow> FilteredHeaders { get; set; } = [];

    public void Show(IReadOnlyList<HeaderRow> headers, IReadOnlyList<HarHeader> query, IReadOnlyList<CookieInfo> cookies,
        DecodedBody? body, string mime, string raw, IReadOnlyList<HarHeader> postParams, string? tls)
    {
        Headers = headers;
        Query = query;
        Cookies = cookies;
        RawText = raw;
        TlsText = tls;
        ApplyHeaderFilter();
        Body.Load(body, mime, postParams, headers);
    }

    public void Clear()
    {
        Headers = [];
        FilteredHeaders = [];
        Query = [];
        Cookies = [];
        RawText = "";
        TlsText = null;
        Body.Load(null, "", [], []);
    }

    partial void OnHeaderFilterChanged(string value) => ApplyHeaderFilter();

    private void ApplyHeaderFilter() =>
        FilteredHeaders = string.IsNullOrWhiteSpace(HeaderFilter)
            ? Headers
            : Headers.Where(h => h.Name.Contains(HeaderFilter, StringComparison.OrdinalIgnoreCase) || h.Value.Contains(HeaderFilter, StringComparison.OrdinalIgnoreCase)).ToList();
}
