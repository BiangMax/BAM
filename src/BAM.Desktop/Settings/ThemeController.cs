using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using BAM.Desktop.Controls;
using BAM.Desktop.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BAM.Desktop.Settings;

internal sealed class ThemeController : IDisposable
{
    private const string PersonalizationKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly (string ControlKey, string BamKey)[] Aliases =
    [
        ("ComboBoxForeground", "Bam.TextPrimary"),
        ("ComboBoxItemBackgroundSelected", "Bam.AccentSurface"),
        ("ComboBoxItemForegroundSelected", "Bam.TextPrimary"),
        ("ContextMenuBackground", "Bam.Surface"),
        ("ContextMenuBorderBrush", "Bam.Border"),
        ("ContextMenuForeground", "Bam.TextPrimary")
    ];
    private readonly SettingsViewModel _settings;
    private readonly ResourceDictionary _paletteHost;
    private HwndSource? _source;
    private FluentWindow? _window;
    private ApplicationTheme _appliedTheme;
    private DispatcherOperation? _scheduled;

    public ThemeController(SettingsViewModel settings)
    {
        _settings = settings;
        _paletteHost = new ResourceDictionary();
        Application.Current.Resources.MergedDictionaries.Add(_paletteHost);
        _settings.PropertyChanged += SettingsChanged;
        SystemParameters.StaticPropertyChanged += SystemChanged;
        Apply();
    }

    public void Attach(FluentWindow window)
    {
        _window = window;
        _source = (HwndSource?)PresentationSource.FromVisual(window);
        _source?.AddHook(WindowMessage);
        Apply();
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.Theme) or nameof(SettingsViewModel.ReduceMotion)) Apply();
    }

    private void SystemChanged(object? sender, PropertyChangedEventArgs args) => ScheduleApply();

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Theme changes are also broadcast as WM_SETTINGCHANGE and WM_THEMECHANGED.
        if (message is 0x001A or 0x031A or 0x031E) ScheduleApply();
        return IntPtr.Zero;
    }

    private void ScheduleApply()
    {
        if (_scheduled is { Status: DispatcherOperationStatus.Pending }) return;
        _scheduled = Application.Current.Dispatcher.InvokeAsync(Apply, DispatcherPriority.Background);
    }

    private void Apply()
    {
        Motion.Reduce = _settings.ReduceMotion;
        var (dark, transparency) = ReadPersonalization();
        var theme = SystemParameters.HighContrast ? ApplicationTheme.HighContrast : _settings.Theme switch
        {
            ThemeChoice.System => dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            ThemeChoice.Light => ApplicationTheme.Light,
            _ => ApplicationTheme.Dark
        };

        if (theme != _appliedTheme)
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
            var palette = new ResourceDictionary { Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative) };
            _paletteHost.MergedDictionaries.Clear();
            _paletteHost.MergedDictionaries.Add(palette);
            foreach (var (controlKey, bamKey) in Aliases) _paletteHost[controlKey] = palette[bamKey];
            _appliedTheme = theme;
        }

        if (_window is not { } window) return;
        var backdrop = transparency && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            && !SystemParameters.HighContrast && !SystemParameters.IsRemoteSession && SystemParameters.IsGlassEnabled
            && WindowBackdrop.IsSupported(WindowBackdropType.Mica)
            ? WindowBackdropType.Mica : WindowBackdropType.None;
        window.WindowBackdropType = backdrop;
        if (backdrop == WindowBackdropType.None) window.SetResourceReference(Control.BackgroundProperty, "Bam.Rail");
    }

    private static (bool Dark, bool Transparency) ReadPersonalization()
    {
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(PersonalizationKey);
            return (personalize?.GetValue("AppsUseLightTheme") is 0, personalize?.GetValue("EnableTransparency") is not 0);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (false, false);
        }
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= SettingsChanged;
        SystemParameters.StaticPropertyChanged -= SystemChanged;
        _scheduled?.Abort();
        _source?.RemoveHook(WindowMessage);
    }
}
