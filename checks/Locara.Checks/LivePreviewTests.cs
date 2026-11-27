using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class LivePreviewTests
{
    private const char Lrm = '\u200E';
    private const char Lro = '\u202D';
    private const char Pdf = '\u202C';

    [Fact]
    public void SupportedBoxWidthsMatchExpectedPresets()
    {
        Assert.Equal([30, 45, 60], ArabicPreviewFormatter.SupportedBoxWidths);
    }

    [Fact]
    public void FormatWrapsArabicParagraphAcrossSupportedBoxWidths()
    {
        const string sample = "مرحباً بك في محطة التحكم الرئيسية، يرجى مراجعة أنظمة الطاقة والملاحة قبل بدء الرحلة الطويلة عبر الفضاء.";

        var narrow = ArabicPreviewFormatter.Format(sample, boxWidthChars: 30, mode: ArabicPreviewMode.PreShaped);
        var medium = ArabicPreviewFormatter.Format(sample, boxWidthChars: 45, mode: ArabicPreviewMode.PreShaped);
        var wide = ArabicPreviewFormatter.Format(sample, boxWidthChars: 60, mode: ArabicPreviewMode.PreShaped);

        Assert.Equal(30, narrow.BoxWidthChars);
        Assert.Equal(45, medium.BoxWidthChars);
        Assert.Equal(60, wide.BoxWidthChars);

        Assert.True(narrow.HasArabic);
        Assert.True(medium.HasArabic);
        Assert.True(wide.HasArabic);

        Assert.Equal(narrow.Lines.Count, narrow.LineCount);
        Assert.Equal(medium.Lines.Count, medium.LineCount);
        Assert.Equal(wide.Lines.Count, wide.LineCount);

        Assert.True(narrow.LineCount > medium.LineCount);
        Assert.True(medium.LineCount >= wide.LineCount);
        Assert.True(wide.LineCount >= 2);

        Assert.InRange(narrow.MaxVisibleLineLength, 1, 30);
        Assert.InRange(medium.MaxVisibleLineLength, 1, 45);
        Assert.InRange(wide.MaxVisibleLineLength, 1, 60);

        Assert.False(narrow.ExceedsBoxWidth);
        Assert.False(medium.ExceedsBoxWidth);
        Assert.False(wide.ExceedsBoxWidth);
    }

    [Fact]
    public void FormatClampsOutOfRangeBoxWidthsAndDetectsOverflow()
    {
        var clampedLow = ArabicPreviewFormatter.Format("مرحبا", boxWidthChars: 2);
        var clampedHigh = ArabicPreviewFormatter.Format("مرحبا", boxWidthChars: 500);

        Assert.Equal(10, clampedLow.BoxWidthChars);
        Assert.Equal(120, clampedHigh.BoxWidthChars);

        var overflow = ArabicPreviewFormatter.Format("كلمةعربيةطويلةجداًلاتنكسر", boxWidthChars: 10);
        Assert.Equal(10, overflow.BoxWidthChars);
        Assert.True(overflow.MaxVisibleLineLength > 10);
        Assert.True(overflow.ExceedsBoxWidth);
    }

    [Fact]
    public void FormatPreShapedVsLogicalProducesExpectedShapingAndLrmIsolation()
    {
        const string sample = "اضغط على زر التأكيد لحفظ التغييرات والعودة إلى القائمة الرئيسية.";

        var preShaped = ArabicPreviewFormatter.Format(sample, boxWidthChars: 30, mode: ArabicPreviewMode.PreShaped);
        var logical = ArabicPreviewFormatter.Format(sample, boxWidthChars: 30, mode: ArabicPreviewMode.Logical);

        Assert.Equal(logical.LineCount, preShaped.LineCount);
        Assert.Equal(logical.MaxVisibleLineLength, preShaped.MaxVisibleLineLength);

        // Logical mode keeps raw logical Arabic without LRM/LRO/PDF or Presentation Forms-B.
        Assert.Equal(logical.RawEngineOutput, logical.DisplayText);
        Assert.DoesNotContain(Lrm, logical.DisplayText);
        Assert.DoesNotContain(Lro, logical.DisplayText);
        Assert.DoesNotContain(Pdf, logical.DisplayText);
        Assert.False(ContainsPresentationFormB(logical.RawEngineOutput));

        // PreShaped mode outputs Presentation Forms in visual order without control marks in RawEngineOutput.
        Assert.True(ContainsPresentationFormB(preShaped.RawEngineOutput));
        Assert.DoesNotContain(Lrm, preShaped.RawEngineOutput);
        Assert.DoesNotContain(Lro, preShaped.RawEngineOutput);
        Assert.DoesNotContain(Pdf, preShaped.RawEngineOutput);

        // DisplayText isolates Arabic clusters with LRM (no LRO/PDF, no consecutive LRM), and stripping LRM restores RawEngineOutput.
        Assert.Contains(Lrm, preShaped.DisplayText);
        Assert.DoesNotContain(Lro, preShaped.DisplayText);
        Assert.DoesNotContain(Pdf, preShaped.DisplayText);
        Assert.DoesNotContain($"{Lrm}{Lrm}", preShaped.DisplayText, StringComparison.Ordinal);

        string strippedDisplay = preShaped.DisplayText.Replace(Lrm.ToString(), "", StringComparison.Ordinal);
        Assert.Equal(preShaped.RawEngineOutput, strippedDisplay);
    }

    [Fact]
    public void FormatPreShapedIsolatesRtlClustersWithoutSplittingCombiningDiacritics()
    {
        const string sample = "تقدمت كثيراً، تراجع للخلف!";

        var result = ArabicPreviewFormatter.Format(sample, boxWidthChars: 45, mode: ArabicPreviewMode.PreShaped);

        Assert.Equal(1, result.LineCount);
        Assert.StartsWith("!", result.RawEngineOutput, StringComparison.Ordinal);
        Assert.Contains('\uFE70', result.RawEngineOutput);
        Assert.DoesNotContain(Lrm, result.RawEngineOutput);
        Assert.Equal(result.RawEngineOutput, result.DisplayText.Replace(Lrm.ToString(), "", StringComparison.Ordinal));

        // Verify a remaining Unicode combining mark (\u0670 superscript alef) stays attached to its base char without LRM between them.
        var combiningSample = ArabicPreviewFormatter.Format("ه\u0670ذا", boxWidthChars: 45, mode: ArabicPreviewMode.PreShaped);
        int supAlefIndex = combiningSample.DisplayText.IndexOf('\u0670');
        Assert.True(supAlefIndex > 0);
        Assert.NotEqual(Lrm, combiningSample.DisplayText[supAlefIndex - 1]);
        Assert.Equal(Lrm, combiningSample.DisplayText[supAlefIndex + 1]);

        // No two RTL base characters may sit adjacent without LRM between them in DisplayText.
        for (int i = 0; i < result.DisplayText.Length - 1; i++)
        {
            char curr = result.DisplayText[i];
            char next = result.DisplayText[i + 1];
            if (IsArabicRangeChar(curr) && !IsCombiningMarkChar(curr) && IsArabicRangeChar(next) && !IsCombiningMarkChar(next))
                Assert.Fail($"Adjacent RTL base characters without LRM isolation at index {i}: U+{(int)curr:X4}, U+{(int)next:X4}");
        }
    }

    [Fact]
    public void FormatPreservesNonBreakableTagsBbCodeAndPlaceholdersAcrossWrappedLines()
    {
        const string sample =
            "تفقّد <nobr>حالة المفاعل النووي</nobr> واضغط على <link=\"help\">دليل التشغيل</link> أو <a href=\"doc\">صفحة المساعدة</a> ثم [b]اختر الوضع المناسب للمهمة[/b] للاعب {0} بنسبة %s.";

        var result = ArabicPreviewFormatter.Format(sample, boxWidthChars: 30, mode: ArabicPreviewMode.Logical);

        Assert.True(result.LineCount > 1);
        Assert.Contains(result.Lines, line => line.Contains("<nobr>حالة المفاعل النووي</nobr>", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.Contains("<link=\"help\">دليل التشغيل</link>", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.Contains("<a href=\"doc\">صفحة المساعدة</a>", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.Contains("{0}", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.Contains("%s", StringComparison.Ordinal));

        // BBCode [b]...[/b] must remain balanced on every wrapped line.
        foreach (string line in result.Lines)
        {
            int opens = CountOccurrences(line, "[b]");
            int closes = CountOccurrences(line, "[/b]");
            Assert.Equal(opens, closes);
        }

        // PreShaped mode should also preserve placeholders without throwing or dropping tokens.
        var preShaped = ArabicPreviewFormatter.Format(sample, boxWidthChars: 30, mode: ArabicPreviewMode.PreShaped);
        Assert.Contains("{0}", preShaped.RawEngineOutput, StringComparison.Ordinal);
        Assert.Contains("%s", preShaped.RawEngineOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Lrm, preShaped.RawEngineOutput);
        Assert.DoesNotContain(Lro, preShaped.RawEngineOutput);
        Assert.DoesNotContain(Pdf, preShaped.RawEngineOutput);
    }

    [Fact]
    public void FormatHandlesEmptyWhitespacePureEnglishAndExplicitMultiLineDeterministically()
    {
        var nullResult = ArabicPreviewFormatter.Format(null, boxWidthChars: 45);
        var emptyResult = ArabicPreviewFormatter.Format("", boxWidthChars: 45);

        foreach (var empty in new[] { nullResult, emptyResult })
        {
            Assert.Equal("", empty.DisplayText);
            Assert.Equal("", empty.RawEngineOutput);
            Assert.Empty(empty.Lines);
            Assert.Equal(0, empty.LineCount);
            Assert.Equal(0, empty.MaxVisibleLineLength);
            Assert.Equal(45, empty.BoxWidthChars);
            Assert.False(empty.HasArabic);
            Assert.False(empty.ExceedsBoxWidth);
        }

        var whitespace = ArabicPreviewFormatter.Format("   ", boxWidthChars: 30);
        Assert.Equal(1, whitespace.LineCount);
        Assert.Equal(0, whitespace.MaxVisibleLineLength);
        Assert.False(whitespace.HasArabic);
        Assert.False(whitespace.ExceedsBoxWidth);
        Assert.DoesNotContain(Lrm, whitespace.DisplayText);
        Assert.DoesNotContain(Lro, whitespace.DisplayText);

        const string english = "Press the confirm button to save all configuration changes and return to the main menu.";
        var englishWrapped = ArabicPreviewFormatter.Format(english, boxWidthChars: 30, mode: ArabicPreviewMode.PreShaped);
        Assert.False(englishWrapped.HasArabic);
        Assert.True(englishWrapped.LineCount >= 3);
        Assert.InRange(englishWrapped.MaxVisibleLineLength, 1, 30);
        Assert.False(englishWrapped.ExceedsBoxWidth);
        Assert.DoesNotContain(Lrm, englishWrapped.DisplayText);
        Assert.DoesNotContain(Lro, englishWrapped.DisplayText);
        Assert.DoesNotContain(Pdf, englishWrapped.DisplayText);

        const string multiLine = "السطر الأول\r\n\r\nEnglish Line\rالسطر الأخير";
        var multi = ArabicPreviewFormatter.Format(multiLine, boxWidthChars: 45, mode: ArabicPreviewMode.PreShaped);
        Assert.True(multi.HasArabic);
        Assert.Equal(4, multi.LineCount);
        Assert.Equal("", multi.Lines[1]);

        string[] displayLines = multi.DisplayText.Split('\n');
        Assert.Equal(4, displayLines.Length);
        Assert.StartsWith(Lrm.ToString(), displayLines[0], StringComparison.Ordinal);
        Assert.EndsWith(Lrm.ToString(), displayLines[0], StringComparison.Ordinal);
        Assert.Equal("", displayLines[1]);
        Assert.Equal("English Line", displayLines[2]);
        Assert.StartsWith(Lrm.ToString(), displayLines[3], StringComparison.Ordinal);
        Assert.EndsWith(Lrm.ToString(), displayLines[3], StringComparison.Ordinal);
        Assert.DoesNotContain(Lrm, multi.RawEngineOutput);
        Assert.DoesNotContain(Lro, multi.RawEngineOutput);
        Assert.DoesNotContain(Pdf, multi.RawEngineOutput);
    }

    private static bool IsArabicRangeChar(char ch) =>
        ch is (>= '\u0600' and <= '\u06FF')
            or (>= '\u0750' and <= '\u077F')
            or (>= '\u08A0' and <= '\u08FF')
            or (>= '\uFB50' and <= '\uFDFF')
            or (>= '\uFE70' and <= '\uFEFF');

    private static bool IsCombiningMarkChar(char ch) =>
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.EnclosingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark;

    private static bool ContainsPresentationFormB(string text)
    {
        foreach (char ch in text)
        {
            if (ch is >= '\uFE70' and <= '\uFEFF')
                return true;
        }

        return false;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
