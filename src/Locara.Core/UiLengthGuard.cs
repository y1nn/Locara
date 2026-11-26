using System.Text;

namespace Locara.Core;

public enum UiLengthSeverity
{
    NotApplicable,
    Safe,
    Warning,
    Overflow
}

public sealed record UiLengthEvaluation(
    UiLengthSeverity Severity,
    int VisibleSourceLength,
    int VisibleTargetLength,
    int RecommendedMaxLength,
    double ExpansionRatio,
    double FillPercent,
    string Summary);

/// <summary>
/// Evaluates visible character expansion for short UI labels, buttons, and headings
/// after stripping protected markup/placeholders and zero-width Arabic diacritics.
/// </summary>
public static class UiLengthGuard
{
    private static readonly string[] UiHints =
    [
        "menu", "btn", "button", "ui", "hud", "title", "header", "heading",
        "tab", "label", "option", "setting", "hotkey", "prompt", "tooltip",
        "stat", "badge", "nav", "action", "popup", "dialog_title"
    ];

    public static UiLengthEvaluation Evaluate(
        string? source,
        string? target,
        string? context = null,
        string? locator = null,
        string engine = "")
    {
        string rawSource = source ?? "";
        string rawTarget = target ?? "";

        string cleanSource = CleanVisibleText(rawSource, engine);
        string cleanTarget = CleanVisibleText(rawTarget, engine);

        int visSource = CountVisibleGlyphChars(cleanSource);
        int visTarget = CountVisibleGlyphChars(cleanTarget);

        if (visSource == 0)
            return new(UiLengthSeverity.NotApplicable, 0, visTarget, 0, 0, 0, "");

        bool isMultiLine = rawSource.Contains('\n') || rawSource.Contains('\r');
        int wordCount = cleanSource.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        bool hasUiHint = HasUiHint(context) || HasUiHint(locator);

        bool looksLikeShortUi = !isMultiLine && (
            (visSource <= 40 && wordCount <= 5 && !cleanSource.Contains(". ", StringComparison.Ordinal))
            || (hasUiHint && visSource <= 55 && wordCount <= 8));

        double ratio = (double)visTarget / visSource;
        if (!looksLikeShortUi)
            return new(UiLengthSeverity.NotApplicable, visSource, visTarget, 0, ratio, 0, "");

        int safeMax;
        int warnMax;
        if (visSource <= 6)
        {
            safeMax = Math.Max(visSource + 4, (int)Math.Ceiling(visSource * 1.50));
            warnMax = Math.Max(visSource + 8, (int)Math.Ceiling(visSource * 2.00));
        }
        else if (visSource <= 20)
        {
            safeMax = Math.Max(visSource + 5, (int)Math.Ceiling(visSource * 1.40));
            warnMax = Math.Max(safeMax + 4, (int)Math.Ceiling(visSource * 1.75));
        }
        else
        {
            safeMax = Math.Max(visSource + 6, (int)Math.Ceiling(visSource * 1.35));
            warnMax = Math.Max(safeMax + 6, (int)Math.Ceiling(visSource * 1.65));
        }

        if (visTarget == 0)
            return new(UiLengthSeverity.Safe, visSource, 0, safeMax, 0, 0, "");

        double fillPercent = Math.Clamp(visTarget * 100.0 / Math.Max(1, safeMax), 0, 100);
        if (visTarget <= safeMax)
            return new(UiLengthSeverity.Safe, visSource, visTarget, safeMax, ratio, fillPercent,
                $"طول مناسب للواجهة ({visTarget}/{safeMax} حرف)");

        if (visTarget <= warnMax)
            return new(UiLengthSeverity.Warning, visSource, visTarget, safeMax, ratio, fillPercent,
                $"أطول من الحد المقترح (≤ {safeMax} حرف) — يُفضّل الاختصار للأزرار الضيقة");

        return new(UiLengthSeverity.Overflow, visSource, visTarget, safeMax, ratio, fillPercent,
            $"تنبيه فيض نصي ({visTarget} حرفاً مقابل حد مقترح ≤ {safeMax}) — قد يُقص النص داخل الواجهة");
    }

    internal static string CleanVisibleText(string text, string engine = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string stripped = Tokens.ReplaceTokens(text, _ => " ", engine);
        var sb = new StringBuilder(stripped.Length);
        bool prevSpace = true;
        foreach (char ch in stripped)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        return sb.ToString();
    }

    internal static int CountVisibleGlyphChars(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (IsZeroWidthMark(c)) continue;
            count++;
        }
        return count;
    }

    private static bool IsZeroWidthMark(char c) =>
        (c is >= '\u064B' and <= '\u065F')
        || c == '\u0670'
        || c == '\u0640'
        || c == '\u061C'
        || (c is >= '\u200B' and <= '\u200F')
        || c == '\uFEFF';

    private static bool HasUiHint(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return false;
        foreach (string hint in UiHints)
        {
            if (metadata.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
