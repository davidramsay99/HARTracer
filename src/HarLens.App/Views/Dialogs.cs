using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using HarLens.App.Services;
using HarLens.App.ViewModels;
using HarLens.Core.Composer;
using HarLens.Core.Curl;
using HarLens.Core.Settings;

namespace HarLens.App.Views;

/// <summary>Shared construction for small modal dialogs.</summary>
internal static class DialogKit
{
    public static Window Create(string title, double width, double height)
    {
        var owner = Ui.ActiveWindow;
        return new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = Math.Min(width, 360),
            MinHeight = Math.Min(height, 200),
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResizeWithGrip,
        };
    }

    public static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(6, 0, 0, 0);
            b.MinWidth = 80;
            panel.Children.Add(b);
        }

        return panel;
    }

    public static Button Ok(Window w, string text = "OK", Func<bool>? validate = null)
    {
        var b = new Button { Content = text, IsDefault = true };
        b.Click += (_, _) =>
        {
            if (validate is null || validate())
            {
                w.DialogResult = true;
            }
        };
        return b;
    }

    public static Button Cancel(string text = "Cancel") => new() { Content = text, IsCancel = true };

    public static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 6, 0, 3), TextWrapping = TextWrapping.Wrap };
}

public static class InputDialog
{
    /// <summary>Asks for a line (or block) of text. Returns null on cancel.</summary>
    public static string? Ask(string title, string prompt, string initial = "", bool multiline = false)
    {
        var w = DialogKit.Create(title, 460, multiline ? 300 : 180);
        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(box, prompt);
        var root = new DockPanel { Margin = new Thickness(12) };
        var label = DialogKit.Label(prompt);
        DockPanel.SetDock(label, Dock.Top);
        var buttons = DialogKit.Buttons(DialogKit.Ok(w), DialogKit.Cancel());
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(label);
        root.Children.Add(buttons);
        root.Children.Add(box);
        w.Content = root;
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return w.ShowDialog() == true ? box.Text : null;
    }
}

public static class CustomColumnDialog
{
    /// <summary>Asks for a header name or vendor field to show as a list column.</summary>
    public static CustomColumn? Ask()
    {
        var w = DialogKit.Create("Add custom column", 460, 260);
        var kind = new ComboBox
        {
            ItemsSource = new[] { "Response header", "Request header", "Header (either side)", "Vendor or HAR field" },
            SelectedIndex = 0,
        };
        AutomationProperties.SetName(kind, "Column source");
        var name = new TextBox();
        AutomationProperties.SetName(name, "Header or field name");
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(DialogKit.Label("Source"));
        root.Children.Add(kind);
        root.Children.Add(DialogKit.Label("Name, for example x-azure-ref, x-cache, x-ms-request-id, _priority, response._transferSize"));
        root.Children.Add(name);
        root.Children.Add(DialogKit.Buttons(DialogKit.Ok(w, "Add", () => name.Text.Trim().Length > 0), DialogKit.Cancel()));
        w.Content = root;
        w.Loaded += (_, _) => name.Focus();
        if (w.ShowDialog() != true)
        {
            return null;
        }

        return new CustomColumn
        {
            Kind = kind.SelectedIndex switch
            {
                1 => "request-header",
                2 => "header",
                3 => "field",
                _ => "response-header",
            },
            Name = name.Text.Trim(),
        };
    }
}

public static class MergeDialog
{
    /// <summary>Chooses the tabs to merge into one session.</summary>
    public static List<SessionViewModel>? Choose(IReadOnlyList<SessionViewModel> candidates)
    {
        var w = DialogKit.Create("Merge into new session", 480, 360);
        var boxes = candidates.Select(c => new CheckBox { Content = c.Title, IsChecked = true, Margin = new Thickness(0, 2, 0, 2), Tag = c }).ToList();
        var list = new StackPanel();
        foreach (var b in boxes)
        {
            list.Children.Add(b);
        }

        var root = new DockPanel { Margin = new Thickness(12) };
        var label = DialogKit.Label("Entries from the checked tabs are combined, ordered by start time, and tagged with their source file.");
        DockPanel.SetDock(label, Dock.Top);
        var buttons = DialogKit.Buttons(DialogKit.Ok(w, "Merge", () => boxes.Count(b => b.IsChecked == true) >= 2), DialogKit.Cancel());
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(label);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        w.Content = root;
        return w.ShowDialog() == true ? boxes.Where(b => b.IsChecked == true).Select(b => (SessionViewModel)b.Tag).ToList() : null;
    }
}

public sealed record CurlImportInput(string Command, CurlDialect Dialect, string? BaseDirectory);

public static class CurlImportDialog
{
    /// <summary>
    /// Paste a cURL command. File references (@file) are only read when a base directory is chosen here.
    /// </summary>
    public static CurlImportInput? Ask(string? baseDirectory)
    {
        var w = DialogKit.Create("Import cURL", 760, 480);
        var text = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        AutomationProperties.SetName(text, "cURL command");
        if (Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith("curl", StringComparison.OrdinalIgnoreCase))
        {
            text.Text = Clipboard.GetText();
        }

        var dialect = new ComboBox { ItemsSource = Enum.GetValues<CurlDialect>(), SelectedItem = CurlDialect.Auto, Width = 140 };
        AutomationProperties.SetName(dialect, "Quoting dialect");
        var dir = new TextBox { Text = baseDirectory ?? "", MinWidth = 300 };
        AutomationProperties.SetName(dir, "Base directory for file references");
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            if (Ui.PickFolder("Base directory for @file references") is { } picked)
            {
                dir.Text = picked;
            }
        };

        var options = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        options.Children.Add(new TextBlock { Text = "Dialect ", VerticalAlignment = VerticalAlignment.Center });
        options.Children.Add(dialect);
        options.Children.Add(new TextBlock { Text = "   Base directory for @file ", VerticalAlignment = VerticalAlignment.Center });
        options.Children.Add(dir);
        options.Children.Add(browse);

        var root = new DockPanel { Margin = new Thickness(12) };
        var label = DialogKit.Label("Paste a command from Chrome or Edge (Copy as cURL, bash or cmd), Firefox, or PowerShell (curl.exe):");
        DockPanel.SetDock(label, Dock.Top);
        var bottom = new StackPanel();
        bottom.Children.Add(options);
        bottom.Children.Add(DialogKit.Label("Files named with @ are read only if you choose a base directory. Without one, they are listed as unresolved."));
        bottom.Children.Add(DialogKit.Buttons(DialogKit.Ok(w, "Import", () => text.Text.Trim().Length > 0), DialogKit.Cancel()));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(label);
        root.Children.Add(bottom);
        root.Children.Add(text);
        w.Content = root;
        w.Loaded += (_, _) => text.Focus();
        return w.ShowDialog() == true
            ? new CurlImportInput(text.Text, (CurlDialect)dialect.SelectedItem, string.IsNullOrWhiteSpace(dir.Text) ? null : dir.Text.Trim())
            : null;
    }
}

public enum ReplayGuardChoice
{
    Cancel,
    Send,
    StripAndSend,
    SendAndSuppressHost,
}

public static class ReplayGuardDialog
{
    /// <summary>Names the target host and the credential-bearing fields before a replay.</summary>
    public static ReplayGuardChoice Ask(string host, IReadOnlyList<CredentialFinding> findings)
    {
        var w = DialogKit.Create("Captured credentials", 560, 340);
        var choice = ReplayGuardChoice.Cancel;
        var root = new DockPanel { Margin = new Thickness(12) };
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"This request came from a captured HAR entry and still carries its captured credentials. Sending it will present them to {host}:",
        };
        DockPanel.SetDock(intro, Dock.Top);
        var list = new StackPanel { Margin = new Thickness(12, 8, 0, 8) };
        foreach (var f in findings)
        {
            list.Children.Add(new TextBlock { Text = "• " + f.Description, TextWrapping = TextWrapping.Wrap });
        }

        DockPanel.SetDock(list, Dock.Top);
        var suppress = new CheckBox { Content = $"Don't ask again for {host} until HarLens closes", Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(suppress, Dock.Top);
        Button Make(string text, ReplayGuardChoice value, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel };
            if (isCancel)
            {
                return b; // IsCancel closes the dialog; the choice stays Cancel.
            }

            b.Click += (_, _) =>
            {
                choice = value == ReplayGuardChoice.Send && suppress.IsChecked == true ? ReplayGuardChoice.SendAndSuppressHost : value;
                w.DialogResult = true;
            };
            return b;
        }

        var buttons = DialogKit.Buttons(
            Make("Strip credentials and send", ReplayGuardChoice.StripAndSend, isDefault: true),
            Make($"Send to {host}", ReplayGuardChoice.Send),
            Make("Cancel", ReplayGuardChoice.Cancel, isCancel: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(intro);
        root.Children.Add(list);
        root.Children.Add(suppress);
        root.Children.Add(buttons);
        root.Children.Add(new Border());
        w.Content = root;
        w.ShowDialog();
        return choice;
    }
}

public static class CrashDialog
{
    /// <summary>An unhandled exception shows a local dialog with a copyable stack trace. Nothing is transmitted.</summary>
    public static void Show(Exception exception, string? logFile)
    {
        var w = DialogKit.Create("HarLens: unexpected error", 720, 460);
        var details = new TextBox
        {
            Text = exception.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        AutomationProperties.SetName(details, "Error details");
        var copy = new Button { Content = "Copy details" };
        copy.Click += (_, _) => Ui.Copy(details.Text);
        var root = new DockPanel { Margin = new Thickness(12) };
        var label = DialogKit.Label(
            "HarLens hit an error it did not expect. Your work is still open; you can keep going or save and restart.\n" +
            (logFile is null ? "" : $"The details were written to {logFile}. Nothing has been sent anywhere."));
        DockPanel.SetDock(label, Dock.Top);
        var buttons = DialogKit.Buttons(copy, new Button { Content = "Close", IsCancel = true, IsDefault = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(label);
        root.Children.Add(buttons);
        root.Children.Add(details);
        w.Content = root;
        w.ShowDialog();
    }
}
