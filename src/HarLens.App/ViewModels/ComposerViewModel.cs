using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarLens.App.Services;
using HarLens.App.Views;
using HarLens.Core.Composer;
using HarLens.Core.Curl;
using HarLens.Core.Engine;
using HarLens.Core.Har;
using HarLens.Core.Http;
using HarLens.Core.Model;
using HarLens.Core.Settings;

namespace HarLens.App.ViewModels;

public sealed partial class EditableRow : ObservableObject
{
    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Value { get; set; } = "";
}

public sealed partial class PartRow : ObservableObject
{
    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial string FilePath { get; set; } = "";

    [ObservableProperty]
    public partial string FileName { get; set; } = "";

    [ObservableProperty]
    public partial string ContentType { get; set; } = "";
}

public sealed partial class OverrideRow : ObservableObject
{
    [ObservableProperty]
    public partial ConnectOverrideKind Kind { get; set; }

    [ObservableProperty]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial string Port { get; set; } = "";

    [ObservableProperty]
    public partial string TargetHost { get; set; } = "";

    [ObservableProperty]
    public partial string TargetPort { get; set; } = "";
}

/// <summary>The Request Composer (SPEC 7). Sending is impossible while Offline Mode is ON (SPEC 3.3).</summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _syncingQuery;
    private CancellationTokenSource? _sendCts;

    public ComposerViewModel(MainViewModel main)
    {
        _main = main;
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.OfflineMode))
            {
                OnPropertyChanged(nameof(OfflineMode));
                OnPropertyChanged(nameof(CanSend));
                SendCommand.NotifyCanExecuteChanged();
            }
        };
        QueryParams.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (EditableRow row in e.NewItems)
                {
                    row.PropertyChanged += OnQueryRowChanged;
                }
            }

            OnQueryRowChanged(null, new PropertyChangedEventArgs(null));
        };
        TimeoutSeconds = AppServices.Current.Settings.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ReloadHistory();
        ReloadCollections();
    }

    public static IReadOnlyList<string> Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    public static IReadOnlyList<HttpVersionPreference> Versions { get; } = Enum.GetValues<HttpVersionPreference>();

    public static IReadOnlyList<BodyMode> BodyModes { get; } = Enum.GetValues<BodyMode>();

    public static IReadOnlyList<ClientCertificateSource> CertificateSources { get; } = Enum.GetValues<ClientCertificateSource>();

    public static IReadOnlyList<ConnectOverrideKind> OverrideKinds { get; } = Enum.GetValues<ConnectOverrideKind>();

    public bool OfflineMode => _main.OfflineMode;

    public bool CanSend => !OfflineMode && !IsSending;

    [ObservableProperty]
    public partial string Method { get; set; } = "GET";

    [ObservableProperty]
    public partial string Url { get; set; } = "https://";

    [ObservableProperty]
    public partial HttpVersionPreference HttpVersion { get; set; } = HttpVersionPreference.Default;

    public ObservableCollection<EditableRow> Headers { get; } = [];

    public ObservableCollection<EditableRow> QueryParams { get; } = [];

    [ObservableProperty]
    public partial BodyMode BodyMode { get; set; } = BodyMode.None;

    [ObservableProperty]
    public partial string BodyText { get; set; } = "";

    /// <summary>Non-text body bytes carried over from a HAR entry; shown as a summary, sent unchanged.</summary>
    [ObservableProperty]
    public partial byte[]? BodyBytes { get; set; }

    public ObservableCollection<EditableRow> FormFields { get; } = [];

    public ObservableCollection<PartRow> Parts { get; } = [];

    [ObservableProperty]
    public partial string BinaryFilePath { get; set; } = "";

    // Auth helper (SPEC 7.1): writes the Authorization header and nothing else.
    [ObservableProperty]
    public partial string AuthUser { get; set; } = "";

    [ObservableProperty]
    public partial string AuthPassword { get; set; } = "";

    [ObservableProperty]
    public partial string AuthToken { get; set; } = "";

    // Options.
    [ObservableProperty]
    public partial bool FollowRedirects { get; set; }

    [ObservableProperty]
    public partial string MaxRedirects { get; set; } = "50";

    [ObservableProperty]
    public partial string TimeoutSeconds { get; set; } = "100";

    [ObservableProperty]
    public partial string ConnectTimeoutSeconds { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoDecompress { get; set; } = true;

    [ObservableProperty]
    public partial bool Insecure { get; set; }

    [ObservableProperty]
    public partial bool UseClientCertificate { get; set; }

    [ObservableProperty]
    public partial ClientCertificateSource CertificateSource { get; set; }

    [ObservableProperty]
    public partial string CertificatePath { get; set; } = "";

    [ObservableProperty]
    public partial string CertificatePassword { get; set; } = "";

    [ObservableProperty]
    public partial string CertificateKeyPath { get; set; } = "";

    [ObservableProperty]
    public partial string CertificateThumbprint { get; set; } = "";

    [ObservableProperty]
    public partial string Proxy { get; set; } = "";

    public ObservableCollection<OverrideRow> Overrides { get; } = [];

    [ObservableProperty]
    public partial string FileBaseDirectory { get; set; } = "";

    // Results.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsSending { get; set; }

    [ObservableProperty]
    public partial string ResultSummary { get; set; } = "";

    [ObservableProperty]
    public partial bool ResultIsError { get; set; }

    [ObservableProperty]
    public partial string? ImportWarnings { get; set; }

    [ObservableProperty]
    public partial HarEntry? Origin { get; set; }

    [ObservableProperty]
    public partial HarEntry? LastResult { get; set; }

    public bool CanDiff => Origin is not null && LastResult is not null;

    public ObservableCollection<HistoryItem> History { get; } = [];

    public ObservableCollection<RequestCollection> Collections { get; } = [];

    [ObservableProperty]
    public partial HistoryItem? SelectedHistory { get; set; }

    [ObservableProperty]
    public partial bool KeepCredentialsInHistory { get; set; }

    partial void OnLastResultChanged(HarEntry? value) => OnPropertyChanged(nameof(CanDiff));

    partial void OnOriginChanged(HarEntry? value) => OnPropertyChanged(nameof(CanDiff));

    // ------------------------------------------------------------------ load and build

    public void LoadFromEntry(HarEntry entry)
    {
        var body = AppServices.Current.BodyCache.Get(entry, BodySide.Request);
        LoadSpec(RequestFactory.FromEntry(entry, body));
        Origin = entry;
        ImportWarnings = null;
        ResultSummary = $"Loaded from #{entry.Id} ({entry.Source.DisplayName}). Captured credentials are still present; you will be asked before they are sent.";
        ResultIsError = false;
    }

    public void LoadSpec(HttpRequestSpec spec)
    {
        Origin = null;
        Method = spec.Method;
        HttpVersion = spec.HttpVersion;
        Headers.Clear();
        foreach (var h in spec.Headers)
        {
            Headers.Add(new EditableRow { Name = h.Name, Value = h.Value, Enabled = h.Enabled });
        }

        Url = spec.Url;
        BodyMode = spec.Body.Mode;
        BodyText = spec.Body.Text ?? "";
        BodyBytes = spec.Body.Bytes;
        FormFields.Clear();
        if (spec.Body.Mode == BodyMode.FormUrlEncoded)
        {
            foreach (var f in FormUrlEncoding.Parse(spec.Body.Text))
            {
                FormFields.Add(new EditableRow { Name = f.Name, Value = f.Value });
            }
        }

        Parts.Clear();
        foreach (var p in spec.Body.Parts)
        {
            Parts.Add(new PartRow
            {
                Enabled = p.Enabled,
                Name = p.Name,
                Value = p.Value ?? "",
                FilePath = p.FilePath ?? "",
                FileName = p.FileName ?? "",
                ContentType = p.ContentType ?? "",
            });
        }

        BinaryFilePath = spec.Body.FilePath ?? "";
        var o = spec.Options;
        FollowRedirects = o.FollowRedirects;
        MaxRedirects = o.MaxRedirects.ToString(CultureInfo.InvariantCulture);
        TimeoutSeconds = o.Timeout is { } t ? t.TotalSeconds.ToString(CultureInfo.InvariantCulture) : AppServices.Current.Settings.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ConnectTimeoutSeconds = o.ConnectTimeout is { } ct ? ct.TotalSeconds.ToString(CultureInfo.InvariantCulture) : "";
        AutoDecompress = o.AutoDecompress;
        Insecure = o.Insecure;
        Proxy = o.Proxy ?? "";
        UseClientCertificate = o.ClientCertificate is not null;
        if (o.ClientCertificate is { } cert)
        {
            CertificateSource = cert.Source;
            CertificatePath = cert.Path ?? "";
            CertificatePassword = cert.Password ?? "";
            CertificateKeyPath = cert.KeyPath ?? "";
            CertificateThumbprint = cert.Thumbprint ?? "";
        }

        Overrides.Clear();
        foreach (var ov in o.ConnectOverrides)
        {
            Overrides.Add(new OverrideRow
            {
                Kind = ov.Kind,
                Host = ov.Host,
                Port = ov.Port == 0 ? "" : ov.Port.ToString(CultureInfo.InvariantCulture),
                TargetHost = ov.TargetHost,
                TargetPort = ov.TargetPort == 0 ? "" : ov.TargetPort.ToString(CultureInfo.InvariantCulture),
            });
        }
    }

    public HttpRequestSpec BuildRequest()
    {
        var spec = new HttpRequestSpec
        {
            Method = string.IsNullOrWhiteSpace(Method) ? "GET" : Method.Trim(),
            Url = Url.Trim(),
            HttpVersion = HttpVersion,
            OriginKey = Origin?.Key,
        };
        foreach (var h in Headers.Where(h => h.Name.Length > 0))
        {
            spec.Headers.Add(new HeaderEntry(h.Name.Trim(), h.Value, h.Enabled));
        }

        spec.Body = BodyMode switch
        {
            BodyMode.None => RequestBody.None(),
            BodyMode.FormUrlEncoded => RequestBody.FromText(
                FormUrlEncoding.Serialize(FormFields.Where(f => f.Enabled && f.Name.Length > 0).Select(f => new HarHeader(f.Name, f.Value))),
                BodyMode.FormUrlEncoded),
            BodyMode.Multipart => new RequestBody
            {
                Mode = BodyMode.Multipart,
                Parts = Parts.Where(p => p.Name.Length > 0).Select(p => new MultipartPart
                {
                    Enabled = p.Enabled,
                    Name = p.Name,
                    Value = p.FilePath.Length > 0 ? null : p.Value,
                    FilePath = p.FilePath.Length > 0 ? p.FilePath : null,
                    FileName = p.FileName.Length > 0 ? p.FileName : null,
                    ContentType = p.ContentType.Length > 0 ? p.ContentType : null,
                }).ToList(),
            },
            BodyMode.BinaryFile => new RequestBody { Mode = BodyMode.BinaryFile, FilePath = BinaryFilePath },
            _ when BodyBytes is not null => new RequestBody { Mode = BodyMode.Raw, Bytes = BodyBytes },
            _ => RequestBody.FromText(BodyText, BodyMode),
        };

        var o = spec.Options;
        o.FollowRedirects = FollowRedirects;
        o.MaxRedirects = int.TryParse(MaxRedirects, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mr) ? mr : 50;
        o.Timeout = Seconds(TimeoutSeconds);
        o.ConnectTimeout = Seconds(ConnectTimeoutSeconds);
        o.AutoDecompress = AutoDecompress;
        o.Insecure = Insecure;
        o.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim();
        if (UseClientCertificate)
        {
            o.ClientCertificate = new ClientCertificateSpec
            {
                Source = CertificateSource,
                Path = Blank(CertificatePath),
                Password = Blank(CertificatePassword),
                KeyPath = Blank(CertificateKeyPath),
                Thumbprint = Blank(CertificateThumbprint),
            };
        }

        foreach (var row in Overrides.Where(r => r.TargetHost.Length > 0))
        {
            o.ConnectOverrides.Add(new ConnectOverride
            {
                Kind = row.Kind,
                Host = row.Host.Trim(),
                Port = int.TryParse(row.Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0,
                TargetHost = row.TargetHost.Trim().Trim('[', ']'),
                TargetPort = int.TryParse(row.TargetPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tp) ? tp : 0,
            });
        }

        return spec;
    }

    private static TimeSpan? Seconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : null;

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ------------------------------------------------------------------ URL and query grid, two-way (SPEC 7.1)

    partial void OnUrlChanged(string value)
    {
        if (_syncingQuery)
        {
            return;
        }

        _syncingQuery = true;
        try
        {
            QueryParams.Clear();
            foreach (var p in FormUrlEncoding.ParseQuery(value))
            {
                QueryParams.Add(new EditableRow { Name = p.Name, Value = p.Value });
            }
        }
        finally
        {
            _syncingQuery = false;
        }
    }

    private void OnQueryRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingQuery)
        {
            return;
        }

        _syncingQuery = true;
        try
        {
            var query = FormUrlEncoding.Serialize(QueryParams.Where(q => q.Enabled && q.Name.Length > 0).Select(q => new HarHeader(q.Name, q.Value)));
            Url = FormUrlEncoding.WithQuery(Url, query);
        }
        finally
        {
            _syncingQuery = false;
        }
    }

    // ------------------------------------------------------------------ auth helper

    [RelayCommand]
    private void ApplyBasic()
    {
        SetHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(AuthUser + ":" + AuthPassword)));
    }

    [RelayCommand]
    private void ApplyBearer()
    {
        if (!string.IsNullOrWhiteSpace(AuthToken))
        {
            SetHeader("Authorization", "Bearer " + AuthToken.Trim());
        }
    }

    private void SetHeader(string name, string value)
    {
        var existing = Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Headers.Add(new EditableRow { Name = name, Value = value });
        }
        else
        {
            existing.Value = value;
            existing.Enabled = true;
        }
    }

    [RelayCommand]
    private void AddHeader() => Headers.Add(new EditableRow());

    [RelayCommand]
    private void RemoveHeader(EditableRow? row)
    {
        if (row is not null)
        {
            Headers.Remove(row);
        }
    }

    [RelayCommand]
    private void AddQuery() => QueryParams.Add(new EditableRow());

    [RelayCommand]
    private void AddFormField() => FormFields.Add(new EditableRow());

    [RelayCommand]
    private void AddPart() => Parts.Add(new PartRow());

    [RelayCommand]
    private void AddOverride() => Overrides.Add(new OverrideRow { Host = UrlParts.Split(Url).Host });

    [RelayCommand]
    private void BrowseBinary()
    {
        var file = Ui.OpenFiles("Choose body file", "All files (*.*)|*.*", multiple: false).FirstOrDefault();
        if (file is not null)
        {
            BinaryFilePath = file;
        }
    }

    [RelayCommand]
    private void BrowsePartFile(PartRow? row)
    {
        var file = Ui.OpenFiles("Choose file for part", "All files (*.*)|*.*", multiple: false).FirstOrDefault();
        if (row is not null && file is not null)
        {
            row.FilePath = file;
        }
    }

    [RelayCommand]
    private void BrowseCertificate()
    {
        var file = Ui.OpenFiles("Choose client certificate", "Certificates (*.pfx;*.p12;*.pem;*.crt;*.cer)|*.pfx;*.p12;*.pem;*.crt;*.cer|All files (*.*)|*.*", multiple: false).FirstOrDefault();
        if (file is not null)
        {
            CertificatePath = file;
        }
    }

    [RelayCommand]
    private void BrowseBaseDirectory()
    {
        if (Ui.PickFolder("Base directory for @file references") is { } dir)
        {
            FileBaseDirectory = dir;
        }
    }

    [RelayCommand]
    private void New()
    {
        LoadSpec(new HttpRequestSpec { Url = "https://" });
        ResultSummary = "";
        ImportWarnings = null;
    }

    // ------------------------------------------------------------------ cURL import and export (SPEC 7.2, 7.3)

    [RelayCommand]
    private void ImportCurl()
    {
        var input = CurlImportDialog.Ask(FileBaseDirectory);
        if (input is null)
        {
            return;
        }

        FileBaseDirectory = input.BaseDirectory ?? FileBaseDirectory;
        var result = CurlParser.Parse(input.Command, new CurlParseOptions { Dialect = input.Dialect, FileBaseDirectory = Blank(FileBaseDirectory) });
        LoadSpec(result.Request);
        var notes = new List<string>(result.Warnings);
        notes.AddRange(result.FileReferences.Where(f => !f.Resolved).Select(f => $"File reference not resolved: {f}. Choose a base directory to resolve it."));
        notes.AddRange(result.FileReferences.Where(f => f.Resolved).Select(f => $"File reference resolved: {f}"));
        ImportWarnings = notes.Count == 0 ? null : string.Join(Environment.NewLine, notes);
        ResultSummary = $"Imported a {result.Dialect} cURL command.";
        ResultIsError = false;
    }

    [RelayCommand]
    private void CopyAs(string? format)
    {
        var f = Enum.TryParse<ExportFormat>(format, out var parsed) ? parsed : ExportFormat.CurlBash;
        var result = RequestExporter.Export(BuildRequest(), f);
        Ui.Copy(result.Text);
        ResultSummary = $"Copied as {f}." + (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "");
        ResultIsError = false;
    }

    // ------------------------------------------------------------------ send (SPEC 7.4, 7.6)

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        if (OfflineMode)
        {
            ResultSummary = "Offline Mode is on. Turn it off (Tools menu or Settings) to send requests.";
            ResultIsError = true;
            return;
        }

        var request = BuildRequest();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ResultSummary = "Enter an absolute http:// or https:// URL.";
            ResultIsError = true;
            return;
        }

        // Replay guard: captured credentials need confirmation, naming the host and the fields.
        var findings = ReplayGuard.Inspect(request, Origin);
        if (findings.Count > 0 && !AppServices.Current.ReplaySuppressions.IsSuppressed(uri.Host))
        {
            var choice = ReplayGuardDialog.Ask(uri.Host, findings);
            switch (choice)
            {
                case ReplayGuardChoice.Cancel:
                    return;
                case ReplayGuardChoice.StripAndSend:
                    request = ReplayGuard.StripCredentials(request, findings);
                    break;
                case ReplayGuardChoice.SendAndSuppressHost:
                    AppServices.Current.ReplaySuppressions.Suppress(uri.Host);
                    break;
            }
        }

        IsSending = true;
        ResultIsError = false;
        ResultSummary = $"Sending to {uri.Host}…";
        _sendCts = new CancellationTokenSource();
        try
        {
            // First use loads HarLens.Net (SPEC 3.1). Never reached while Offline Mode is on.
            var engine = RequestEngineLoader.Load();
            var settings = new SendSettings
            {
                ResponseSizeCap = AppServices.Current.Settings.ResponseSizeCapBytes,
                FileBaseDirectory = Blank(FileBaseDirectory),
            };
            AppLog.Info("Composer send started");
            var outcome = await engine.SendAsync(request, settings, _sendCts.Token);
            ShowOutcome(request, outcome);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TypeLoadException or System.Reflection.ReflectionTypeLoadException)
        {
            ResultSummary = "The request engine failed: " + ex.Message;
            ResultIsError = true;
            AppLog.Error("Composer send failed", ex);
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void CancelSend() => _sendCts?.Cancel();

    private void ShowOutcome(HttpRequestSpec request, SendOutcome outcome)
    {
        var composer = _main.EnsureComposerSession();
        if (outcome.Exchanges.Count > 0)
        {
            var added = ExchangeConverter.Append(composer.Session!, outcome.Exchanges, request.OriginKey);
            composer.RebuildRows();
            _ = composer.ApplyFilterAsync();
            LastResult = added[^1];
        }

        var last = outcome.Exchanges.LastOrDefault();
        var parts = new List<string>();
        if (last is not null && last.Status > 0)
        {
            parts.Add($"{last.Status} {last.StatusText}");
            parts.Add(DisplayFormat.Duration(last.Timings.Total));
            parts.Add(DisplayFormat.HumanSize(last.ResponseBody.Length));
            parts.Add(last.HttpVersion);
            if (last.RemoteAddress is not null)
            {
                parts.Add($"{last.RemoteAddress}:{last.RemotePort}");
            }

            if (outcome.Exchanges.Count > 1)
            {
                parts.Add($"{outcome.Exchanges.Count - 1} redirect(s) followed");
            }
        }

        if (outcome.Cancelled)
        {
            parts.Add("Cancelled");
        }
        else if (outcome.Error is not null)
        {
            parts.Add(outcome.Error);
        }

        if (last is not null)
        {
            parts.AddRange(last.Notices);
        }

        ResultSummary = string.Join("    ", parts);
        ResultIsError = outcome.Error is not null || outcome.Cancelled;
        AppLog.Info($"Composer send finished: {(outcome.Error is null ? "ok" : "error")}");

        var store = AppServices.Current.ComposerStore;
        try
        {
            store.AppendHistory(new HistoryItem
            {
                Sent = DateTimeOffset.Now,
                Request = request,
                Status = last?.Status,
                TimeMs = last?.Timings.Total,
            }, KeepCredentialsInHistory);
            ReloadHistory();
        }
        catch (IOException ex)
        {
            AppLog.Warning("History could not be saved: " + ex.Message);
        }
    }

    [RelayCommand]
    private void DiffAgainstOriginal()
    {
        if (Origin is not null && LastResult is not null)
        {
            _main.ShowCompare(Origin, LastResult);
        }
    }

    [RelayCommand]
    private void ShowResult()
    {
        if (LastResult is not null)
        {
            _main.SelectEntry(LastResult);
            System.Windows.Application.Current.MainWindow?.Activate();
        }
    }

    // ------------------------------------------------------------------ history and collections (SPEC 7.5)

    private void ReloadHistory()
    {
        History.Clear();
        foreach (var item in AppServices.Current.ComposerStore.LoadHistory())
        {
            History.Add(item);
        }
    }

    private void ReloadCollections()
    {
        Collections.Clear();
        foreach (var c in AppServices.Current.ComposerStore.LoadCollections())
        {
            Collections.Add(c);
        }
    }

    partial void OnSelectedHistoryChanged(HistoryItem? value)
    {
        if (value is not null)
        {
            LoadSpec(value.Request);
            ResultSummary = value.CredentialsRemoved ? "Loaded from history. Credential header values were not stored; fill them in before sending." : "Loaded from history.";
            ResultIsError = false;
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (Ui.Confirm("Clear the composer history?"))
        {
            AppServices.Current.ComposerStore.ClearHistory();
            ReloadHistory();
        }
    }

    [RelayCommand]
    private void SaveToCollection()
    {
        var name = InputDialog.Ask("Save request", "Collection name:", Collections.FirstOrDefault()?.Name ?? "My requests");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var requestName = InputDialog.Ask("Save request", "Request name:", $"{Method} {UrlParts.Split(Url).Host}");
        if (string.IsNullOrWhiteSpace(requestName))
        {
            return;
        }

        var collection = Collections.FirstOrDefault(c => c.Name == name) ?? new RequestCollection { Name = name };
        collection.Requests.RemoveAll(r => r.Name == requestName);
        collection.Requests.Add(new SavedRequest { Name = requestName, Request = BuildRequest() });
        AppServices.Current.ComposerStore.SaveCollection(collection);
        ReloadCollections();
    }

    [RelayCommand]
    private void LoadSaved(SavedRequest? saved)
    {
        if (saved is not null)
        {
            LoadSpec(saved.Request);
            ResultSummary = $"Loaded '{saved.Name}'.";
            ResultIsError = false;
        }
    }

    [RelayCommand]
    private void DeleteCollection(RequestCollection? collection)
    {
        if (collection is not null && Ui.Confirm($"Delete the collection '{collection.Name}'?"))
        {
            AppServices.Current.ComposerStore.DeleteCollection(collection.Name);
            ReloadCollections();
        }
    }
}
