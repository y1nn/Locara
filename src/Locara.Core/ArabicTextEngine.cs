namespace Locara.Core;

/// <summary>
/// Engine-agnostic facade for Arabic script detection, ICU contextual glyph shaping,
/// BiDi ordering, and tag-safe dynamic-programming line wrapping (preserving HTML/TMP and BBCode tags).
/// </summary>
public static class ArabicTextEngine
{
    public static bool HasArabic(string? text) =>
        !string.IsNullOrEmpty(text) && StarTruckerDisplay.HasArabic(text);

    public static string Shape(string text) =>
        StarTruckerDisplay.Shape(text);

    public static string WrapAndShape(string text, int maxVisibleCharsPerLine = StarTruckerDisplay.AutoWrap) =>
        StarTruckerDisplay.Shape(text, maxVisibleCharsPerLine);

    public static string ValidateTokens(string source, string target, string engine = "") =>
        Tokens.Validate(source, target, engine);
}
