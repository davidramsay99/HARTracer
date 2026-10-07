using System.Windows;
using HarLens.Core.Settings;
using Microsoft.Win32;

namespace HarLens.App.Services;

/// <summary>Thin wrappers over WPF dialogs and the clipboard so view models stay short and readable.</summary>
public static class Ui
{
    public const string HarFilter = "HAR files (*.har;*.json;*.har.gz)|*.har;*.json;*.har.gz;*.gz|All files (*.*)|*.*";
    public const string OpenFilter = "HAR and SAZ files (*.har;*.json;*.har.gz;*.saz)|*.har;*.json;*.har.gz;*.gz;*.saz|HAR files (*.har;*.json;*.har.gz)|*.har;*.json;*.har.gz;*.gz|SAZ session archives (*.saz)|*.saz|All files (*.*)|*.*";

    public static Window? ActiveWindow =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static string[] OpenFiles(string title = "Open HAR", string filter = OpenFilter, bool multiple = true)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = multiple, CheckFileExists = true };
        return dialog.ShowDialog(ActiveWindow) == true ? dialog.FileNames : [];
    }

    public static string? SaveFile(string title, string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(ActiveWindow) == true ? dialog.FileName : null;
    }

    public static string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(ActiveWindow) == true ? dialog.FolderName : null;
    }

    public static void Info(string message, string title = "HarLens") =>
        MessageBox.Show(ActiveWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string message, string title = "HarLens")
    {
        AppLog.Error(message);
        MessageBox.Show(ActiveWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static bool Confirm(string message, string title = "HarLens") =>
        MessageBox.Show(ActiveWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Puts text on the clipboard. Callers apply the secret mask first when it is on.</summary>
    public static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetDataObject(text, copy: true);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Warning("Clipboard is busy: " + ex.Message);
        }
    }
}
