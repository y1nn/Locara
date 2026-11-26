using System.Text;

namespace Locara.Core;

public sealed record GlossaryTermViolation(string TermSource, string ExpectedArabic, bool IsExactSourceMatch);

public sealed record GlossaryUnitViolation(Unit Unit, IReadOnlyList<GlossaryTermViolation> Violations)
{
    public string Summary => string.Join(" · ",
        Violations.Select(v => $"«{v.TermSource}» ← المطلوب «{v.ExpectedArabic}»"));
}

public sealed record GlossaryAuditReport(
    int ScannedUnits,
    int ManualTermCount,
    IReadOnlyList<GlossaryUnitViolation> Items)
{
    public int ViolatingUnitCount => Items.Count;
    public int TotalTermViolations => Items.Sum(item => item.Violations.Count);
    public int ExactFixableCount => Items.Count(item => item.Violations.Count == 1 && item.Violations[0].IsExactSourceMatch);
}

/// <summary>
/// Audits translated/approved units against the project's authoritative manual glossary
/// using token-masked source matching and Arabic morphological normalization.
/// </summary>
public static class GlossaryConsistencyChecker
{
    public static IReadOnlyList<GlossaryTermViolation> CheckUnit(
        string? source,
        string? target,
        IReadOnlyDictionary<string, string>? manualGlossary,
        string engine = "")
    {
        if (manualGlossary == null || manualGlossary.Count == 0
            || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return [];

        string maskedSource = Tokens.Mask(source, engine).Text;
        string trimmedSource = source.Trim();
        string trimmedTarget = target.Trim();
        string normalizedTarget = NormalizeArabic(trimmedTarget);

        List<GlossaryTermViolation>? violations = null;

        foreach (var (rawKey, rawVal) in manualGlossary.OrderByDescending(kv => kv.Key.Length))
        {
            string termSource = (rawKey ?? "").Trim();
            string expectedArabic = (rawVal ?? "").Trim();
            if (termSource.Length == 0 || expectedArabic.Length == 0) continue;

            if (!LiveGlossary.ContainsWholeTerm(maskedSource, termSource))
                continue;

            if (ContainsExpectedArabic(trimmedTarget, normalizedTarget, expectedArabic))
                continue;

            bool isExact = string.Equals(trimmedSource, termSource, StringComparison.OrdinalIgnoreCase);
            violations ??= [];
            violations.Add(new GlossaryTermViolation(termSource, expectedArabic, isExact));
        }

        return violations ?? (IReadOnlyList<GlossaryTermViolation>)[];
    }

    public static GlossaryAuditReport AuditProject(ProjectStore store, IEnumerable<Unit>? scope = null)
    {
        var manualGlossary = store.Glossary();
        if (manualGlossary.Count == 0)
            return new(0, 0, []);

        string engine = new ProjectSettings(store).ScannedEngine;
        var units = scope ?? store.Units(limit: -1);

        int scanned = 0;
        var items = new List<GlossaryUnitViolation>();
        foreach (var unit in units)
        {
            if (unit.Status is not ("translated" or "imported" or "approved") || string.IsNullOrWhiteSpace(unit.Target))
                continue;

            scanned++;
            var violations = CheckUnit(unit.Source, unit.Target, manualGlossary, engine);
            if (violations.Count > 0)
                items.Add(new GlossaryUnitViolation(unit, violations));
        }

        return new(scanned, manualGlossary.Count, items);
    }

    public static int ApplyExactTermFixes(
        ProjectStore store,
        IReadOnlyList<GlossaryUnitViolation> violations,
        bool approve = false)
    {
        if (violations.Count == 0) return 0;

        string engine = new ProjectSettings(store).ScannedEngine;
        var edits = new List<(Unit Unit, bool Approve)>();

        foreach (var item in violations)
        {
            if (item.Violations.Count != 1 || !item.Violations[0].IsExactSourceMatch)
                continue;

            string expected = item.Violations[0].ExpectedArabic;
            var current = store.UnitsById(item.Unit.Id);
            if (current == null) continue;
            if (!string.Equals(current.Source.Trim(), item.Violations[0].TermSource, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Tokens.Validate(current.Source, expected, engine).Length != 0)
                continue;

            bool keepApproved = approve || current.Status == "approved";
            current.Target = expected;
            edits.Add((current, keepApproved));
        }

        if (edits.Count == 0) return 0;
        store.SavePage(edits);
        return edits.Count;
    }

    internal static bool ContainsExpectedArabic(string rawTarget, string normalizedTarget, string expectedArabic)
    {
        if (SmartQaEvaluator.ContainsArabicTermOrStem(rawTarget, expectedArabic))
            return true;

        string normalizedExpected = NormalizeArabic(expectedArabic);
        if (normalizedExpected.Length == 0)
            return true;

        return SmartQaEvaluator.ContainsArabicTermOrStem(normalizedTarget, normalizedExpected);
    }

    internal static string NormalizeArabic(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // Strip Arabic combining diacritics & tatweel
            if ((c is >= '\u064B' and <= '\u065F') || c == '\u0670' || c == '\u0640')
                continue;

            char mapped = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => c
            };
            sb.Append(mapped);
        }
        return sb.ToString();
    }
}
