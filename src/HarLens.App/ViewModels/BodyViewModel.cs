using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarLens.App.Services;
using HarLens.Core.Har;
using HarLens.Core.Http;
using HarLens.Core.Model;
using HarLens.Core.Sanitize;
using HarLens.Core.Text;

namespace HarLens.App.ViewModels;

public enum BodyViewKind
{
    Pretty,
    Text,
    Hex,
    Image,
    Jwt,
}

public enum PrettyKind
{
    None,
    JsonTree,
    Code,
    Form,
    Multipart,
}

/// <summary>
/// The Body tab with its sub-views: Pretty, Text, Hex, Image, JWT. The view is auto-selected from the MIME
/// type and can be overridden. HTML is shown as text only.
/// </summary>
public sealed partial class BodyViewModel : ObservableObject, IDisposable
{
    /// <summary>Characters shown in the text editors; larger bodies are cut with a notice (save to file to see all).</summary>
    public const int MaxDisplayChars = 16 * 1024 * 1024;

    private JsonDocument? _jsonDocument;
    private int _loadVersion;

    [ObservableProperty]
    public partial DecodedBody? Body { get; set; }

    [ObservableProperty]
    public partial bool HasBody { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial BodyViewKind SelectedView { get; set; } = BodyViewKind.Text;

    [ObservableProperty]
    public partial PrettyKind PrettyKind { get; set; }

    [ObservableProperty]
    public partial bool ShowJsonAsText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<JsonTreeNode> JsonRoots { get; set; } = [];

    [ObservableProperty]
    public partial string PrettyText { get; set; } = "";

    [ObservableProperty]
    public partial string? PrettyHighlighting { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<HarHeader> FormFields { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<MultipartSection> Parts { get; set; } = [];

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string? Highlighting { get; set; }

    [ObservableProperty]
    public partial IList HexRows { get; set; } = Array.Empty<HexRow>();

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    [ObservableProperty]
    public partial string? ImageInfo { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<JwtToken> JwtTokens { get; set; } = [];

    [ObservableProperty]
    public partial bool CanPretty { get; set; }

    [ObservableProperty]
    public partial bool CanImage { get; set; }

    [ObservableProperty]
    public partial bool HasJwt { get; set; }

    public void Load(DecodedBody? body, string mime, IReadOnlyList<HarHeader> postParams, IReadOnlyList<HeaderRow> headers)
    {
        var version = ++_loadVersion;
        _jsonDocument?.Dispose();
        _jsonDocument = null;
        Body = body;
        HasBody = body is not null && body.Bytes.Length > 0;
        Image = null;
        ImageInfo = null;
        JsonRoots = [];
        FormFields = [];
        Parts = [];
        PrettyText = "";
        PrettyKind = PrettyKind.None;
        ShowJsonAsText = false;

        // JWTs in headers are offered even when there is no body.
        var mask = AppServices.Current.Settings.MaskSecrets;
        var tokens = new List<JwtToken>();
        foreach (var h in headers)
        {
            tokens.AddRange(Jwt.FindAll(h.Value, "Header " + h.Name));
        }

        if (body is null || body.Bytes.Length == 0)
        {
            Summary = postParams.Count > 0 ? "Form parameters (no body text in the HAR)" : "No body";
            Text = "";
            HexRows = Array.Empty<HexRow>();
            Notice = null;
            if (postParams.Count > 0)
            {
                FormFields = postParams;
                PrettyKind = PrettyKind.Form;
            }

            JwtTokens = tokens;
            HasJwt = tokens.Count > 0;
            CanPretty = PrettyKind != PrettyKind.None;
            CanImage = false;
            SelectedView = CanPretty ? BodyViewKind.Pretty : HasJwt ? BodyViewKind.Jwt : BodyViewKind.Text;
            return;
        }

        var notices = new List<string>(body.Notices);
        Summary = $"{DisplayFormat.HumanSize(body.Bytes.Length)}  {(string.IsNullOrEmpty(mime) ? "(no MIME type)" : mime)}{(body.WasBase64 ? "  (base64 decoded)" : "")}";
        var text = body.Text;
        if (text is not null)
        {
            if (text.Length > MaxDisplayChars)
            {
                notices.Add($"Showing the first {MaxDisplayChars / (1024 * 1024)} M characters of {text.Length:N0}. Save the body to a file to see all of it.");
                text = text[..MaxDisplayChars];
            }

            if (mask)
            {
                text = SecretMasker.MaskText(text);
            }

            tokens.AddRange(Jwt.FindAll(text, "Body"));
        }

        Text = text ?? $"[{body.Bytes.Length:N0} bytes of binary content. See the Hex view.]";
        Highlighting = HighlightingFor(mime, text);
        HexRows = new HexRowList(body.Bytes);
        JwtTokens = tokens;
        HasJwt = tokens.Count > 0;
        CanImage = MimeTypes.IsImage(mime) && !MimeTypes.StripParameters(mime).Contains("svg", StringComparison.Ordinal);
        if (MimeTypes.StripParameters(mime).Contains("svg", StringComparison.Ordinal))
        {
            notices.Add("SVG is shown as text only.");
        }

        BuildPretty(body, text, mime, postParams);
        CanPretty = PrettyKind != PrettyKind.None;
        Notice = notices.Count == 0 ? null : string.Join(Environment.NewLine, notices);

        SelectedView = CanImage ? BodyViewKind.Image
            : CanPretty && PrettyKind is PrettyKind.JsonTree or PrettyKind.Form or PrettyKind.Multipart ? BodyViewKind.Pretty
            : text is null ? BodyViewKind.Hex
            : BodyViewKind.Text;

        if (CanImage)
        {
            var bytes = body.Bytes;
            _ = Task.Run(() => DecodeImage(bytes)).ContinueWith(t =>
            {
                if (version != _loadVersion)
                {
                    return;
                }

                (Image, ImageInfo) = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    partial void OnShowJsonAsTextChanged(bool value)
    {
        if (PrettyKind is PrettyKind.JsonTree or PrettyKind.Code && _jsonDocument is not null)
        {
            PrettyKind = value ? PrettyKind.Code : PrettyKind.JsonTree;
        }
    }

    private void BuildPretty(DecodedBody body, string? text, string mime, IReadOnlyList<HarHeader> postParams)
    {
        if (text is null)
        {
            if (MimeTypes.IsMultipart(mime))
            {
                Parts = MultipartParser.Parse(body.Bytes, mime);
                PrettyKind = Parts.Count > 0 ? PrettyKind.Multipart : PrettyKind.None;
            }

            return;
        }

        if (MimeTypes.IsJson(mime) || JsonPretty.LooksLikeJson(text))
        {
            try
            {
                _jsonDocument = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 4096 });
                JsonRoots = [JsonTreeNode.Create("(root)", _jsonDocument.RootElement)];
                JsonRoots[0].IsExpanded = true;
                PrettyText = JsonPretty.TryFormat(text, out var pretty) ? pretty : text;
                PrettyHighlighting = "Json";
                PrettyKind = PrettyKind.JsonTree;
                return;
            }
            catch (JsonException)
            {
            }
        }

        if (MimeTypes.IsFormUrlEncoded(mime))
        {
            FormFields = postParams.Count > 0 ? postParams : FormUrlEncoding.Parse(text);
            PrettyKind = PrettyKind.Form;
            return;
        }

        if (MimeTypes.IsMultipart(mime) || text.StartsWith("--", StringComparison.Ordinal))
        {
            var parts = MultipartParser.Parse(body.Bytes, mime);
            if (parts.Count > 0)
            {
                Parts = parts;
                PrettyKind = PrettyKind.Multipart;
                return;
            }
        }

        if (MimeTypes.IsHtml(mime))
        {
            PrettyText = HtmlIndenter.Format(text);
            PrettyHighlighting = "HTML";
            PrettyKind = PrettyKind.Code;
        }
        else if (MimeTypes.IsXml(mime) || text.TrimStart().StartsWith("<?xml", StringComparison.Ordinal))
        {
            if (XmlPretty.TryFormat(text, out var xml))
            {
                PrettyText = xml;
                PrettyHighlighting = "XML";
                PrettyKind = PrettyKind.Code;
            }
        }
    }

    public static string? HighlightingFor(string mime, string? text)
    {
        if (MimeTypes.IsJson(mime) || (text is not null && JsonPretty.LooksLikeJson(text)))
        {
            return "Json";
        }

        if (MimeTypes.IsHtml(mime))
        {
            return "HTML";
        }

        if (MimeTypes.IsXml(mime) || MimeTypes.StripParameters(mime).Contains("svg", StringComparison.Ordinal))
        {
            return "XML";
        }

        if (MimeTypes.IsJavaScript(mime))
        {
            return "JavaScript";
        }

        return MimeTypes.IsCss(mime) ? "CSS" : null;
    }

    /// <summary>Decodes with the Windows Imaging Component; formats without an installed codec (for example WebP on some systems) report so.</summary>
    private static (ImageSource? Image, string Info) DecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            var codec = decoder.CodecInfo?.FriendlyName ?? "image";
            var frames = decoder.Frames.Count > 1 ? $", {decoder.Frames.Count} frames" : "";
            return (frame, $"{frame.PixelWidth} × {frame.PixelHeight} px, {codec}{frames}");
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return (null, "This image could not be decoded: no codec for this format is installed on this system, or the data is not a valid image.");
        }
    }

    [RelayCommand]
    private void CopyText()
    {
        var text = SelectedView == BodyViewKind.Pretty && PrettyText.Length > 0 ? PrettyText : Text;
        Ui.Copy(text);
    }

    [RelayCommand]
    private void SaveToFile()
    {
        if (Body is null || Body.Bytes.Length == 0)
        {
            return;
        }

        var path = Ui.SaveFile("Save body", "All files (*.*)|*.*", "body" + ExtensionFor(Body.MimeType));
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(path, Body.Bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error("Saving failed: " + ex.Message);
        }
    }

    private static string ExtensionFor(string mime) => MimeTypes.StripParameters(mime) switch
    {
        "application/json" => ".json",
        "text/html" => ".html",
        "text/css" => ".css",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "application/javascript" or "text/javascript" => ".js",
        "application/xml" or "text/xml" => ".xml",
        "text/plain" => ".txt",
        _ => ".bin",
    };

    public void Dispose()
    {
        _jsonDocument?.Dispose();
        _jsonDocument = null;
    }
}

/// <summary>A lazily expanded JSON tree node for the Pretty view.</summary>
public sealed partial class JsonTreeNode : ObservableObject
{
    private static readonly JsonTreeNode Placeholder = new("", "", "", null);
    private readonly JsonElement? _element;
    private bool _loaded;

    private JsonTreeNode(string name, string value, string kind, JsonElement? element)
    {
        Name = name;
        Value = value;
        Kind = kind;
        _element = element;
        if (element is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } e && Count(e) > 0)
        {
            Children.Add(Placeholder);
        }
    }

    public string Name { get; }

    public string Value { get; }

    /// <summary>"object", "array", "string", "number", "bool", "null".</summary>
    public string Kind { get; }

    public ObservableCollection<JsonTreeNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public static JsonTreeNode Create(string name, JsonElement element)
    {
        var mask = AppServices.Current.Settings.MaskSecrets;
        return element.ValueKind switch
        {
            JsonValueKind.Object => new JsonTreeNode(name, $"{{{Count(element)}}}", "object", element),
            JsonValueKind.Array => new JsonTreeNode(name, $"[{Count(element)}]", "array", element),
            JsonValueKind.String => new JsonTreeNode(name, "\"" + Shorten(mask ? SecretMasker.MaskText(element.GetString()) : element.GetString()) + "\"", "string", null),
            JsonValueKind.Number => new JsonTreeNode(name, element.GetRawText(), "number", null),
            JsonValueKind.True or JsonValueKind.False => new JsonTreeNode(name, element.GetRawText(), "bool", null),
            _ => new JsonTreeNode(name, "null", "null", null),
        };
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded || _element is not { } element)
        {
            return;
        }

        _loaded = true;
        Children.Clear();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                Children.Add(Create(p.Name, p.Value));
            }
        }
        else
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
            {
                Children.Add(Create($"[{i++}]", item));
            }
        }
    }

    private static int Count(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Count() : e.GetArrayLength();

    private static string Shorten(string? s) => s is null ? "" : s.Length > 2000 ? s[..2000] + "…" : s;
}

/// <summary>A row of the hex view, formatted on demand.</summary>
public sealed record HexRow(byte[] Data, int Row)
{
    public string Text => HexDump.FormatRow(Data, Row);
}

/// <summary>Virtual list of hex rows: WPF asks only for the rows on screen.</summary>
public sealed class HexRowList(byte[] data) : IList
{
    public int Count => HexDump.RowCount(data.Length);

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index]
    {
        get => new HexRow(data, index);
        set => throw new NotSupportedException();
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value) => value is HexRow r && ReferenceEquals(r.Data, data) && r.Row < Count;

    public int IndexOf(object? value) => Contains(value) ? ((HexRow)value!).Row : -1;

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }
}
