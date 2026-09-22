using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChroniclesDonationBridge.App.Services;

/// <summary>
/// Keeps the native Windows caption and resize border while asking DWM to match
/// the active application palette. Unsupported attributes are intentionally ignored.
/// </summary>
internal static class DarkSystemFrame
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    private const int AttributeValueSize = sizeof(int);

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;

        var customAppearanceApplied = false;
        var systemEventAttached = false;
        var themeEventAttached = false;
        PropertyChangedEventHandler? systemParametersChanged = null;
        EventHandler? sourceInitialized = null;
        EventHandler? closed = null;

        void ApplyForCurrentAccessibilityMode()
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
            if (!window.Dispatcher.CheckAccess())
            {
                _ = window.Dispatcher.BeginInvoke((Action)ApplyForCurrentAccessibilityMode);
                return;
            }

            if (SystemParameters.HighContrast)
            {
                if (customAppearanceApplied) RestoreSystemAppearance(window);
                customAppearanceApplied = false;
                return;
            }

            customAppearanceApplied = ApplyThemeAppearance(window);
        }

        systemParametersChanged = (_, eventArgs) =>
        {
            if (string.IsNullOrEmpty(eventArgs.PropertyName) ||
                eventArgs.PropertyName == nameof(SystemParameters.HighContrast))
            {
                ApplyForCurrentAccessibilityMode();
            }
        };
        sourceInitialized = (_, _) =>
        {
            if (!systemEventAttached)
            {
                SystemParameters.StaticPropertyChanged += systemParametersChanged;
                systemEventAttached = true;
            }
            if (!themeEventAttached)
            {
                ThemeManager.ThemeChanged += ApplyForCurrentAccessibilityMode;
                themeEventAttached = true;
            }
            ApplyForCurrentAccessibilityMode();
        };
        closed = (_, _) =>
        {
            window.SourceInitialized -= sourceInitialized;
            window.Closed -= closed;
            if (systemEventAttached)
            {
                SystemParameters.StaticPropertyChanged -= systemParametersChanged;
                systemEventAttached = false;
            }
            if (themeEventAttached)
            {
                ThemeManager.ThemeChanged -= ApplyForCurrentAccessibilityMode;
                themeEventAttached = false;
            }
        };

        window.SourceInitialized += sourceInitialized;
        window.Closed += closed;

        // Normally Attach is called from the constructor, but applying immediately
        // makes the helper safe to use after SourceInitialized as well.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            sourceInitialized(window, EventArgs.Empty);
        }
    }

    private static bool ApplyThemeAppearance(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        var applied = TrySetImmersiveDarkMode(handle, enabled: !ThemeManager.IsLightTheme);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            applied |= TrySetAttribute(handle, DwmwaCaptionColor, ColorRef(window, "BackgroundBrush", Color.FromRgb(0x15, 0x17, 0x19)));
            applied |= TrySetAttribute(handle, DwmwaBorderColor, ColorRef(window, "BorderBrush", Color.FromRgb(0x38, 0x3D, 0x43)));
            applied |= TrySetAttribute(handle, DwmwaTextColor, ColorRef(window, "TextBrush", Color.FromRgb(0xF3, 0xF4, 0xF5)));
        }
        return applied;
    }

    private static void RestoreSystemAppearance(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        _ = TrySetImmersiveDarkMode(handle, enabled: false);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            _ = TrySetAttribute(handle, DwmwaCaptionColor, DwmColorDefault);
            _ = TrySetAttribute(handle, DwmwaBorderColor, DwmColorDefault);
            _ = TrySetAttribute(handle, DwmwaTextColor, DwmColorDefault);
        }
    }

    private static bool TrySetImmersiveDarkMode(IntPtr handle, bool enabled)
    {
        var value = enabled ? 1 : 0;
        return TrySetAttribute(handle, DwmwaUseImmersiveDarkMode, value) ||
               TrySetAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, value);
    }

    private static bool TrySetAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, AttributeValueSize) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or
                                          EntryPointNotFoundException or
                                          BadImageFormatException or
                                          PlatformNotSupportedException or
                                          SEHException)
        {
            return false;
        }
    }

    private static int ColorRef(Window window, string resourceKey, Color fallback)
    {
        var color = window.TryFindResource(resourceKey) is SolidColorBrush brush ? brush.Color : fallback;
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
