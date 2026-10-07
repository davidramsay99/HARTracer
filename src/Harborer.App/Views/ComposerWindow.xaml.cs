using System.Windows;
using System.Windows.Controls;
using Harborer.App.ViewModels;

namespace Harborer.App.Views;

public partial class ComposerWindow : Window
{
    public ComposerWindow(ComposerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closing += (_, _) => vm.CancelSendCommand.Execute(null);
    }

    private void OnBodyTextChanged(object sender, TextChangedEventArgs e)
    {
        // Typing replaces a binary body carried over from a captured entry.
        if (DataContext is ComposerViewModel vm && vm.BodyBytes is not null && ((TextBox)sender).IsKeyboardFocusWithin)
        {
            vm.BodyBytes = null;
        }
    }
}
