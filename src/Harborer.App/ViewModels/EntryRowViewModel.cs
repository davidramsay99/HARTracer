using CommunityToolkit.Mvvm.ComponentModel;
using Harborer.App.Services;
using Harborer.Core.Har;
using Harborer.Core.Http;
using Harborer.Core.Sanitize;

namespace Harborer.App.ViewModels;

/// <summary>One row of the session list. Display strings are computed on access so recycled rows stay cheap.</summary>
public sealed class EntryRowViewModel : ObservableObject
{
    private readonly SessionViewModel _owner;
    private Dictionary<string, string?>? _fieldCache;

    public EntryRowViewModel(HarEntry entry, SessionViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public HarEntry Entry { get; }

    public int Id => Entry.Id;

    public int FileIndex => Entry.FileIndex;

    public int Status => Entry.Status;

    public string StatusDisplay => Entry.Status == 0 ? "—" : Entry.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Method => Entry.Method;

    public string Protocol => RawMessage.Http1Version(string.IsNullOrEmpty(Entry.ResponseHttpVersion) ? Entry.RequestHttpVersion : Entry.ResponseHttpVersion);

    public string Host => Entry.HostDisplay;

    public string Path => Mask(Entry.Path);

    public string Url => Mask(Entry.Url);

    public string MimeType => Entry.MimeTypeBase;

    public long ResponseSize => Entry.ResponseSize;

    public string ResponseSizeText => DisplayFormat.Size(Entry.ResponseSize);

    public double TotalTime => Entry.TotalTime;

    public string TotalTimeText => DisplayFormat.Duration(Entry.TotalTime);

    public DateTimeOffset Started => Entry.StartedDateTime;

    public string StartedText => DisplayFormat.Started(Entry.StartedDateTime, _owner.FirstStart);

    public string? SourceTag => Entry.SourceTag;

    public string? PageRef => Entry.PageRef;

    public string PageGroup => _owner.PageGroupLabel(Entry);

    /// <summary>"failed", "5xx", "4xx", "3xx" or "ok", for row styling.</summary>
    public string RowState => Entry.IsFailed ? "failed" : Entry.StatusClass switch
    {
        5 => "5xx",
        4 => "4xx",
        3 => "3xx",
        _ => "ok",
    };

    public string ToolTipText => Entry.Error is { } error ? $"{Url}\n{error}" : Url;

    public DateTimeOffset AxisStart => _owner.FirstStart;

    public double AxisMilliseconds => _owner.AxisMilliseconds;

    public string? Comment
    {
        get => Entry.Comment;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (normalized != Entry.Comment)
            {
                Entry.Comment = normalized;
                Entry.AnnotationsDirty = true;
                OnPropertyChanged();
                _owner.OnAnnotationChanged();
            }
        }
    }

    public string? ColorMark
    {
        get => Entry.ColorMark;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (normalized != Entry.ColorMark)
            {
                Entry.ColorMark = normalized;
                Entry.AnnotationsDirty = true;
                OnPropertyChanged();
                _owner.OnAnnotationChanged();
            }
        }
    }

    /// <summary>Custom column values, keyed by <c>kind:name</c>, for example <c>response-header:x-azure-ref</c>.</summary>
    public string? this[string key]
    {
        get
        {
            var colon = key.IndexOf(':');
            if (colon < 0)
            {
                return null;
            }

            var kind = key[..colon];
            var name = key[(colon + 1)..];
            string? value = kind switch
            {
                "request-header" => Entry.GetRequestHeader(name),
                "response-header" => Entry.GetResponseHeader(name),
                "header" => Entry.GetResponseHeader(name) ?? Entry.GetRequestHeader(name),
                "field" => Field(name),
                _ => null,
            };
            return value is null ? null : AppServices.Current.Settings.MaskSecrets ? SecretMasker.MaskHeaderValue(name, value) : value;
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private string Mask(string text) => AppServices.Current.Settings.MaskSecrets ? SecretMasker.MaskText(text) : text;

    private string? Field(string name)
    {
        // Indexed vendor fields first; anything else is read from the entry JSON once and cached.
        var known = name switch
        {
            "_priority" => Entry.Priority,
            "_resourceType" => Entry.ResourceType,
            "_fromCache" => Entry.FromCache,
            "_transferSize" => Entry.TransferSize >= 0 ? Entry.TransferSize.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            "_error" => Entry.Error,
            "_securityState" => Entry.SecurityState,
            "_initiator" or "_initiator.type" => Entry.InitiatorType,
            "serverIPAddress" => Entry.ServerIPAddress,
            "connection" => Entry.Connection,
            "pageref" => Entry.PageRef,
            "comment" => Entry.Comment,
            _ => null,
        };
        if (known is not null)
        {
            return known;
        }

        _fieldCache ??= [];
        if (_fieldCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        try
        {
            using var detail = EntryDetail.Load(Entry);
            cached = detail.GetField(name);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            cached = null;
        }

        _fieldCache[name] = cached;
        return cached;
    }
}
