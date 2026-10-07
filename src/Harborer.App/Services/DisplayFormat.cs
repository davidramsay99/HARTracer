using System.Globalization;
using Harborer.Core.Settings;

namespace Harborer.App.Services;

/// <summary>Size and time formatting according to the display toggles.</summary>
public static class DisplayFormat
{
    private static AppSettings Settings => AppServices.Current.Settings;

    public static string Size(long bytes)
    {
        if (bytes < 0)
        {
            return "";
        }

        if (Settings.SizeDisplay == SizeDisplay.Bytes)
        {
            return bytes.ToString("N0", CultureInfo.CurrentCulture);
        }

        return HumanSize(bytes);
    }

    public static string HumanSize(long bytes) => bytes switch
    {
        < 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string Duration(double milliseconds) => milliseconds switch
    {
        < 0 => "",
        < 1000 => $"{milliseconds:0.#} ms",
        < 60_000 => $"{milliseconds / 1000:0.00} s",
        _ => $"{milliseconds / 60_000:0.0} min",
    };

    public static string Started(DateTimeOffset started, DateTimeOffset first)
    {
        if (started == DateTimeOffset.MinValue)
        {
            return "";
        }

        return Settings.TimeDisplay switch
        {
            TimeDisplay.LocalTime => started.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture),
            TimeDisplay.Utc => started.UtcDateTime.ToString("HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            _ => "+" + Duration(Math.Max(0, (started - first).TotalMilliseconds)),
        };
    }

    public static string Timestamp(DateTimeOffset value) => value == DateTimeOffset.MinValue
        ? ""
        : Settings.TimeDisplay == TimeDisplay.Utc
            ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture);
}
