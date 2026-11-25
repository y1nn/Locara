using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Locara.Core;

namespace Locara.Desktop;

public partial class MainWindow
{
    private static Brush LookupThemeBrush(string key, Color fallback) =>
        Application.Current?.Resources[key] as Brush ?? new SolidColorBrush(fallback);

    private sealed class ScannedFileRow(FileQualitySummary summary) : INotifyPropertyChanged
    {
        private bool included = summary.Included;
        private int includedUnits = summary.IncludedUnits;
        private bool humanOnlyFilter = summary.HumanOnlyFilter;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path => summary.Path;
        public string Format => summary.Format;
        public int TotalUnits => summary.TotalUnits;
        public int PendingUnits => summary.PendingUnits;
        public int ApprovedUnits => summary.ApprovedUnits;
        public int HumanCount => summary.HumanCount;
        public int TechnicalCount => summary.TechnicalCount;
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
                PropertyChanged?.Invoke(this, new(nameof(UnitsSummary)));
            }
        }

        public int IncludedUnits
        {
            get => includedUnits;
            set
            {
                if (includedUnits == value) return;
                includedUnits = value;
                PropertyChanged?.Invoke(this, new(nameof(IncludedUnits)));
                PropertyChanged?.Invoke(this, new(nameof(UnitsSummary)));
            }
        }

        public bool HumanOnlyFilter
        {
            get => humanOnlyFilter;
            set
            {
                if (humanOnlyFilter == value) return;
                humanOnlyFilter = value;
                PropertyChanged?.Invoke(this, new(nameof(HumanOnlyFilter)));
                PropertyChanged?.Invoke(this, new(nameof(UnitsSummary)));
            }
        }

        public string UnitsSummary
        {
            get
            {
                if (!included && summary.TotalUnits == 0 && summary.Verdict is FileQualityVerdict.SecondaryLanguage or FileQualityVerdict.AuxiliaryData)
                    return "مُتخطّى";
                if (included && humanOnlyFilter && includedUnits < summary.TotalUnits)
                    return $"{includedUnits:N0}/{summary.TotalUnits:N0} (بشري)";
                return summary.ApprovedUnits > 0
                    ? $"{summary.TotalUnits:N0} ({summary.ApprovedUnits:N0} معتمد)"
                    : $"{summary.TotalUnits:N0}";
            }
        }

        public string VerdictLabel => summary.Verdict switch
        {
            FileQualityVerdict.Translatable => "قابل للترجمة",
            FileQualityVerdict.Mixed => "مختلط — راجع العينة",
            FileQualityVerdict.AuxiliaryData => "بيانات جانبية",
            FileQualityVerdict.SecondaryLanguage => "لغة إضافية",
            _ => "تقني / برمجي"
        };

        public Brush VerdictBrush => summary.Verdict switch
        {
            FileQualityVerdict.Translatable => LookupThemeBrush("SuccessFg", Color.FromRgb(0x28, 0xCE, 0x9B)),
            FileQualityVerdict.Mixed => LookupThemeBrush("WarningFg", Color.FromRgb(0xEA, 0xA8, 0x3B)),
            FileQualityVerdict.AuxiliaryData => LookupThemeBrush("TextMuted", Color.FromRgb(0x66, 0x7B, 0x81)),
            FileQualityVerdict.SecondaryLanguage => LookupThemeBrush("InfoFg", Color.FromRgb(0x47, 0xBD, 0xF8)),
            _ => LookupThemeBrush("DangerFg", Color.FromRgb(0xF0, 0x6A, 0x6A))
        };

        public string SamplesPreview => summary.Samples.Count == 0
            ? "—"
            : string.Join("  ·  ", summary.Samples.Take(4));
    }

    private int filesRefreshRequest;
    private bool updatingFileSelection;
    private bool updatingFiles;
    private List<ScannedFileRow> currentScannedFiles = [];

    private void FilterScannedFilesChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyScannedFilesFilter();
    }

    private void ApplyScannedFilesFilter(string? preferredPath = null)
    {
        string query = (FilesSearchBox?.Text ?? "").Trim();
        string tag = ((FilesVerdictFilter?.SelectedItem as ComboBoxItem)?.Tag as string) ?? "all";
        string? selectedPath = preferredPath ?? (ScannedFilesGrid.SelectedItem as ScannedFileRow)?.Path;

        var filtered = currentScannedFiles.Where(row =>
        {
            bool verdictOk = tag switch
            {
                "included" => row.Included,
                "translatable" => row.Verdict == FileQualityVerdict.Translatable,
                "mixed" => row.Verdict == FileQualityVerdict.Mixed,
                "auxiliary" => row.Verdict == FileQualityVerdict.AuxiliaryData,
                "secondary" => row.Verdict == FileQualityVerdict.SecondaryLanguage,
                "technical" => row.Verdict == FileQualityVerdict.Technical,
                _ => true
            };
            if (!verdictOk) return false;
            if (query.Length == 0) return true;
            return row.Path.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Format.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.SamplesPreview.Contains(query, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        updatingFileSelection = true;
        try
        {
            ScannedFilesGrid.ItemsSource = filtered;
            var toSelect = selectedPath != null
                ? filtered.FirstOrDefault(r => r.Path == selectedPath)
                : filtered.FirstOrDefault();
            ScannedFilesGrid.SelectedItem = toSelect;
            UpdateSelectedFilePreview(toSelect);
        }
        finally { updatingFileSelection = false; }
    }

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
            ApplyScannedFilesFilter(previousSelected);
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
            FilesSelectionSummary.Text = "لا توجد ملفات مفحوصة بعد — اضغط «افحص ملفات اللعبة» لاستعراض الملفات ومعاينة نصوصها.";
            FilesGuardBanner.Visibility = Visibility.Collapsed;
            TranslationFilesGuardText.Text = "";
            if (NavFiles != null) NavFiles.Tag = "";
            return;
        }

        int includedFiles = currentScannedFiles.Count(f => f.Included);
        int includedUnits = currentScannedFiles.Where(f => f.Included).Sum(f => f.IncludedUnits);
        int totalUnits = currentScannedFiles.Sum(f => f.TotalUnits);
        int excludedFiles = currentScannedFiles.Count - includedFiles;
        int excludedUnits = Math.Max(0, totalUnits - includedUnits);
        int flaggedIncluded = currentScannedFiles.Count(f => f.Included && !f.HumanOnlyFilter
            && f.Verdict is FileQualityVerdict.Mixed or FileQualityVerdict.Technical);
        int technicalExcluded = currentScannedFiles.Count(f => !f.Included && f.Verdict == FileQualityVerdict.Technical);
        int mixedExcluded = currentScannedFiles.Count(f => !f.Included && f.Verdict == FileQualityVerdict.Mixed);
        int auxiliaryExcluded = currentScannedFiles.Count(f => !f.Included && f.Verdict == FileQualityVerdict.AuxiliaryData);
        int secondaryExcluded = currentScannedFiles.Count(f => !f.Included && f.Verdict == FileQualityVerdict.SecondaryLanguage);
        bool hasPotOnlyWithSiblingPo = currentScannedFiles.Any(f => f.Included && f.Format == "po" && ProjectService.IsGettextTemplate(f.Path))
            && !currentScannedFiles.Any(f => f.Included && f.Format == "po" && !ProjectService.IsGettextTemplate(f.Path))
            && currentScannedFiles.Any(f => !f.Included && f.Format == "po" && !ProjectService.IsGettextTemplate(f.Path));

        if (NavFiles != null)
            NavFiles.Tag = includedFiles > 0 ? $"{includedFiles}" : "!";
        if (ContinueToBotsBtn != null)
            ContinueToBotsBtn.Visibility = includedUnits > 0 ? Visibility.Visible : Visibility.Collapsed;

        FilesSelectionSummary.Text = excludedFiles == 0 && excludedUnits == 0
            ? $"المفعّل للترجمة: {includedFiles:N0} ملف ({includedUnits:N0} نص)"
            : $"المفعّل للترجمة: {includedFiles:N0} ملف ({includedUnits:N0} نص)  ·  المستبعد: {excludedFiles:N0} ملف ({excludedUnits:N0} نص)";

        FilesGuardBanner.Visibility = Visibility.Visible;
        UpdateHeaderPrimaryVisibility();
        if (includedFiles == 0 && excludedFiles > 0)
        {
            FilesGuardTitle.Text = $"استُبعدت جميع الملفات المكتشفة ({excludedFiles:N0} ملف تقني/مختلط) لحماية رصيدك";
            FilesGuardTitle.SetResourceReference(TextBlock.ForegroundProperty, "WarningFg");
            FilesGuardDetail.Text = "شغّل «تحليل ذكي (AI Scout)» أو عاين العينات أدناه وفعّل النصوص البشرية فقط.";
            TranslationFilesGuardText.Text = $"تنبيه: لا توجد ملفات مفعّلة للترجمة حالياً ({excludedFiles:N0} ملف مستبعد).";
        }
        else if (flaggedIncluded > 0 || technicalExcluded > 0 || mixedExcluded > 0 || auxiliaryExcluded > 0 || secondaryExcluded > 0 || hasPotOnlyWithSiblingPo)
        {
            var parts = new List<string>();
            if (auxiliaryExcluded > 0)
                parts.Add($"استُبعد {auxiliaryExcluded:N0} ملف بيانات جانبي لوجود ملفات ترجمة رسمية");
            if (mixedExcluded > 0)
                parts.Add($"استُبعد {mixedExcluded:N0} ملف مختلط افتراضياً لحماية اللعبة");
            if (technicalExcluded > 0)
                parts.Add($"استُبعد {technicalExcluded:N0} ملف تقني تلقائياً");
            if (secondaryExcluded > 0)
                parts.Add($"استُبعد {secondaryExcluded:N0} ملف لغة إضافية للاكتفاء بالإنجليزية");
            if (flaggedIncluded > 0)
                parts.Add($"يوجد {flaggedIncluded:N0} ملف مفعّل بتصنيف مختلط أو تقني — يُنصح باستخدام «تحليل ذكي (AI Scout)» أو «تفعيل النصوص البشرية فقط»");
            if (hasPotOnlyWithSiblingPo)
                parts.Add("ملاحظة Gettext: النصوص الحالية في قالب (.pot) ومعظم الألعاب تقرأ ملف (.po) عند التثبيت");
            FilesGuardTitle.Text = $"المصدر جاهز للتعريب — {includedFiles:N0} ملف مفعّل ({includedUnits:N0} نص)";
            FilesGuardTitle.SetResourceReference(TextBlock.ForegroundProperty, flaggedIncluded > 0 ? "WarningFg" : "AccentIndicator");
            FilesGuardDetail.Text = string.Join(" · ", parts) + ".";
            var brief = new List<string> { $"الملفات المفعّلة: {includedFiles:N0}/{currentScannedFiles.Count:N0} ({includedUnits:N0} نص)" };
            if (flaggedIncluded > 0) brief.Add($"تنبيه: {flaggedIncluded:N0} ملف مختلط/تقني مفعّل");
            if (auxiliaryExcluded > 0) brief.Add($"استُبعد {auxiliaryExcluded:N0} بيانات جانبية");
            if (mixedExcluded > 0) brief.Add($"استُبعد {mixedExcluded:N0} مختلط");
            if (secondaryExcluded > 0) brief.Add($"استُبعد {secondaryExcluded:N0} لغة إضافية");
            if (technicalExcluded > 0) brief.Add($"استُبعد {technicalExcluded:N0} تقني");
            TranslationFilesGuardText.Text = string.Join(" · ", brief);
        }
        else
        {
            FilesGuardTitle.Text = $"الملفات صافية وجاهزة للتعريب ({includedFiles:N0} ملف · {includedUnits:N0} نص)";
            FilesGuardTitle.SetResourceReference(TextBlock.ForegroundProperty, "SuccessFg");
            FilesGuardDetail.Text = "لم يُكتشف أي تداخل تقني في الملفات المحددة — انتقل إلى غرفة البوتات لبدء التعريب التلقائي.";
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
            if (IncludeHumanOnlyButton != null) IncludeHumanOnlyButton.Visibility = Visibility.Collapsed;
            return;
        }

        SelectedFilePathText.Text = $"{row.Path}  [{row.Format}]  ·  {row.UnitsSummary} نص";
        SelectedFileReasonText.Text = row.HumanOnlyFilter
            ? $"التصنيف: {row.VerdictLabel} — مُفعّل بوضع «النصوص البشرية فقط» ({row.IncludedUnits:N0} من {row.TotalUnits:N0} نص، مع حماية القيم التقنية)."
            : $"التصنيف: {row.VerdictLabel} — {row.Reason}";
        SelectedFileReasonText.Foreground = row.VerdictBrush;
        SelectedFileSamplesText.Text = row.Samples.Count == 0
            ? (row.Verdict == FileQualityVerdict.SecondaryLanguage && !row.Included
                ? "(تم تخطي تحليل ملف اللغة الإضافية لتسريع الفحص وتوفير الذاكرة — فعّل الملف من مربع التضمين لاستخراج نصوصه فوراً)"
                : row.Verdict == FileQualityVerdict.AuxiliaryData && !row.Included
                    ? "(تم تخطي تحليل الملف الجانبي لتسريع الفحص لوجود ملفات ترجمة رسمية — فعّل الملف من مربع التضمين لاستخراج نصوصه فوراً)"
                    : "(لا توجد نصوص مستخرجة في هذا الملف)")
            : string.Join('\n', row.Samples.Select((s, i) => $"{i + 1}. {s}"));

        if (IncludeHumanOnlyButton != null)
        {
            bool canFilterHuman = row.HumanCount > 0 && row.TechnicalCount > 0 && (!row.Included || !row.HumanOnlyFilter);
            IncludeHumanOnlyButton.Visibility = canFilterHuman ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void IncludeHumanOnlySelectedFile(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || work != null || updatingFiles || ScannedFilesGrid.SelectedItem is not ScannedFileRow row) return;
        string path = projectPath;
        string pwd = ZipPassword.Password;
        updatingFiles = true;
        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                new ProjectService(store).SetFileIncluded(row.Path, included: true, humanOnly: true, pwd);
            });
            if (!IsLoaded || projectPath != path) return;
            Status.Text = $"تم تفعيل النصوص البشرية فقط في «{row.Path}» وحماية القيم التقنية من التعديل.";
            RefreshScannedFiles();
            await RefreshRows();
            UpdateOverview();
        }
        catch (Exception error)
        {
            if (IsLoaded && projectPath == path) Status.Text = error.Message;
        }
        finally { updatingFiles = false; }
    }

    private async void ToggleScannedFile(object sender, RoutedEventArgs e)
    {
        if (updatingFileSelection || updatingFiles || projectPath == null || work != null) return;
        if (sender is not CheckBox cb || cb.DataContext is not ScannedFileRow row) return;
        bool included = cb.IsChecked == true;
        row.Included = included;
        row.HumanOnlyFilter = false;
        row.IncludedUnits = included ? row.TotalUnits : 0;

        bool wasLazyZero = included && row.TotalUnits == 0;
        string path = projectPath;
        string pwd = ZipPassword.Password;
        updatingFiles = true;
        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                new ProjectService(store).SetFileIncluded(row.Path, included, humanOnly: false, pwd);
            });
            if (!IsLoaded || projectPath != path) return;
            if (wasLazyZero) RefreshScannedFiles();
            else
            {
                UpdateFilesSummaryAndGuards();
                UpdateSelectedFilePreview(row);
            }
            Status.Text = included
                ? $"تم تفعيل الملف «{row.Path}» للترجمة."
                : $"تم استبعاد الملف «{row.Path}» (لن يُترجم ولن يُعدّل عند البناء).";
            await RefreshRows();
            UpdateOverview();
        }
        catch (Exception error)
        {
            row.Included = !included;
            if (IsLoaded && projectPath == path)
            {
                Status.Text = error.Message;
                RefreshScannedFiles();
            }
        }
        finally { updatingFiles = false; }
    }

    private async void SelectAllScannedFiles(object sender, RoutedEventArgs e) =>
        await ApplyBulkFileSelection(_ => true, "تم تفعيل جميع الملفات للترجمة.");

    private async void SelectRecommendedScannedFiles(object sender, RoutedEventArgs e) =>
        await ApplyBulkFileSelection(f => f.Verdict == FileQualityVerdict.Translatable,
            "تم تحديد الملفات القابلة للترجمة الصافية فقط واستبعاد الملفات المختلطة والجانبية والتقنية واللغات الإضافية.");

    private async void DeselectAllScannedFiles(object sender, RoutedEventArgs e) =>
        await ApplyBulkFileSelection(_ => false, "تم إلغاء تحديد جميع الملفات.");

    private async Task ApplyBulkFileSelection(Func<ScannedFileRow, bool> predicate, string message)
    {
        if (projectPath == null || work != null || updatingFiles || currentScannedFiles.Count == 0) return;
        string path = projectPath;
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in currentScannedFiles)
        {
            bool next = predicate(row);
            row.Included = next;
            row.HumanOnlyFilter = false;
            row.IncludedUnits = next ? row.TotalUnits : 0;
            map[row.Path] = next;
        }

        bool anyLazyIncluded = currentScannedFiles.Any(r => r.Included && r.TotalUnits == 0);
        string pwd = ZipPassword.Password;
        updatingFiles = true;
        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                new ProjectService(store).SetFilesIncluded(map, pwd);
            });
            if (!IsLoaded || projectPath != path) return;
            if (anyLazyIncluded) RefreshScannedFiles();
            else
            {
                UpdateFilesSummaryAndGuards();
                UpdateSelectedFilePreview(ScannedFilesGrid.SelectedItem as ScannedFileRow);
            }
            Status.Text = message;
            await RefreshRows();
            UpdateOverview();
        }
        catch (Exception error)
        {
            if (IsLoaded && projectPath == path)
            {
                Status.Text = error.Message;
                RefreshScannedFiles();
            }
        }
        finally { updatingFiles = false; }
    }

    private async void RunAiScout(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path || work != null) return;
        if (currentScannedFiles.Count == 0)
        {
            Status.Text = "افحص ملفات اللعبة أولاً قبل تشغيل كشّاف الذكاء الاصطناعي.";
            return;
        }
        ProviderOptions options;
        try { options = ReadProvider(); }
        catch (Exception error)
        {
            OpenConnectionSettings();
            Status.Text = "أكمل إعداد اتصال الذكاء الاصطناعي أولاً: " + error.Message;
            return;
        }

        string password = ZipPassword.Password;
        work = new CancellationTokenSource();
        var token = work.Token;
        if (RunAiScoutButton != null) RunAiScoutButton.IsEnabled = false;
        Status.Text = $"جارٍ تحليل هياكل الملفات عبر كشّاف الذكاء الاصطناعي ({options.Provider} / {options.Model})…";
        try
        {
            ScoutPlan plan = await Task.Run(async () =>
            {
                using var store = new ProjectStore(path);
                return await LocalizationScout.AnalyzeAsync(store, options, token);
            }, token);

            if (plan.Recommendations.Count == 0)
            {
                Status.Text = "لا توجد ملفات مختلطة أو غير محسومة تحتاج تحليل كشّاف الذكاء الاصطناعي — ملفاتك صافية بالفعل.";
                return;
            }

            var dialog = new ScoutPlanWindow(plan) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                Status.Text = "تم إلغاء تطبيق خطة كشّاف الذكاء الاصطناعي.";
                return;
            }

            var selected = dialog.SelectedClusterIds;
            if (selected.Count == 0)
            {
                Status.Text = "لم يُحدَّد أي صف من توصيات كشّاف الذكاء الاصطناعي.";
                return;
            }

            Status.Text = "جارٍ تطبيق قواعد الاستخراج وتحديث الفحص…";
            ScoutApplyResult applied = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return new ProjectService(store).ApplyScoutPlan(plan, selected, password, token);
            }, token);

            Status.Text = $"طُبّقت توصيات {applied.AppliedClusters:N0} مجموعة ({applied.IncludedFiles:N0} ملف مفعّل · {applied.ConfiguredRuleFiles:N0} قاعدة حقول · {applied.ExcludedFiles:N0} مستبعد) — النصوص النشطة الآن: {applied.ActiveUnitsAfterScan:N0}.";
            RefreshScannedFiles();
            await RefreshRows();
            UpdateOverview();
        }
        catch (OperationCanceledException)
        {
            Status.Text = "تم إلغاء كشّاف الذكاء الاصطناعي.";
        }
        catch (Exception error)
        {
            Status.Text = "تعذّر إكمال كشّاف الذكاء الاصطناعي: " + error.Message;
        }
        finally
        {
            work?.Dispose();
            work = null;
            if (RunAiScoutButton != null) RunAiScoutButton.IsEnabled = true;
        }
    }
}
