using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Locara.Core;

namespace Locara.Desktop;

public partial class MainWindow
{
    private static readonly SolidColorBrush VerdictGreen = new(Color.FromRgb(0x14, 0x63, 0x56));
    private static readonly SolidColorBrush VerdictAmber = new(Color.FromRgb(0x9A, 0x5B, 0x00));
    private static readonly SolidColorBrush VerdictSlate = new(Color.FromRgb(0x4A, 0x65, 0x72));
    private static readonly SolidColorBrush VerdictRed = new(Color.FromRgb(0xA9, 0x32, 0x26));

    private sealed class ScannedFileRow(FileQualitySummary summary) : INotifyPropertyChanged
    {
        private bool included = summary.Included;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path => summary.Path;
        public string Format => summary.Format;
        public int TotalUnits => summary.TotalUnits;
        public int PendingUnits => summary.PendingUnits;
        public int ApprovedUnits => summary.ApprovedUnits;
        public FileQualityVerdict Verdict => summary.Verdict;
        public string Reason => summary.Reason;
        public IReadOnlyList<string> Samples => summary.Samples;

        public bool Included
        {
            get => included;
            set
            {
                if (included == value) return;
                included = value;
                PropertyChanged?.Invoke(this, new(nameof(Included)));
            }
        }

        public string UnitsSummary => summary.ApprovedUnits > 0
            ? $"{summary.TotalUnits:N0} ({summary.ApprovedUnits:N0} معتمد)"
            : $"{summary.TotalUnits:N0}";

        public string VerdictLabel => summary.Verdict switch
        {
            FileQualityVerdict.Translatable => "قابل للترجمة",
            FileQualityVerdict.Mixed => "مختلط — راجع العينة",
            FileQualityVerdict.SecondaryLanguage => "لغة إضافية",
            _ => "تقني / برمجي"
        };

        public Brush VerdictBrush => summary.Verdict switch
        {
            FileQualityVerdict.Translatable => VerdictGreen,
            FileQualityVerdict.Mixed => VerdictAmber,
            FileQualityVerdict.SecondaryLanguage => VerdictSlate,
            _ => VerdictRed
        };

        public string SamplesPreview => summary.Samples.Count == 0
            ? "—"
            : string.Join("  ·  ", summary.Samples.Take(4));
    }

    private int filesRefreshRequest;
    private bool updatingFileSelection;
    private List<ScannedFileRow> currentScannedFiles = [];

    private async void RefreshScannedFiles()
    {
        if (projectPath == null)
        {
            currentScannedFiles = [];
            ScannedFilesGrid.ItemsSource = null;
            UpdateFilesSummaryAndGuards();
            UpdateSelectedFilePreview(null);
            return;
        }

        string path = projectPath;
        int request = ++filesRefreshRequest;
        try
        {
            var summaries = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return new ProjectService(store).ScannedFiles();
            });
            if (!IsLoaded || request != filesRefreshRequest || projectPath != path) return;

            string? previousSelected = (ScannedFilesGrid.SelectedItem as ScannedFileRow)?.Path;
            currentScannedFiles = summaries.Select(s => new ScannedFileRow(s)).ToList();
            updatingFileSelection = true;
            try
            {
                ScannedFilesGrid.ItemsSource = currentScannedFiles;
                var toSelect = previousSelected != null
                    ? currentScannedFiles.FirstOrDefault(r => r.Path == previousSelected)
                    : currentScannedFiles.FirstOrDefault();
                ScannedFilesGrid.SelectedItem = toSelect;
                UpdateSelectedFilePreview(toSelect);
            }
            finally { updatingFileSelection = false; }

            UpdateFilesSummaryAndGuards();
        }
        catch (Exception error)
        {
            if (IsLoaded && request == filesRefreshRequest && projectPath == path)
                Status.Text = error.Message;
        }
    }

    private void UpdateFilesSummaryAndGuards()
    {
        if (currentScannedFiles.Count == 0)
        {
            FilesSelectionSummary.Text = "لا توجد ملفات مفحوصة بعد — اضغط «فحص الملفات» لاستعراض الملفات ومعاينة نصوصها.";
            FilesGuardBanner.Visibility = Visibility.Collapsed;
            TranslationFilesGuardText.Text = "";
            return;
        }

        int includedFiles = currentScannedFiles.Count(f => f.Included);
        int includedUnits = currentScannedFiles.Where(f => f.Included).Sum(f => f.TotalUnits);
        int excludedFiles = currentScannedFiles.Count - includedFiles;
        int excludedUnits = currentScannedFiles.Where(f => !f.Included).Sum(f => f.TotalUnits);
        int flaggedIncluded = currentScannedFiles.Count(f => f.Included && f.Verdict is FileQualityVerdict.Mixed or FileQualityVerdict.Technical);
        int technicalExcluded = currentScannedFiles.Count(f => !f.Included && f.Verdict == FileQualityVerdict.Technical);
        int secondaryExcluded = currentScannedFiles.Count(f => !f.Included && f.Verdict == FileQualityVerdict.SecondaryLanguage);

        FilesSelectionSummary.Text = excludedFiles == 0
            ? $"المفعّل للترجمة: {includedFiles:N0} ملف ({includedUnits:N0} نص)"
            : $"المفعّل للترجمة: {includedFiles:N0} ملف ({includedUnits:N0} نص)  ·  المستبعد: {excludedFiles:N0} ملف ({excludedUnits:N0} نص)";

        if (includedFiles == 0 && excludedFiles > 0)
        {
            FilesGuardBanner.Visibility = Visibility.Visible;
            FilesGuardTitle.Text = $"استُبعدت جميع الملفات المكتشفة ({excludedFiles:N0} ملف تقني/غير محدد) لحماية رصيدك";
            FilesGuardDetail.Text = "لم تُعثر على نصوص بشرية واضحة في الملفات المفحوصة. افتح تبويب «الملفات» لمعاينة العينات أو تحديد ملفات يدوياً.";
            TranslationFilesGuardText.Text = $"تنبيه: لا توجد ملفات مفعّلة للترجمة حالياً ({excludedFiles:N0} ملف مستبعد).";
        }
        else if (flaggedIncluded > 0 || technicalExcluded > 0 || secondaryExcluded > 0)
        {
            FilesGuardBanner.Visibility = Visibility.Visible;
            var parts = new List<string>();
            if (technicalExcluded > 0)
                parts.Add($"تم استبعاد {technicalExcluded:N0} ملف تقني تلقائياً لتوفير رصيدك");
            if (secondaryExcluded > 0)
                parts.Add($"تم استبعاد {secondaryExcluded:N0} حزمة لغة إضافية تلقائياً للاكتفاء بالإنجليزية");
            if (flaggedIncluded > 0)
                parts.Add($"يوجد {flaggedIncluded:N0} ملف مفعّل بتصنيف مختلط أو تقني — يُنصح بمراجعة عيناته");
            FilesGuardTitle.Text = "حماية ما قبل الترجمة (فحص جودة الملفات)";
            FilesGuardDetail.Text = string.Join(" · ", parts) + ".";
            TranslationFilesGuardText.Text = $"الملفات المفعّلة: {includedFiles:N0}/{currentScannedFiles.Count:N0} ({includedUnits:N0} نص)"
                + (flaggedIncluded > 0 ? $" · تنبيه: {flaggedIncluded:N0} ملف مختلط/تقني مفعّل" : "")
                + (secondaryExcluded > 0 ? $" · استُبعد {secondaryExcluded:N0} لغة إضافية تلقائياً" : "")
                + (technicalExcluded > 0 ? $" · استُبعد {technicalExcluded:N0} ملف تقني تلقائياً" : "");
        }
        else
        {
            FilesGuardBanner.Visibility = Visibility.Collapsed;
            TranslationFilesGuardText.Text = $"الملفات المفعّلة للترجمة: {includedFiles:N0} من {currentScannedFiles.Count:N0} ({includedUnits:N0} نص).";
        }
    }

    private void SelectScannedFileRow(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFileSelection) return;
        UpdateSelectedFilePreview(ScannedFilesGrid.SelectedItem as ScannedFileRow);
    }

    private void UpdateSelectedFilePreview(ScannedFileRow? row)
    {
        if (row == null)
        {
            SelectedFilePathText.Text = "اختر ملفاً من الجدول أعلاه لمعاينة أول 5 نصوص فيه قبل الترجمة.";
            SelectedFileReasonText.Text = "";
            SelectedFileSamplesText.Text = "";
            return;
        }

        SelectedFilePathText.Text = $"{row.Path}  [{row.Format}]  ·  {row.UnitsSummary} نص";
        SelectedFileReasonText.Text = $"التصنيف: {row.VerdictLabel} — {row.Reason}";
        SelectedFileReasonText.Foreground = row.VerdictBrush;
        SelectedFileSamplesText.Text = row.Samples.Count == 0
            ? "(لا توجد نصوص مستخرجة في هذا الملف)"
            : string.Join('\n', row.Samples.Select((s, i) => $"{i + 1}. {s}"));
    }

    private async void ToggleScannedFile(object sender, RoutedEventArgs e)
    {
        if (updatingFileSelection || projectPath == null || work != null) return;
        if (sender is not CheckBox cb || cb.DataContext is not ScannedFileRow row) return;
        bool included = cb.IsChecked == true;
        row.Included = included;

        string path = projectPath;
        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                new ProjectService(store).SetFileIncluded(row.Path, included);
            });
            UpdateFilesSummaryAndGuards();
            UpdateSelectedFilePreview(row);
            Status.Text = included
                ? $"تم تفعيل الملف «{row.Path}» للترجمة."
                : $"تم استبعاد الملف «{row.Path}» (لن يُترجم ولن يُعدّل عند البناء).";
            await RefreshRows();
            UpdateOverview();
        }
        catch (Exception error)
        {
            row.Included = !included;
            Status.Text = error.Message;
        }
    }

    private async void SelectAllScannedFiles(object sender, RoutedEventArgs e) =>
        await ApplyBulkFileSelection(_ => true, "تم تفعيل جميع الملفات للترجمة.");

    private async void SelectRecommendedScannedFiles(object sender, RoutedEventArgs e) =>
        await ApplyBulkFileSelection(f => f.Verdict is FileQualityVerdict.Translatable or FileQualityVerdict.Mixed,
            "تم تحديد الملفات الأساسية القابلة للترجمة فقط واستبعاد الملفات التقنية واللغات الإضافية.");

    private async void DeselectAllScannedFiles(object sender, RoutedEventArgs e) =>
        await ApplyBulkFileSelection(_ => false, "تم إلغاء تحديد جميع الملفات.");

    private async Task ApplyBulkFileSelection(Func<ScannedFileRow, bool> predicate, string message)
    {
        if (projectPath == null || work != null || currentScannedFiles.Count == 0) return;
        string path = projectPath;
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in currentScannedFiles)
        {
            bool next = predicate(row);
            row.Included = next;
            map[row.Path] = next;
        }

        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                new ProjectService(store).SetFilesIncluded(map);
            });
            UpdateFilesSummaryAndGuards();
            Status.Text = message;
            await RefreshRows();
            UpdateOverview();
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
            RefreshScannedFiles();
        }
    }
}
