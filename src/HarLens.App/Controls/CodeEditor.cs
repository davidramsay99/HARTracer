using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;

namespace HarLens.App.Controls;

/// <summary>
/// Read-only, virtualized, syntax-highlighted text view (AvalonEdit) with a bindable <see cref="Code"/>.
/// Ctrl+F opens AvalonEdit's search panel ("find within the current inspector view", SPEC 6.5).
/// Hyperlinks are disabled: a click must never open a browser (SPEC 3).
/// </summary>
public sealed class CodeEditor : TextEditor
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(CodeEditor), new PropertyMetadata("", (d, e) => ((CodeEditor)d).SetCode((string?)e.NewValue)));

    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.Register(
        nameof(Syntax), typeof(string), typeof(CodeEditor), new PropertyMetadata(null, (d, e) => ((CodeEditor)d).SetSyntax((string?)e.NewValue)));

    public CodeEditor()
    {
        IsReadOnly = true;
        ShowLineNumbers = true;
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New");
        SetResourceReference(FontSizeProperty, "MonoFontSize");
        SetResourceReference(ForegroundProperty, SystemColors.WindowTextBrushKey);
        Background = Brushes.Transparent;
        HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.EnableRectangularSelection = true;
        SearchPanel.Install(this);
        System.Windows.Automation.AutomationProperties.SetName(this, "Text view");

        // Word wrap follows the View menu toggle (SPEC 6.3).
        WordWrap = Services.AppServices.Current.Settings.WordWrap;
        if (ViewModels.MainViewModel.Instance is { } main)
        {
            System.ComponentModel.PropertyChangedEventManager.AddHandler(main, (_, _) => WordWrap = main.WordWrap, nameof(ViewModels.MainViewModel.WordWrap));
        }
    }

    public string? Code
    {
        get => (string?)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public string? Syntax
    {
        get => (string?)GetValue(SyntaxProperty);
        set => SetValue(SyntaxProperty, value);
    }

    /// <summary>Selects and scrolls to a range, used when a search hit is opened.</summary>
    public void Highlight(int offset, int length)
    {
        if (offset < 0 || offset + length > Document.TextLength)
        {
            return;
        }

        Select(offset, length);
        var location = Document.GetLocation(offset);
        ScrollTo(location.Line, location.Column);
    }

    private void SetCode(string? text)
    {
        Document = new TextDocument(text ?? "");
        ScrollToHome();
    }

    private void SetSyntax(string? name) =>
        SyntaxHighlighting = string.IsNullOrEmpty(name) ? null : HighlightingManager.Instance.GetDefinition(name);
}
