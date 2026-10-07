using System.Windows;
using HarLens.Core.Settings;
using Microsoft.Win32;

namespace HarLens.App.Services;

/// <summary>
/// Light and dark themes following the system setting, with an override. Controls use WPF's Fluent theme
/// (Application.ThemeMode); HarLens' own semantic brushes (row colors, waterfall phases, diff colors) swap with it.
/// Reads the Windows "apps use light theme" preference; never writes the registry.
/// </summary>
public static class ThemeManager
{
    private static ThemeChoice s_choice = ThemeChoice.System;
    private static ResourceDictionary? s_semantic;

    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Initialize(ThemeChoice choice)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && s_choice == ThemeChoice.System)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
            }
        };
        Apply(choice);
    }

    public static void Apply(ThemeChoice choice)
    {
        s_choice = choice;
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        IsDark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => SystemPrefersDark(),
        };

        app.ThemeMode = choice switch
        {
            ThemeChoice.Dark => ThemeMode.Dark,
            ThemeChoice.Light => ThemeMode.Light,
            _ => ThemeMode.System,
        };

        var semantic = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "pack://application:,,,/HarLens;component/Themes/Dark.xaml" : "pack://application:,,,/HarLens;component/Themes/Light.xaml"),
        };
        if (s_semantic is not null)
        {
            app.Resources.MergedDictionaries.Remove(s_semantic);
        }

        // Insert first so that Themes/Common.xaml (added after) can reference these keys dynamically.
        app.Resources.MergedDictionaries.Insert(0, semantic);
        s_semantic = semantic;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
