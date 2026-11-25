using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Locara.Core;

namespace Locara.Desktop;

internal static class ThemeManager
{
    private static readonly string ThemeSettingPath = Path.Combine(LocaraPaths.UserDataDirectory, "ui-theme.txt");
    private static readonly string UiFontsDir = Path.Combine(LocaraPaths.UserDataDirectory, "ui-fonts");
    private static readonly HashSet<Window> AttachedWindows = [];

    public static bool IsDark { get; private set; } = true;
    public static event Action<bool>? ThemeChanged;

    public static void Initialize()
    {
        EnsureUiFont();
        bool preferDark = true;
        try
        {
            if (File.Exists(ThemeSettingPath))
            {
                string raw = File.ReadAllText(ThemeSettingPath).Trim();
                if (raw.Equals("light", StringComparison.OrdinalIgnoreCase))
                    preferDark = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        ApplyTheme(preferDark, persist: false);
    }

    public static void ToggleTheme() => ApplyTheme(!IsDark, persist: true);

    public static void ApplyTheme(bool dark, bool persist = true)
    {
        IsDark = dark;
        var res = Application.Current?.Resources;
        if (res != null)
        {
            if (dark)
            {
                SetBrush(res, "BgCanvas", "#0C1113");
                SetBrush(res, "BgSidebar", "#080C0E");
                SetBrush(res, "BgSurface", "#121A1D");
                SetBrush(res, "BgSubtle", "#0F1619");
                SetBrush(res, "BgElevated", "#192328");
                SetBrush(res, "BgHover", "#212E34");
                SetBrush(res, "BgInput", "#0E1518");
                SetBrush(res, "BorderSubtle", "#223036");
                SetBrush(res, "CardBorder", "#223036");
                SetBrush(res, "BorderStrong", "#324750");
                SetBrush(res, "TextPrimary", "#ECF3F1");
                SetBrush(res, "Ink", "#ECF3F1");
                SetBrush(res, "TextSecondary", "#96AAAF");
                SetBrush(res, "TextMuted", "#667B81");

                // Targeted Locara Jade (10% focal rule)
                SetBrush(res, "Accent", "#129674");
                SetBrush(res, "AccentHover", "#16B089");
                SetBrush(res, "AccentSoft", "#132C25");
                SetBrush(res, "AccentIndicator", "#24CA98");
                SetBrush(res, "OnAccent", "#FFFFFF");

                SetBrush(res, "SuccessFg", "#28CE9B");
                SetBrush(res, "SuccessBg", "#112922");
                SetBrush(res, "WarningFg", "#EAA83B");
                SetBrush(res, "WarningBg", "#2A2010");
                SetBrush(res, "Danger", "#D94848");
                SetBrush(res, "DangerFg", "#F06A6A");
                SetBrush(res, "DangerBg", "#2B1517");
                SetBrush(res, "InfoFg", "#47BDF8");
                SetBrush(res, "InfoBg", "#112430");
            }
            else
            {
                SetBrush(res, "BgCanvas", "#F3F6F5");
                SetBrush(res, "BgSidebar", "#E8EFED");
                SetBrush(res, "BgSurface", "#FFFFFF");
                SetBrush(res, "BgSubtle", "#F7FAF9");
                SetBrush(res, "BgElevated", "#EDF3F1");
                SetBrush(res, "BgHover", "#E1ECE9");
                SetBrush(res, "BgInput", "#FFFFFF");
                SetBrush(res, "BorderSubtle", "#D4E0DC");
                SetBrush(res, "CardBorder", "#D4E0DC");
                SetBrush(res, "BorderStrong", "#B3C7C1");
                SetBrush(res, "TextPrimary", "#102125");
                SetBrush(res, "Ink", "#102125");
                SetBrush(res, "TextSecondary", "#486167");
                SetBrush(res, "TextMuted", "#6D8489");

                // Targeted Deep Jade for Light Mode
                SetBrush(res, "Accent", "#0E7A5F");
                SetBrush(res, "AccentHover", "#0B634D");
                SetBrush(res, "AccentSoft", "#DCF0E9");
                SetBrush(res, "AccentIndicator", "#0E7A5F");
                SetBrush(res, "OnAccent", "#FFFFFF");

                SetBrush(res, "SuccessFg", "#0D6E55");
                SetBrush(res, "SuccessBg", "#E3F5EF");
                SetBrush(res, "WarningFg", "#945D00");
                SetBrush(res, "WarningBg", "#FFF6E0");
                SetBrush(res, "Danger", "#C0392B");
                SetBrush(res, "DangerFg", "#B83232");
                SetBrush(res, "DangerBg", "#FDECEC");
                SetBrush(res, "InfoFg", "#0369A1");
                SetBrush(res, "InfoBg", "#E6F4FE");
            }

            // Override SystemColors inside App resources so native popups/menus never render white-on-white
            res[SystemColors.WindowBrushKey] = res["BgElevated"];
            res[SystemColors.ControlBrushKey] = res["BgSurface"];
            res[SystemColors.WindowTextBrushKey] = res["TextPrimary"];
            res[SystemColors.ControlTextBrushKey] = res["TextPrimary"];
            res[SystemColors.HighlightBrushKey] = res["AccentSoft"];
            res[SystemColors.HighlightTextBrushKey] = res["TextPrimary"];
            res[SystemColors.InactiveSelectionHighlightBrushKey] = res["BgHover"];
            res[SystemColors.InactiveSelectionHighlightTextBrushKey] = res["TextPrimary"];
        }

        if (persist)
        {
            try
            {
                Directory.CreateDirectory(LocaraPaths.UserDataDirectory);
                File.WriteAllText(ThemeSettingPath, dark ? "dark" : "light");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        foreach (var win in AttachedWindows.ToArray())
            ApplyTitleBarTheme(win, dark);

        ThemeChanged?.Invoke(dark);
    }

    public static void AttachWindow(Window window)
    {
        window.SetResourceReference(ControlBackgroundProperty, "BgCanvas");
        window.SetResourceReference(ControlForegroundProperty, "TextPrimary");
        if (Application.Current?.Resources["StudioFont"] is FontFamily studioFont)
            window.FontFamily = studioFont;

        if (AttachedWindows.Add(window))
        {
            window.SourceInitialized += (_, _) => ApplyTitleBarTheme(window, IsDark);
            window.Closed += (_, _) => AttachedWindows.Remove(window);
        }
        ApplyTitleBarTheme(window, IsDark);
    }

    private static readonly DependencyProperty ControlBackgroundProperty = System.Windows.Controls.Control.BackgroundProperty;
    private static readonly DependencyProperty ControlForegroundProperty = System.Windows.Controls.Control.ForegroundProperty;

    private static void EnsureUiFont()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        try
        {
            Directory.CreateDirectory(UiFontsDir);
            ExtractBuiltInFont("builtin:ibm-plex-medium", "IBMPlexSansArabic-Medium.ttf");
            ExtractBuiltInFont("builtin:ibm-plex-bold", "IBMPlexSansArabic-Bold.ttf");

            string dirWithSlash = UiFontsDir.EndsWith(Path.DirectorySeparatorChar)
                ? UiFontsDir
                : UiFontsDir + Path.DirectorySeparatorChar;
            var baseUri = new Uri(dirWithSlash, UriKind.Absolute);
            var family = new FontFamily(baseUri, "./#IBM Plex Sans Arabic, Segoe UI");
            res["StudioFont"] = family;
        }
        catch
        {
            res["StudioFont"] = new FontFamily("Segoe UI");
        }
    }

    private static void ExtractBuiltInFont(string fontId, string fileName)
    {
        var choice = ArabicFontCatalog.BuiltInFonts.FirstOrDefault(f => f.Id == fontId);
        if (choice == null) return;
        string dest = Path.Combine(UiFontsDir, fileName);
        byte[] bytes = ArabicFontCatalog.LoadBytes(choice);
        if (!File.Exists(dest) || new FileInfo(dest).Length != bytes.Length)
            File.WriteAllBytes(dest, bytes);
    }

    private static void SetBrush(ResourceDictionary dict, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dict[key] = brush;
    }

    private static void ApplyTitleBarTheme(Window window, bool dark)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDark = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
