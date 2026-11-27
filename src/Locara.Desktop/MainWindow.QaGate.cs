using System.Text;
using System.Windows;
using System.Windows.Controls;
using Locara.Core;

namespace Locara.Desktop;

/// <summary>Pre-build QA readiness gate card on the packages page and its editor jump-links.</summary>
public partial class MainWindow
{
    private PreBuildQaReport? lastQaReport;
    private HashSet<string> lengthWarningUnitIds = new(StringComparer.Ordinal);
    private int qaGateRequest;

    private void RefreshQaGate() => _ = RefreshQaGateAsync();

    private void ResetQaGateState()
    {
        qaGateRequest++;
        lastQaReport = null;
        lengthWarningUnitIds = new HashSet<string>(StringComparer.Ordinal);
        if (EditorChipLength != null)
        {
            EditorChipLength.Visibility = Visibility.Collapsed;
            EditorChipLength.Content = "تحذيرات الطول";
        }
        if (ReviewStatus == "length" && ReviewFilter != null)
            ReviewFilter.SelectedValue = "";
        ResetQaGateUi();
    }

    private void ResetQaGateUi()
    {
        if (QaGateCard == null) return;
        SetQaBadge("…", "TextMuted", "BgSubtle", "BorderSubtle");
        QaGateSummaryText.Text = "افتح مشروعاً وافحص الملفات لتفعيل بوابة الجاهزية.";
        foreach (var (count, button) in QaRows())
        {
            count.Text = "—";
            count.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            button.IsEnabled = false;
        }
    }

    private IEnumerable<(TextBlock Count, Button Fix)> QaRows()
    {
        yield return (QaCountIssues, QaFixIssuesButton);
        yield return (QaCountGlossary, QaFixGlossaryButton);
        yield return (QaCountPending, QaFixPendingButton);
        yield return (QaCountLength, QaFixLengthButton);
    }

    private void SetQaBadge(string text, string fgKey, string bgKey, string borderKey)
    {
        QaGateBadgeText.Text = text;
        QaGateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
        QaGateBadge.SetResourceReference(Border.BackgroundProperty, bgKey);
        QaGateBadge.SetResourceReference(Border.BorderBrushProperty, borderKey);
    }

    private async Task<PreBuildQaReport?> RefreshQaGateAsync()
    {
        if (QaGateCard == null) return null;
        if (projectPath is not { } path)
        {
            ResetQaGateUi();
            return null;
        }
        if (work != null) return null;

        int request = ++qaGateRequest;
        QaGateRescanButton.IsEnabled = false;
        SetQaBadge("جارٍ الفحص…", "TextMuted", "BgSubtle", "BorderSubtle");
        try
        {
            var report = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return PreBuildQaGate.Evaluate(store);
            });
            if (!IsLoaded || request != qaGateRequest || projectPath != path) return null;
            ApplyQaReport(report);
            return report;
        }
        catch (Exception error)
        {
            if (IsLoaded && request == qaGateRequest && projectPath == path)
            {
                SetQaBadge("تعذّر الفحص", "WarningFg", "WarningBg", "WarningFg");
                QaGateSummaryText.Text = error.Message;
            }
            return null;
        }
        finally
        {
            if (request == qaGateRequest && QaGateRescanButton != null)
                QaGateRescanButton.IsEnabled = true;
        }
    }

    private void ApplyQaReport(PreBuildQaReport report)
    {
        lastQaReport = report;
        lengthWarningUnitIds = new HashSet<string>(report.LengthWarnings.UnitIds, StringComparer.Ordinal);

        if (QaGateCard != null)
        {
            switch (report.Level)
            {
                case QaReadinessLevel.Blocked:
                    SetQaBadge("محظور — أخطاء حرجة", "DangerFg", "DangerBg", "DangerFg");
                    break;
                case QaReadinessLevel.WarningsOnly:
                    SetQaBadge("جاهز مع تنبيهات", "WarningFg", "WarningBg", "WarningFg");
                    break;
                default:
                    SetQaBadge("جاهز ✓", "SuccessFg", "SuccessBg", "SuccessFg");
                    break;
            }
            QaGateSummaryText.Text = report.Summary;

            var categories = new[] { report.TokenErrors, report.GlossaryViolations, report.UntranslatedUnits, report.LengthWarnings };
            var rows = QaRows().ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var category = categories[i];
                rows[i].Count.Text = category.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                string fgKey = category.Count == 0 ? "SuccessFg" : category.IsBlocking ? "DangerFg" : "WarningFg";
                rows[i].Count.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
                rows[i].Fix.IsEnabled = category.Count > 0 && work == null;
            }
        }

        if (EditorChipLength != null)
        {
            bool hasLength = report.LengthWarnings.Count > 0;
            EditorChipLength.Visibility = hasLength ? Visibility.Visible : Visibility.Collapsed;
            EditorChipLength.Content = hasLength ? $"تحذيرات الطول ({report.LengthWarnings.Count:N0})" : "تحذيرات الطول";
        }
    }

    private async void QaGateRescanClick(object sender, RoutedEventArgs e)
    {
        if (!SaveEdits()) return;
        var report = await RefreshQaGateAsync();
        if (report != null && IsLoaded)
            Status.Text = report.Summary;
    }

    private async void QaGateFixClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || work != null || lastQaReport is not { } report || !SaveEdits())
            return;

        if (key == "glossary")
            ApplyGlossaryAuditReport(report.GlossaryAudit);

        SelectStage(EditorPage);
        Search.Clear();
        offset = 0;
        ReviewFilter.SelectedValue = key;
        await RefreshRows();
    }

    private static string BuildQaConfirmSection(PreBuildQaReport? report)
    {
        if (report == null) return "";
        if (!report.HasBlockers && report.GlossaryViolations.Count == 0 && report.LengthWarnings.Count == 0
            && report.UntranslatedUnits.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.Append("\n\n");
        if (report.HasBlockers)
        {
            sb.Append("⛔ تحذير حرج من بوابة الجاهزية: ").Append(report.Summary).Append('\n');
            sb.Append("أسطر الرموز المفقودة أو المتعارضة قد تُكسر اللعبة أو تُستبعد من البناء — يُنصح بإصلاحها في المحرّر أولاً.");
        }
        else
        {
            sb.Append("⚠ تنبيهات بوابة الجاهزية (لا تمنع التثبيت):");
        }
        foreach (var category in new[] { report.GlossaryViolations, report.UntranslatedUnits, report.LengthWarnings })
        {
            if (category.Count > 0)
                sb.Append("\n• ").Append(category.Title).Append(": ").Append(category.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
