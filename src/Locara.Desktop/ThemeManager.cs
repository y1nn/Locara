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
                SetBrush(res, "BgCanvas", "#0B1014");
                SetBrush(res, "BgSidebar", "#070B0E");
                SetBrush(res, "BgSurface", "#121A20");
                SetBrush(res, "BgSubtle", "#0E151A");
                SetBrush(res, "BgElevated", "#1A242C");
                SetBrush(res, "BgHover", "#23313C");
                SetBrush(res, "BgInput", "#0B1116");
                SetBrush(res, "BorderSubtle", "#263642");
                SetBrush(res, "CardBorder", "#263642");
                SetBrush(res, "BorderStrong", "#394F60");
                SetBrush(res, "TextPrimary", "#F0F6F5");
                SetBrush(res, "Ink", "#F0F6F5");
                SetBrush(res, "TextSecondary", "#A8BDC4");
                SetBrush(res, "TextMuted", "#7E969F");

                // Targeted Locara Jade (10% focal rule)
                SetBrush(res, "Accent", "#109876");
                SetBrush(res, "AccentHover", "#15B58D");
                SetBrush(res, "AccentSoft", "#112F27");
                SetBrush(res, "AccentIndicator", "#2AD4A2");
                SetBrush(res, "OnAccent", "#FFFFFF");

                SetBrush(res, "SuccessFg", "#2ED6A3");
                SetBrush(res, "SuccessBg", "#0F2D24");
                SetBrush(res, "WarningFg", "#F2B441");
                SetBrush(res, "WarningBg", "#2E220E");
                SetBrush(res, "Danger", "#D94646");
                SetBrush(res, "DangerFg", "#F47272");
                SetBrush(res, "DangerBg", "#2F1417");
                SetBrush(res, "InfoFg", "#4DC3FA");
                SetBrush(res, "InfoBg", "#0F2533");
            }
            else
            {
                SetBrush(res, "BgCanvas", "#E7EEEC");
                SetBrush(res, "BgSidebar", "#DCE6E4");
                SetBrush(res, "BgSurface", "#FFFFFF");
                SetBrush(res, "BgSubtle", "#F4F8F7");
                SetBrush(res, "BgElevated", "#E4ECEA");
                SetBrush(res, "BgHover", "#D4E2DF");
                SetBrush(res, "BgInput", "#FFFFFF");
                SetBrush(res, "BorderSubtle", "#C2D2CE");
                SetBrush(res, "CardBorder", "#C2D2CE");
                SetBrush(res, "BorderStrong", "#98B0AB");
                SetBrush(res, "TextPrimary", "#0D1C20");
                SetBrush(res, "Ink", "#0D1C20");
                SetBrush(res, "TextSecondary", "#364F56");
                SetBrush(res, "TextMuted", "#536C73");

                // Targeted Deep Jade for Light Mode
                SetBrush(res, "Accent", "#0B7C60");
                SetBrush(res, "AccentHover", "#09664F");
                SetBrush(res, "AccentSoft", "#CFECE3");
                SetBrush(res, "AccentIndicator", "#096951");
                SetBrush(res, "OnAccent", "#FFFFFF");

                SetBrush(res, "SuccessFg", "#0B6B52");
                SetBrush(res, "SuccessBg", "#D7F2E9");
                SetBrush(res, "WarningFg", "#8F5700");
                SetBrush(res, "WarningBg", "#FFF0CE");
                SetBrush(res, "Danger", "#C0362C");
                SetBrush(res, "DangerFg", "#B22828");
                SetBrush(res, "DangerBg", "#FCE4E4");
                SetBrush(res, "InfoFg", "#036296");
                SetBrush(res, "InfoBg", "#DCEFFC");
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
