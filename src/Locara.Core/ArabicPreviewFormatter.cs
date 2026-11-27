using System.Globalization;
using System.Text;

namespace Locara.Core;

public enum ArabicPreviewMode
{
    PreShaped,
    Logical
}

public sealed record ArabicPreviewResult(
    string DisplayText,
    string RawEngineOutput,
    IReadOnlyList<string> Lines,
    int LineCount,
    int MaxVisibleLineLength,
    int BoxWidthChars,
    bool HasArabic,
    bool ExceedsBoxWidth);

/// <summary>
/// Formats Arabic and mixed-script localization text for live studio previews,
/// supporting both pre-shaped visual rendering (with LRM grapheme-cluster isolation for WPF LTR TextBlocks)
/// and logical wrapped output.
/// </summary>
public static class ArabicPreviewFormatter
{
    private const char LeftToRightMark = '\u200E';

    public static readonly int[] SupportedBoxWidths = [30, 45, 60];

    public static ArabicPreviewResult Format(
        string? text,
        int boxWidthChars = 45,
        ArabicPreviewMode mode = ArabicPreviewMode.PreShaped,
        string engine = "")
    {
        int width = Math.Clamp(boxWidthChars, 10, 120);

        if (string.IsNullOrEmpty(text))
        {
            return new ArabicPreviewResult(
                DisplayText: "",
                RawEngineOutput: "",
                Lines: [],
                LineCount: 0,
                MaxVisibleLineLength: 0,
                BoxWidthChars: width,
                HasArabic: false,
                ExceedsBoxWidth: false);
        }

        string normalizedText = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        bool hasArabic = ArabicTextEngine.HasArabic(normalizedText);
        string[] paragraphs = normalizedText.Split('\n');
        var logicalLines = new List<string>(paragraphs.Length);

        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                logicalLines.Add("");
            }
            else if (StarTruckerDisplay.HasArabic(paragraph))
            {
                logicalLines.AddRange(StarTruckerDisplay.WrapArabicParagraph(paragraph, width));
            }
            else
            {
                logicalLines.AddRange(WrapNonArabicParagraph(paragraph, width));
            }
        }

        int maxVisibleLineLength = 0;
        foreach (string line in logicalLines)
        {
            string clean = UiLengthGuard.CleanVisibleText(line, engine);
            int visibleLength = UiLengthGuard.CountVisibleGlyphChars(clean);
            if (visibleLength > maxVisibleLineLength)
                maxVisibleLineLength = visibleLength;
        }

        bool exceedsBoxWidth = maxVisibleLineLength > width;

        string rawEngineOutput;
        string displayText;
        if (mode == ArabicPreviewMode.Logical)
        {
            rawEngineOutput = string.Join('\n', logicalLines);
            displayText = rawEngineOutput;
        }
        else
        {
            rawEngineOutput = ArabicTextEngine.WrapAndShape(normalizedText, width)
                .Replace("\u200E", "", StringComparison.Ordinal)
                .Replace("\u202D", "", StringComparison.Ordinal)
                .Replace("\u202C", "", StringComparison.Ordinal);
            displayText = IsolateRtlClustersForWpfLtr(rawEngineOutput);
        }

        return new ArabicPreviewResult(
            DisplayText: displayText,
            RawEngineOutput: rawEngineOutput,
            Lines: logicalLines,
            LineCount: logicalLines.Count,
            MaxVisibleLineLength: maxVisibleLineLength,
            BoxWidthChars: width,
            HasArabic: hasArabic,
            ExceedsBoxWidth: exceedsBoxWidth);
    }

    private static List<string> WrapNonArabicParagraph(string paragraph, int maxLineChars)
    {
        if (string.IsNullOrWhiteSpace(paragraph) || paragraph.Length <= maxLineChars)
            return [paragraph];

        string[] words = paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
            return [paragraph];

        var lines = new List<string>();
        var current = new StringBuilder(maxLineChars);

        foreach (string word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if (current.Length + 1 + word.Length <= maxLineChars)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines;
    }

    private static string IsolateRtlClustersForWpfLtr(string text)
    {
        if (string.IsNullOrEmpty(text) || !ContainsArabicOrPresentationForm(text))
            return text;

        var sb = new StringBuilder(text.Length * 2);
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (IsArabicOrPresentationForm(ch))
            {
                if (sb.Length == 0 || sb[^1] != LeftToRightMark)
                    sb.Append(LeftToRightMark);

                sb.Append(ch);
                i++;

                while (i < text.Length && IsCombiningMark(text[i]))
                {
                    sb.Append(text[i]);
                    i++;
                }

                sb.Append(LeftToRightMark);
            }
            else
            {
                sb.Append(ch);
                i++;
            }
        }

        return sb.ToString();
    }

    private static bool IsCombiningMark(char ch) =>
        CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.SpacingCombiningMark;

    private static bool ContainsArabicOrPresentationForm(string text)
    {
        foreach (char ch in text)
        {
            if (IsArabicOrPresentationForm(ch))
                return true;
        }

        return false;
    }

    private static bool IsArabicOrPresentationForm(char ch) =>
        ch is (>= '\u0600' and <= '\u06FF')
            or (>= '\u0750' and <= '\u077F')
            or (>= '\u08A0' and <= '\u08FF')
            or (>= '\uFB50' and <= '\uFDFF')
            or (>= '\uFE70' and <= '\uFEFF');
}
