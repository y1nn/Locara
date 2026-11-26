using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Locara.Core;
using Microsoft.Win32;

namespace Locara.Desktop;

public partial class MainWindow
{
    private static string CustomFontsDir => Path.Combine(UserData, "fonts");
    private static string FontPreviewCacheDir => Path.Combine(UserData, "font-preview-cache");
    private bool loadingDisplayFont;

    private void LoadDisplayFontSelector(string? savedFontId, string? savedUnityWrap = null, string? savedUnityScale = null, string? savedUnityShaping = null, string? savedUnityContrast = null)
    {
        loadingDisplayFont = true;
        try
        {
            var all = ArabicFontCatalog.All(CustomFontsDir);
            DisplayFontSelector.ItemsSource = all;
            var chosen = ArabicFontCatalog.Resolve(savedFontId, CustomFontsDir);
            DisplayFontSelector.SelectedItem = all.FirstOrDefault(f => f.Id.Equals(chosen.Id, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault();
            if (DisplayFontSelector.SelectedItem is ArabicFontChoice selected)
                UpdateFontPreview(selected);

            if (savedUnityWrap != null || savedUnityScale != null || savedUnityShaping != null || savedUnityContrast != null)
                LoadUnityArabicDisplayControls(savedUnityWrap, savedUnityScale, savedUnityShaping, savedUnityContrast);
        }
        finally { loadingDisplayFont = false; }
    }

    private void LoadUnityArabicDisplayControls(string? savedUnityWrap, string? savedUnityScale, string? savedUnityShaping, string? savedUnityContrast = null)
    {
        var opts = UnityArabicDisplayOptions.Parse(savedUnityWrap, savedUnityScale, savedUnityShaping, savedUnityContrast);
        string wrapTag = opts.WrapMode;
        UnityWrapModeSelector.SelectedItem = UnityWrapModeSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), wrapTag, StringComparison.OrdinalIgnoreCase))
            ?? UnityWrapModeSelector.Items.OfType<ComboBoxItem>().FirstOrDefault();

        string scaleTag = opts.FontScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        UnityFontScaleSelector.SelectedItem = UnityFontScaleSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), scaleTag, StringComparison.OrdinalIgnoreCase))
            ?? UnityFontScaleSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), "0.80", StringComparison.OrdinalIgnoreCase));

        UnityArabicShapingToggle.IsChecked = opts.ShapeArabic;
        if (UnityContrastBoostToggle != null) UnityContrastBoostToggle.IsChecked = opts.ContrastBoost;
        UnityWrapModeSelector.IsEnabled = opts.ShapeArabic;
    }

    private void SelectUnityArabicSettings(object sender, RoutedEventArgs e)
    {
        if (loadingDisplayFont || projectPath == null) return;
        string wrap = (UnityWrapModeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        string scale = (UnityFontScaleSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0.80";
        bool shape = UnityArabicShapingToggle.IsChecked != false;
        bool contrast = UnityContrastBoostToggle?.IsChecked == true;
        UnityWrapModeSelector.IsEnabled = shape;
        try
        {
            var opts = UnityArabicDisplayOptions.Parse(wrap, scale, shape ? "1" : "0", contrast ? "1" : "0");
            using var project = new ProjectStore(projectPath);
            new ProjectSettings(project).SaveUnityDisplayOptions(opts);
            string contrastLabel = opts.ContrastBoost ? " · تباين معزّز" : "";
            Status.Text = $"تم حفظ إعدادات العرض العربي لـ Unity (الالتفاف: {opts.WrapLabel} · الحجم: {opts.ScaleLabel}{contrastLabel}).";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void SelectDisplayFont(object sender, SelectionChangedEventArgs e)
    {
        if (loadingDisplayFont || DisplayFontSelector.SelectedItem is not ArabicFontChoice chosen) return;
        UpdateFontPreview(chosen);
        if (projectPath == null) return;
        try
        {
            using var project = new ProjectStore(projectPath);
            new ProjectSettings(project).DisplayFont = chosen.Id;
            Status.Text = $"تم اختيار الخط «{chosen.DisplayName}» — سيُطبّق تلقائياً عند التجربة أو بناء الحزمة.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void AddCustomFont(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر ملف خط عربي (.ttf أو .otf)",
            Filter = "ملفات الخطوط (*.ttf;*.otf)|*.ttf;*.otf"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            byte[] bytes = File.ReadAllBytes(dialog.FileName);
            var compat = ArabicFontCatalog.Inspect(bytes);
            if (!compat.IsValidFont)
            {
                MessageBox.Show(this, compat.Summary, "ملف خط غير صالح", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!compat.FullyCompatible)
            {
                string warning = compat.Summary + "\n\nهل تريد إضافته على أي حال؟ (يفضّل اختيار خط يدعم 115+ شكلاً عربياً حتى لا تختفي أي حروف داخل اللعبة.)";
                if (MessageBox.Show(this, warning, "فحص توافق الخط العربي", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

            Directory.CreateDirectory(CustomFontsDir);
            string fileName = Path.GetFileName(dialog.FileName);
            string destination = Path.Combine(CustomFontsDir, fileName);
            File.Copy(dialog.FileName, destination, overwrite: true);

            string newId = "custom:" + fileName;
            if (projectPath != null)
            {
                using var project = new ProjectStore(projectPath);
                new ProjectSettings(project).DisplayFont = newId;
            }
            LoadDisplayFontSelector(newId);
            Status.Text = $"أُضيف الخط الخاص «{Path.GetFileNameWithoutExtension(fileName)}» وحُفظ لمشاريعك.";
        }
        catch (Exception error)
        {
            Status.Text = "تعذّرت إضافة الخط. " + error.Message;
        }
    }

    private void DeleteCustomFont(object sender, RoutedEventArgs e)
    {
        if (DisplayFontSelector.SelectedItem is not ArabicFontChoice { IsCustom: true } chosen || string.IsNullOrEmpty(chosen.FilePath))
            return;
        if (MessageBox.Show(this, $"هل تريد حذف الخط الخاص «{chosen.DisplayName}» من مكتبتك؟",
            "حذف خط خاص", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            if (File.Exists(chosen.FilePath)) File.Delete(chosen.FilePath);
            if (projectPath != null)
            {
                using var project = new ProjectStore(projectPath);
                new ProjectSettings(project).DisplayFont = ArabicFontCatalog.DefaultFontId;
            }
            LoadDisplayFontSelector(ArabicFontCatalog.DefaultFontId);
            Status.Text = "حُذف الخط الخاص وأُعيد الخط الافتراضي.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void UpdateFontPreview(ArabicFontChoice choice)
    {
        DeleteCustomFontButton.IsEnabled = choice.IsCustom;
        try
        {
            byte[] bytes = ArabicFontCatalog.LoadBytes(choice);
            var compat = ArabicFontCatalog.Inspect(bytes);
            FontCompatibilityStatus.Text = $"[{choice.Category}]  {compat.Summary}";
            FontCompatibilityStatus.SetResourceReference(TextBlock.ForegroundProperty, compat.FullyCompatible ? "SuccessFg" : "WarningFg");

            string? previewPath = choice.FilePath;
            if (string.IsNullOrEmpty(previewPath))
            {
                Directory.CreateDirectory(FontPreviewCacheDir);
                string safeName = choice.Id.Replace(':', '_') + ".ttf";
                previewPath = Path.Combine(FontPreviewCacheDir, safeName);
                if (!File.Exists(previewPath) || new FileInfo(previewPath).Length != bytes.Length)
                    File.WriteAllBytes(previewPath, bytes);
            }

            FontFamily? family = TryLoadWpfFontFamily(previewPath, choice.FontFamilyName);
            if (family != null)
            {
                FontPreviewTitle.FontFamily = family;
                FontPreviewBody.FontFamily = family;
                FontPreviewMenu.FontFamily = family;
            }
        }
        catch (Exception error)
        {
            FontCompatibilityStatus.Text = "تعذّرت معاينة الخط: " + error.Message;
            FontCompatibilityStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerFg");
        }
    }

    private static FontFamily? TryLoadWpfFontFamily(string fontFilePath, string fallbackFamilyName)
    {
        try
        {
            string? dir = Path.GetDirectoryName(fontFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                var uri = new Uri(fontFilePath, UriKind.Absolute);
                var families = Fonts.GetFontFamilies(uri);
                var first = families.FirstOrDefault();
                if (first != null) return first;
            }
        }
        catch { }
        return string.IsNullOrWhiteSpace(fallbackFamilyName) ? null : new FontFamily(fallbackFamilyName);
    }
}
