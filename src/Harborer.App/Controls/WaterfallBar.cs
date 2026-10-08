using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Harborer.Core.Har;

namespace Harborer.App.Controls;

/// <summary>
/// The Waterfall column: stacked bars for blocked, DNS, connect, TLS, send, wait and receive on a shared
/// time axis, drawn directly in OnRender so that recycled rows stay cheap. The tooltip lists each phase in milliseconds.
/// </summary>
public sealed class WaterfallBar : FrameworkElement
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
        nameof(Entry), typeof(HarEntry), typeof(WaterfallBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnEntryChanged));

    public static readonly DependencyProperty AxisStartProperty = DependencyProperty.Register(
        nameof(AxisStart), typeof(DateTimeOffset), typeof(WaterfallBar),
        new FrameworkPropertyMetadata(DateTimeOffset.MinValue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisMillisecondsProperty = DependencyProperty.Register(
        nameof(AxisMilliseconds), typeof(double), typeof(WaterfallBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly string[] PhaseBrushKeys = ["PhaseBlocked", "PhaseDns", "PhaseConnect", "PhaseTls", "PhaseSend", "PhaseWait", "PhaseReceive"];

    public WaterfallBar()
    {
        ToolTip = "";
        ToolTipOpening += (_, _) => ToolTip = BuildToolTip(Entry);
    }

    public HarEntry? Entry
    {
        get => (HarEntry?)GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    public DateTimeOffset AxisStart
    {
        get => (DateTimeOffset)GetValue(AxisStartProperty);
        set => SetValue(AxisStartProperty, value);
    }

    public double AxisMilliseconds
    {
        get => (double)GetValue(AxisMillisecondsProperty);
        set => SetValue(AxisMillisecondsProperty, value);
    }

    public static string BuildToolTip(HarEntry? entry)
    {
        if (entry is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        var t = entry.Timings;
        void Line(string name, double value) => sb.AppendLine(value < 0 ? $"{name,-9} n/a" : $"{name,-9} {value.ToString("0.###", CultureInfo.CurrentCulture)} ms");
        Line("Blocked", t.Blocked);
        Line("DNS", t.Dns);
        Line("Connect", t.TcpConnect);
        Line("TLS", t.Ssl);
        Line("Send", t.Send);
        Line("Wait", t.Wait);
        Line("Receive", t.Receive);
        sb.Append($"{"Total",-9} {entry.TotalTime.ToString("0.###", CultureInfo.CurrentCulture)} ms");
        return sb.ToString();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        var entry = Entry;
        if (entry is null || entry.StartedDateTime == DateTimeOffset.MinValue || ActualWidth <= 2 || AxisMilliseconds <= 0)
        {
            return;
        }

        var scale = ActualWidth / AxisMilliseconds;
        var x = Math.Max(0, (entry.StartedDateTime - AxisStart).TotalMilliseconds) * scale;
        var height = Math.Max(4, ActualHeight - 8);
        var y = (ActualHeight - height) / 2;
        var phases = entry.Timings.Phases().ToList();
        if (phases.Count == 0 || phases.All(p => p.Duration <= 0))
        {
            // No phase data: one bar for the total time.
            var w = Math.Max(1, Math.Max(0, entry.TotalTime) * scale);
            dc.DrawRectangle(Brush("PhaseWait"), null, new Rect(x, y, Math.Min(w, Math.Max(1, ActualWidth - x)), height));
            return;
        }

        foreach (var (phase, start, duration) in phases)
        {
            if (duration <= 0)
            {
                continue;
            }

            var left = x + (start * scale);
            var width = Math.Max(1, duration * scale);
            if (left >= ActualWidth)
            {
                break;
            }

            width = Math.Min(width, ActualWidth - left);
            var index = phase switch
            {
                "Blocked" => 0,
                "DNS" => 1,
                "Connect" => 2,
                "TLS" => 3,
                "Send" => 4,
                "Wait" => 5,
                _ => 6,
            };
            dc.DrawRectangle(Brush(PhaseBrushKeys[index]), null, new Rect(left, y, width, height));
        }
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.SteelBlue;

    private static void OnEntryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WaterfallBar)d).ToolTip = "";
}
