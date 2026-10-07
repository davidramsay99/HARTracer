using HarLens.Core.Har;
using HarLens.Core.Text;

namespace HarLens.Core.Filtering;

/// <summary>The quick-toggle type chips, in Chrome DevTools order.</summary>
public enum ResourceCategory
{
    Fetch,
    Document,
    Script,
    Stylesheet,
    Image,
    Font,
    Media,
    WebSocket,
    Other,
}

public static class ResourceCategories
{
    public static IReadOnlyList<ResourceCategory> All { get; } = Enum.GetValues<ResourceCategory>();

    public static string Label(ResourceCategory category) => category switch
    {
        ResourceCategory.Fetch => "XHR/Fetch",
        ResourceCategory.Document => "Doc",
        ResourceCategory.Script => "JS",
        ResourceCategory.Stylesheet => "CSS",
        ResourceCategory.Image => "Img",
        ResourceCategory.Font => "Font",
        ResourceCategory.Media => "Media",
        ResourceCategory.WebSocket => "WS",
        _ => "Other",
    };

    /// <summary>
    /// Category from Chromium's <c>_resourceType</c> when present, else from <c>Sec-Fetch-Dest</c>, the scheme, and the MIME type.
    /// </summary>
    public static ResourceCategory Of(HarEntry entry)
    {
        if (entry.CategoryCache >= 0)
        {
            return (ResourceCategory)entry.CategoryCache;
        }

        var category = Compute(entry);
        entry.CategoryCache = (int)category;
        return category;
    }

    public static ResourceCategory FromResourceType(string? resourceType) => resourceType?.ToLowerInvariant() switch
    {
        "xhr" or "fetch" => ResourceCategory.Fetch,
        "document" => ResourceCategory.Document,
        "script" => ResourceCategory.Script,
        "stylesheet" => ResourceCategory.Stylesheet,
        "image" or "img" => ResourceCategory.Image,
        "font" => ResourceCategory.Font,
        "media" => ResourceCategory.Media,
        "websocket" => ResourceCategory.WebSocket,
        _ => ResourceCategory.Other,
    };

    private static ResourceCategory Compute(HarEntry e)
    {
        if (!string.IsNullOrEmpty(e.ResourceType))
        {
            return FromResourceType(e.ResourceType);
        }

        var scheme = e.Scheme;
        if (scheme is "ws" or "wss" || e.WebSocketMessageCount > 0 ||
            (e.Status == 101 && string.Equals(e.GetRequestHeader("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase)))
        {
            return ResourceCategory.WebSocket;
        }

        switch (e.GetRequestHeader("Sec-Fetch-Dest")?.ToLowerInvariant())
        {
            case "document" or "iframe" or "frame":
                return ResourceCategory.Document;
            case "script" or "worker" or "sharedworker" or "serviceworker":
                return ResourceCategory.Script;
            case "style":
                return ResourceCategory.Stylesheet;
            case "image":
                return ResourceCategory.Image;
            case "font":
                return ResourceCategory.Font;
            case "audio" or "video" or "track":
                return ResourceCategory.Media;
            case "empty":
                return ResourceCategory.Fetch;
        }

        if (e.GetRequestHeader("X-Requested-With") is not null)
        {
            return ResourceCategory.Fetch;
        }

        return MimeTypes.Categorize(e.MimeType) switch
        {
            MimeCategory.Document => ResourceCategory.Document,
            MimeCategory.Script => ResourceCategory.Script,
            MimeCategory.Stylesheet => ResourceCategory.Stylesheet,
            MimeCategory.Image => ResourceCategory.Image,
            MimeCategory.Font => ResourceCategory.Font,
            MimeCategory.Media => ResourceCategory.Media,
            MimeCategory.Json or MimeCategory.Xml => ResourceCategory.Fetch,
            _ => ResourceCategory.Other,
        };
    }
}
