namespace Locara.Core;

public enum QaReadinessLevel
{
    Ready,
    WarningsOnly,
    Blocked
}

public sealed record QaCategoryReport(
    string Key,
    string Title,
    int Count,
    bool IsBlocking,
    IReadOnlyList<string> UnitIds,
    string Summary);

public sealed record PreBuildQaReport(
    QaReadinessLevel Level,
    int TotalActiveUnits,
    int ApprovedUnits,
    int DraftUnits,
    QaCategoryReport TokenErrors,
    QaCategoryReport GlossaryViolations,
    QaCategoryReport UntranslatedUnits,
    QaCategoryReport LengthWarnings,
    int LengthOverflowCount,
    GlossaryAuditReport GlossaryAudit,
    IReadOnlyList<string> BlockingScanErrors)
{
    public bool HasBlockers => Level == QaReadinessLevel.Blocked;

    public bool HasWarnings =>
        GlossaryViolations.Count > 0
        || UntranslatedUnits.Count > 0
        || LengthWarnings.Count > 0
        || DraftUnits > 0;

    public string Summary => BuildSummary();

    private string BuildSummary()
    {
        if (Level == QaReadinessLevel.Blocked)
        {
            var blockers = new List<string>(2);
            if (TokenErrors.Count > 0)
                blockers.Add($"{TokenErrors.Count} خطأ رموز/تعارض");
            if (BlockingScanErrors.Count > 0)
                blockers.Add($"{BlockingScanErrors.Count} خطأ فحص");
            return "محظور قبل البناء: " + string.Join(" · ", blockers) + ".";
        }

        if (TotalActiveUnits == 0)
            return "لا توجد أسطر نشطة للبناء حالياً — افحص مصدر اللعبة أولاً.";

        if (Level == QaReadinessLevel.Ready)
            return $"جاهز للبناء — جميع الأسطر النشطة معتمدة ({ApprovedUnits}/{TotalActiveUnits}) وخالية من الأخطاء والتنبيهات.";

        var warnings = new List<string>(4);
        if (UntranslatedUnits.Count > 0)
            warnings.Add($"{UntranslatedUnits.Count} غير مترجم");
        if (DraftUnits > 0)
            warnings.Add($"{DraftUnits} مسودة غير معتمدة");
        if (GlossaryViolations.Count > 0)
            warnings.Add($"{GlossaryViolations.Count} مخالفة معجم");
        if (LengthWarnings.Count > 0)
            warnings.Add($"{LengthWarnings.Count} تنبيه طول واجهة");

        return "جاهز مع تنبيهات: " + string.Join(" · ", warnings) + ".";
    }
}

/// <summary>
/// Evaluates pre-build translation readiness across all active units in a project,
/// separating build-blocking token/conflict/scan errors from non-blocking quality warnings.
/// </summary>
public static class PreBuildQaGate
{
    public static PreBuildQaReport Evaluate(ProjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var settings = new ProjectSettings(store);
        string engine = settings.ScannedEngine;
        var blockingScanErrors = settings.ReadScanErrors()
            .Select(FormatDiagnostic)
            .Where(text => text.Length > 0)
            .ToList();

        var activeUnits = store.Units(limit: -1);

        var tokenErrorIds = new List<string>();
        var untranslatedIds = new List<string>();
        var lengthOverflowIds = new List<string>();
        var lengthWarnOnlyIds = new List<string>();

        int approvedUnits = 0;
        int draftUnits = 0;

        foreach (var unit in activeUnits)
        {
            bool hasTarget = !string.IsNullOrWhiteSpace(unit.Target);
            bool hasTokenError = string.Equals(unit.Status, "conflict", StringComparison.Ordinal)
                || !string.IsNullOrWhiteSpace(unit.Warning)
                || (hasTarget && Tokens.Validate(unit.Source, unit.Target, engine).Length > 0);

            if (hasTokenError)
            {
                tokenErrorIds.Add(unit.Id);
            }
            else if (string.Equals(unit.Status, "approved", StringComparison.Ordinal))
            {
                approvedUnits++;
            }
            else if (hasTarget && (string.Equals(unit.Status, "translated", StringComparison.Ordinal)
                || string.Equals(unit.Status, "imported", StringComparison.Ordinal)))
            {
                draftUnits++;
            }

            if (!hasTarget || string.Equals(unit.Status, "pending", StringComparison.Ordinal))
                untranslatedIds.Add(unit.Id);

            if (hasTarget)
            {
                var lengthEval = UiLengthGuard.Evaluate(unit.Source, unit.Target, unit.Context, unit.Locator, engine);
                if (lengthEval.Severity == UiLengthSeverity.Overflow)
                    lengthOverflowIds.Add(unit.Id);
                else if (lengthEval.Severity == UiLengthSeverity.Warning)
                    lengthWarnOnlyIds.Add(unit.Id);
            }
        }

        var glossaryAudit = GlossaryConsistencyChecker.AuditProject(store, activeUnits);
        var glossaryIds = new List<string>(glossaryAudit.Items.Count);
        var seenGlossaryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in glossaryAudit.Items)
        {
            if (seenGlossaryIds.Add(item.Unit.Id))
                glossaryIds.Add(item.Unit.Id);
        }

        int lengthOverflowCount = lengthOverflowIds.Count;
        var lengthAllIds = new List<string>(lengthOverflowCount + lengthWarnOnlyIds.Count);
        lengthAllIds.AddRange(lengthOverflowIds);
        lengthAllIds.AddRange(lengthWarnOnlyIds);

        var tokenErrors = new QaCategoryReport(
            Key: "issues",
            Title: "أخطاء الرموز والتعارضات",
            Count: tokenErrorIds.Count,
            IsBlocking: true,
            UnitIds: tokenErrorIds,
            Summary: tokenErrorIds.Count == 0
                ? "لا توجد أخطاء رموز أو تعارضات."
                : $"{tokenErrorIds.Count} سطر يحتوي على رموز مفقودة أو زائدة أو تعارض يمنع البناء.");

        var glossaryViolations = new QaCategoryReport(
            Key: "glossary",
            Title: "مخالفات المعجم اليدوي",
            Count: glossaryIds.Count,
            IsBlocking: false,
            UnitIds: glossaryIds,
            Summary: glossaryAudit.ManualTermCount == 0
                ? "لا توجد مصطلحات في المعجم اليدوي."
                : glossaryIds.Count == 0
                    ? "جميع الأسطر المترجمة ملتزمة بالمعجم اليدوي."
                    : $"{glossaryIds.Count} سطر يخالف المعجم اليدوي ({glossaryAudit.TotalTermViolations} مخالفة).");

        var untranslatedUnits = new QaCategoryReport(
            Key: "pending",
            Title: "أسطر غير مترجمة",
            Count: untranslatedIds.Count,
            IsBlocking: false,
            UnitIds: untranslatedIds,
            Summary: untranslatedIds.Count == 0
                ? "جميع الأسطر النشطة مترجمة."
                : $"{untranslatedIds.Count} سطر بانتظار الترجمة.");

        var lengthWarnings = new QaCategoryReport(
            Key: "length",
            Title: "تنبيهات طول نص الواجهة",
            Count: lengthAllIds.Count,
            IsBlocking: false,
            UnitIds: lengthAllIds,
            Summary: lengthAllIds.Count == 0
                ? "أطوال نصوص الواجهة ضمن الحدود المناسبة."
                : lengthOverflowCount > 0 && lengthWarnOnlyIds.Count > 0
                    ? $"{lengthAllIds.Count} سطر طويل للواجهة ({lengthOverflowCount} فيض نصي و{lengthWarnOnlyIds.Count} تحذير طول)."
                    : lengthOverflowCount > 0
                        ? $"{lengthOverflowCount} سطر يتجاوز الحد المقترح للواجهة (فيض نصي)."
                        : $"{lengthWarnOnlyIds.Count} سطر أطول من الحد المقترح لعناصر الواجهة.");

        QaReadinessLevel level;
        if (tokenErrors.Count > 0 || blockingScanErrors.Count > 0)
        {
            level = QaReadinessLevel.Blocked;
        }
        else if (activeUnits.Count == 0
            || glossaryViolations.Count > 0
            || untranslatedUnits.Count > 0
            || lengthWarnings.Count > 0
            || draftUnits > 0)
        {
            level = QaReadinessLevel.WarningsOnly;
        }
        else
        {
            level = QaReadinessLevel.Ready;
        }

        return new PreBuildQaReport(
            Level: level,
            TotalActiveUnits: activeUnits.Count,
            ApprovedUnits: approvedUnits,
            DraftUnits: draftUnits,
            TokenErrors: tokenErrors,
            GlossaryViolations: glossaryViolations,
            UntranslatedUnits: untranslatedUnits,
            LengthWarnings: lengthWarnings,
            LengthOverflowCount: lengthOverflowCount,
            GlossaryAudit: glossaryAudit,
            BlockingScanErrors: blockingScanErrors);
    }

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        string path = (diagnostic.Path ?? "").Trim();
        string message = (diagnostic.Message ?? "").Trim();
        if (path.Length == 0) return message;
        if (message.Length == 0) return path;
        return $"{path}: {message}";
    }
}
