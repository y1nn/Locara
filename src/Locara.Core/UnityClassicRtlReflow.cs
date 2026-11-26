using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Locara.Core
{
    public enum ClassicArabicTextKind
    {
        None = 0,
        PreShaped = 1,
        Logical = 2
    }

    /// <summary>
    /// Pure managed (netstandard2.0 + net10.0 compatible, zero native P/Invoke) engine for fixing
    /// bottom-to-top Arabic line wrapping in classic Unity UI frameworks (UnityEngine.UI.Text, TextMesh, NGUI).
    /// Supports both Pre-Shaped Arabic (paragraph-preserving line reversal with rich-text tag repair)
    /// and Logical Arabic (contextual Presentation Forms-B shaping + per-line tag/LTR-safe BiDi reversal).
    /// </summary>
    public static class UnityClassicRtlReflow
    {
        private static readonly Regex HtmlRichTagRegex = new Regex(
            @"<(?<close>/)?(?<name>color|b|i|size|material|quad)(?:\s*=[^<>]*)?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NguiColorOrStyleTagRegex = new Regex(
            @"\[(?:(?<hex>[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})|(?<reset>-)|(?<close>/)?(?<style>b|i|u|s|sub|sup))\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Arabic contextual shaping forms: [isolated, final, initial, medial], dualJoin (connects to following letter)
        private struct GlyphForms
        {
            public readonly char Isolated;
            public readonly char Final;
            public readonly char Initial;
            public readonly char Medial;
            public readonly bool DualJoin;

            public GlyphForms(int iso, int fin, int ini, int med, bool dual)
            {
                Isolated = (char)iso;
                Final = (char)fin;
                Initial = (char)ini;
                Medial = (char)med;
                DualJoin = dual;
            }
        }

        private static readonly Dictionary<char, GlyphForms> ArabicTable = BuildArabicTable();

        private static Dictionary<char, GlyphForms> BuildArabicTable()
        {
            var d = new Dictionary<char, GlyphForms>(44);
            // Right-joining only (DualJoin = false)
            d['\u0621'] = new GlyphForms(0xFE80, 0xFE80, 0xFE80, 0xFE80, false); // ء
            d['\u0622'] = new GlyphForms(0xFE81, 0xFE82, 0xFE81, 0xFE82, false); // آ
            d['\u0623'] = new GlyphForms(0xFE83, 0xFE84, 0xFE83, 0xFE84, false); // أ
            d['\u0624'] = new GlyphForms(0xFE85, 0xFE86, 0xFE85, 0xFE86, false); // ؤ
            d['\u0625'] = new GlyphForms(0xFE87, 0xFE88, 0xFE87, 0xFE88, false); // إ
            d['\u0627'] = new GlyphForms(0xFE8D, 0xFE8E, 0xFE8D, 0xFE8E, false); // ا
            d['\u0671'] = new GlyphForms(0xFB50, 0xFB51, 0xFB50, 0xFB51, false); // ٱ
            d['\u0629'] = new GlyphForms(0xFE93, 0xFE94, 0xFE93, 0xFE94, false); // ة
            d['\u062F'] = new GlyphForms(0xFEA9, 0xFEAA, 0xFEA9, 0xFEAA, false); // د
            d['\u0630'] = new GlyphForms(0xFEAB, 0xFEAC, 0xFEAB, 0xFEAC, false); // ذ
            d['\u0631'] = new GlyphForms(0xFEAD, 0xFEAE, 0xFEAD, 0xFEAE, false); // ر
            d['\u0632'] = new GlyphForms(0xFEAF, 0xFEB0, 0xFEAF, 0xFEB0, false); // ز
            d['\u0698'] = new GlyphForms(0xFB8A, 0xFB8B, 0xFB8A, 0xFB8B, false); // ژ
            d['\u0648'] = new GlyphForms(0xFEED, 0xFEEE, 0xFEED, 0xFEEE, false); // و
            d['\u0649'] = new GlyphForms(0xFEEF, 0xFEF0, 0xFEEF, 0xFEF0, false); // ى

            // Dual-joining (DualJoin = true)
            d['\u0626'] = new GlyphForms(0xFE89, 0xFE8A, 0xFE8B, 0xFE8C, true); // ئ
            d['\u0628'] = new GlyphForms(0xFE8F, 0xFE90, 0xFE91, 0xFE92, true); // ب
            d['\u067E'] = new GlyphForms(0xFB56, 0xFB57, 0xFB58, 0xFB59, true); // پ
            d['\u062A'] = new GlyphForms(0xFE95, 0xFE96, 0xFE97, 0xFE98, true); // ت
            d['\u062B'] = new GlyphForms(0xFE99, 0xFE9A, 0xFE9B, 0xFE9C, true); // ث
            d['\u062C'] = new GlyphForms(0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0, true); // ج
            d['\u0686'] = new GlyphForms(0xFB7A, 0xFB7B, 0xFB7C, 0xFB7D, true); // چ
            d['\u062D'] = new GlyphForms(0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4, true); // ح
            d['\u062E'] = new GlyphForms(0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8, true); // خ
            d['\u0633'] = new GlyphForms(0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4, true); // س
            d['\u0634'] = new GlyphForms(0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8, true); // ش
            d['\u0635'] = new GlyphForms(0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC, true); // ص
            d['\u0636'] = new GlyphForms(0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0, true); // ض
            d['\u0637'] = new GlyphForms(0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4, true); // ط
            d['\u0638'] = new GlyphForms(0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8, true); // ظ
            d['\u0639'] = new GlyphForms(0xFEC9, 0xFECA, 0xFECB, 0xFECC, true); // ع
            d['\u063A'] = new GlyphForms(0xFECD, 0xFECE, 0xFECF, 0xFED0, true); // غ
            d['\u0640'] = new GlyphForms(0x0640, 0x0640, 0x0640, 0x0640, true); // ـ tatweel
            d['\u0641'] = new GlyphForms(0xFED1, 0xFED2, 0xFED3, 0xFED4, true); // ف
            d['\u0642'] = new GlyphForms(0xFED5, 0xFED6, 0xFED7, 0xFED8, true); // ق
            d['\u0643'] = new GlyphForms(0xFED9, 0xFEDA, 0xFEDB, 0xFEDC, true); // ك
            d['\u06A9'] = new GlyphForms(0xFB8E, 0xFB8F, 0xFB90, 0xFB91, true); // ک
            d['\u06AF'] = new GlyphForms(0xFB92, 0xFB93, 0xFB94, 0xFB95, true); // گ
            d['\u0644'] = new GlyphForms(0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0, true); // ل
            d['\u0645'] = new GlyphForms(0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4, true); // م
            d['\u0646'] = new GlyphForms(0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8, true); // ن
            d['\u0647'] = new GlyphForms(0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC, true); // ه
            d['\u064A'] = new GlyphForms(0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4, true); // ي
            d['\u06CC'] = new GlyphForms(0xFBFC, 0xFBFD, 0xFBFE, 0xFBFF, true); // ی
            return d;
        }

        public static bool ContainsAnyArabic(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text!.Length; i++)
            {
                char c = text[i];
                if ((c >= '\u0600' && c <= '\u06FF')
                    || (c >= '\u0750' && c <= '\u077F')
                    || (c >= '\uFB50' && c <= '\uFDFF')
                    || (c >= '\uFE70' && c <= '\uFEFF'))
                    return true;
            }
            return false;
        }

        public static ClassicArabicTextKind DetectArabicKind(string? text)
        {
            if (string.IsNullOrEmpty(text)) return ClassicArabicTextKind.None;
            bool hasLogical = false;
            for (int i = 0; i < text!.Length; i++)
            {
                char c = text[i];
                if ((c >= '\uFE70' && c <= '\uFEFF') || (c >= '\uFB50' && c <= '\uFDFF'))
                    return ClassicArabicTextKind.PreShaped;
                if ((c >= '\u0621' && c <= '\u064A') || (c >= '\u0671' && c <= '\u06D3'))
                    hasLogical = true;
            }
            return hasLogical ? ClassicArabicTextKind.Logical : ClassicArabicTextKind.None;
        }

        /// <summary>
        /// Reflows a Pre-Shaped Arabic string after Unity's TextGenerator splits it into visual lines.
        /// Reverses wrapped sub-lines within each explicit '\n' paragraph so sentences read top-to-bottom
        /// while repairing split HTML/NGUI rich-text tags across line boundaries.
        /// </summary>
        public static string ReflowPreShapedLines(string originalText, IReadOnlyList<int> lineStartIndices)
        {
            if (string.IsNullOrEmpty(originalText) || lineStartIndices == null || lineStartIndices.Count <= 1)
                return originalText;

            var paragraphs = SliceParagraphSubLines(originalText, lineStartIndices);
            bool anyMultiLineParagraph = false;
            for (int p = 0; p < paragraphs.Count; p++)
            {
                if (paragraphs[p].Count > 1)
                {
                    anyMultiLineParagraph = true;
                    break;
                }
            }
            if (!anyMultiLineParagraph)
                return originalText;

            return RebuildFlippedParagraphs(paragraphs);
        }

        /// <summary>
        /// Reflows a Pre-Shaped Arabic string after NGUI's NGUIText.WrapText inserts '\n' at wrap points.
        /// </summary>
        public static string ReflowPreShapedWrappedText(string originalText, string wrappedText)
        {
            if (string.IsNullOrEmpty(originalText) || string.IsNullOrEmpty(wrappedText))
                return wrappedText;
            if (!wrappedText.Contains("\n"))
                return wrappedText;

            string normOrig = NormalizeLf(originalText);
            string normWrap = NormalizeLf(wrappedText);
            if (normOrig == normWrap)
                return wrappedText;

            string[] origParas = normOrig.Split('\n');
            string[] wrapLines = normWrap.Split('\n');
            if (wrapLines.Length <= origParas.Length)
                return wrappedText;

            // Map wrapped lines sequentially back to their originating explicit paragraphs
            var paragraphs = MapWrappedLinesToParagraphs(origParas, wrapLines);
            bool anyMulti = false;
            for (int i = 0; i < paragraphs.Count; i++)
            {
                if (paragraphs[i].Count > 1) { anyMulti = true; break; }
            }
            if (!anyMulti) return wrappedText;

            return RebuildFlippedParagraphs(paragraphs);
        }

        /// <summary>
        /// Shapes logical Arabic glyphs into contextual Presentation Forms-B while keeping words
        /// in logical order so Unity/NGUI measures exact glyph widths before per-line BiDi reversal.
        /// </summary>
        public static string ShapeLogicalForMeasurement(string logicalText)
        {
            if (string.IsNullOrEmpty(logicalText) || !ContainsAnyArabic(logicalText))
                return logicalText;
            return ShapeContextualOnly(logicalText);
        }

        /// <summary>
        /// Given logical (or measurement-shaped logical) text and Unity's TextGenerator line start indices,
        /// shapes and visually reverses each wrapped line independently while keeping top-to-bottom line order.
        /// </summary>
        public static string ReflowLogicalLines(string logicalOrShapedLogicalText, IReadOnlyList<int> lineStartIndices)
        {
            if (string.IsNullOrEmpty(logicalOrShapedLogicalText))
                return logicalOrShapedLogicalText;

            if (lineStartIndices == null || lineStartIndices.Count <= 1)
                return ShapeLogicalTextPerExplicitLine(logicalOrShapedLogicalText);

            var paragraphs = SliceParagraphSubLines(logicalOrShapedLogicalText, lineStartIndices);
            var outParagraphs = new List<string>(paragraphs.Count);
            for (int p = 0; p < paragraphs.Count; p++)
            {
                var subLines = MakeLinesTagSelfContained(paragraphs[p]);
                for (int i = 0; i < subLines.Count; i++)
                {
                    subLines[i] = ShapeAndVisualReverseSingleLine(subLines[i]);
                }
                outParagraphs.Add(string.Join("\n", subLines.ToArray()));
            }
            return string.Join("\n", outParagraphs.ToArray());
        }

        /// <summary>
        /// Shapes and visually reverses each line of a '\n'-wrapped logical string (e.g., from NGUIText.WrapText
        /// or single-pass text), keeping lines in top-to-bottom order.
        /// </summary>
        public static string ShapeLogicalTextPerExplicitLine(string wrappedLogicalText)
        {
            if (string.IsNullOrEmpty(wrappedLogicalText) || !ContainsAnyArabic(wrappedLogicalText))
                return wrappedLogicalText;

            string norm = NormalizeLf(wrappedLogicalText);
            if (!norm.Contains("\n"))
                return ShapeAndVisualReverseSingleLine(norm);

            var rawLines = new List<string>(norm.Split('\n'));
            var balanced = MakeLinesTagSelfContained(rawLines);
            for (int i = 0; i < balanced.Count; i++)
                balanced[i] = ShapeAndVisualReverseSingleLine(balanced[i]);
            return string.Join("\n", balanced.ToArray());
        }

        public static string ShapeAndVisualReverseSingleLine(string line)
        {
            if (string.IsNullOrEmpty(line) || !ContainsAnyArabic(line))
                return line;

            string shaped = DetectArabicKind(line) == ClassicArabicTextKind.PreShaped
                ? line
                : ShapeContextualOnly(line);

            return VisualReverseLinePreservingTagsAndLtr(shaped);
        }

        // ==================== PARAGRAPH SLICING & TAG BALANCING ====================

        private static List<List<string>> SliceParagraphSubLines(string text, IReadOnlyList<int> lineStarts)
        {
            var paragraphs = new List<List<string>>();
            var currentPara = new List<string>();
            paragraphs.Add(currentPara);

            int count = lineStarts.Count;
            for (int i = 0; i < count; i++)
            {
                int start = Math.Max(0, Math.Min(lineStarts[i], text.Length));
                int end = (i + 1 < count)
                    ? Math.Max(start, Math.Min(lineStarts[i + 1], text.Length))
                    : text.Length;

                // Check if the previous slice ended with an explicit '\n' in originalText
                if (i > 0 && start > 0 && text[start - 1] == '\n')
                {
                    currentPara = new List<string>();
                    paragraphs.Add(currentPara);
                }

                // Trim trailing \r\n or trailing wrap space on soft breaks
                int trimEnd = end;
                bool endsWithNewline = false;
                while (trimEnd > start && (text[trimEnd - 1] == '\n' || text[trimEnd - 1] == '\r'))
                {
                    if (text[trimEnd - 1] == '\n') endsWithNewline = true;
                    trimEnd--;
                }
                if (!endsWithNewline && i + 1 < count && trimEnd > start && text[trimEnd - 1] == ' ')
                {
                    trimEnd--;
                }

                string slice = text.Substring(start, trimEnd - start);
                currentPara.Add(slice);
            }

            return paragraphs;
        }

        private static List<List<string>> MapWrappedLinesToParagraphs(string[] origParas, string[] wrapLines)
        {
            var result = new List<List<string>>(origParas.Length);
            int wIdx = 0;

            for (int p = 0; p < origParas.Length; p++)
            {
                var sub = new List<string>();
                result.Add(sub);
                string targetCompact = StripWhitespace(origParas[p]);

                if (targetCompact.Length == 0)
                {
                    if (wIdx < wrapLines.Length && StripWhitespace(wrapLines[wIdx]).Length == 0)
                    {
                        sub.Add(wrapLines[wIdx++]);
                    }
                    else
                    {
                        sub.Add("");
                    }
                    continue;
                }

                int consumed = 0;
                while (wIdx < wrapLines.Length)
                {
                    string line = wrapLines[wIdx++];
                    sub.Add(line);
                    consumed += StripWhitespace(line).Length;
                    if (consumed >= targetCompact.Length || p == origParas.Length - 1 && wIdx >= wrapLines.Length)
                        break;
                }
            }

            while (wIdx < wrapLines.Length)
            {
                result[result.Count - 1].Add(wrapLines[wIdx++]);
            }

            return result;
        }

        private static string RebuildFlippedParagraphs(List<List<string>> paragraphs)
        {
            var rebuiltParas = new List<string>(paragraphs.Count);
            for (int p = 0; p < paragraphs.Count; p++)
            {
                var sub = paragraphs[p];
                if (sub.Count <= 1)
                {
                    rebuiltParas.Add(sub.Count == 1 ? sub[0] : "");
                    continue;
                }

                var balanced = MakeLinesTagSelfContained(sub);
                balanced.Reverse();
                rebuiltParas.Add(string.Join("\n", balanced.ToArray()));
            }
            return string.Join("\n", rebuiltParas.ToArray());
        }

        private sealed class ActiveTag
        {
            public readonly string Name;
            public readonly string OpenRaw;
            public readonly string CloseRaw;
            public readonly bool IsNguiColor;

            public ActiveTag(string name, string openRaw, string closeRaw, bool isNguiColor = false)
            {
                Name = name;
                OpenRaw = openRaw;
                CloseRaw = closeRaw;
                IsNguiColor = isNguiColor;
            }
        }

        internal static List<string> MakeLinesTagSelfContained(IReadOnlyList<string> lines)
        {
            var result = new List<string>(lines.Count);
            var openHtml = new List<ActiveTag>();
            var openNgui = new List<ActiveTag>();

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i] ?? "";
                var prefix = new StringBuilder();
                for (int t = 0; t < openHtml.Count; t++) prefix.Append(openHtml[t].OpenRaw);
                for (int t = 0; t < openNgui.Count; t++) prefix.Append(openNgui[t].OpenRaw);

                UpdateActiveHtmlTags(line, openHtml);
                UpdateActiveNguiTags(line, openNgui);

                var suffix = new StringBuilder();
                for (int t = openNgui.Count - 1; t >= 0; t--) suffix.Append(openNgui[t].CloseRaw);
                for (int t = openHtml.Count - 1; t >= 0; t--) suffix.Append(openHtml[t].CloseRaw);

                if (prefix.Length == 0 && suffix.Length == 0)
                    result.Add(line);
                else
                    result.Add(prefix.ToString() + line + suffix.ToString());
            }

            return result;
        }

        private static void UpdateActiveHtmlTags(string line, List<ActiveTag> stack)
        {
            var matches = HtmlRichTagRegex.Matches(line);
            for (int m = 0; m < matches.Count; m++)
            {
                Match match = matches[m];
                string name = match.Groups["name"].Value.ToLowerInvariant();
                if (name == "quad") continue;
                if (match.Groups["close"].Success)
                {
                    for (int k = stack.Count - 1; k >= 0; k--)
                    {
                        if (stack[k].Name == name)
                        {
                            stack.RemoveAt(k);
                            break;
                        }
                    }
                }
                else
                {
                    stack.Add(new ActiveTag(name, match.Value, "</" + name + ">"));
                }
            }
        }

        private static void UpdateActiveNguiTags(string line, List<ActiveTag> stack)
        {
            var matches = NguiColorOrStyleTagRegex.Matches(line);
            for (int m = 0; m < matches.Count; m++)
            {
                Match match = matches[m];
                if (match.Groups["hex"].Success)
                {
                    stack.Add(new ActiveTag("color", match.Value, "[-]", isNguiColor: true));
                }
                else if (match.Groups["reset"].Success)
                {
                    for (int k = stack.Count - 1; k >= 0; k--)
                    {
                        if (stack[k].IsNguiColor)
                        {
                            stack.RemoveAt(k);
                            break;
                        }
                    }
                }
                else if (match.Groups["style"].Success)
                {
                    string style = match.Groups["style"].Value.ToLowerInvariant();
                    if (match.Groups["close"].Success)
                    {
                        for (int k = stack.Count - 1; k >= 0; k--)
                        {
                            if (!stack[k].IsNguiColor && stack[k].Name == style)
                            {
                                stack.RemoveAt(k);
                                break;
                            }
                        }
                    }
                    else
                    {
                        stack.Add(new ActiveTag(style, match.Value, "[/" + style + "]"));
                    }
                }
            }
        }

        // ==================== PURE-C# CONTEXTUAL SHAPING & VISUAL BIDI ====================

        private static bool IsArabicDiacritic(char c) =>
            (c >= '\u064B' && c <= '\u065F') || c == '\u0670';

        private static string ShapeContextualOnly(string text)
        {
            var sb = new StringBuilder(text.Length);
            int n = text.Length;
            int i = 0;

            while (i < n)
            {
                // Skip markup tags verbatim so tag names/hex codes are untouched
                if (TryMatchMarkupTagAt(text, i, out int tagLen))
                {
                    sb.Append(text, i, tagLen);
                    i += tagLen;
                    continue;
                }

                char c = text[i];
                if (!ArabicTable.TryGetValue(c, out GlyphForms forms))
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                bool prevJoins = PrevLetterDualJoins(text, i);

                // Check Lam-Alef ligature (ل followed optionally by harakat, then آ / أ / إ / ا)
                if (c == '\u0644')
                {
                    int nextLetterIdx = NextNonDiacriticIndex(text, i + 1);
                    if (nextLetterIdx < n && TryGetLamAlefLigature(text[nextLetterIdx], prevJoins, out char ligature))
                    {
                        sb.Append(ligature);
                        // Preserve any intermediate harakat after the ligature
                        for (int d = i + 1; d < nextLetterIdx; d++)
                            sb.Append(text[d]);
                        i = nextLetterIdx + 1;
                        continue;
                    }
                }

                bool nextJoins = forms.DualJoin && NextLetterAcceptsRightJoin(text, i + 1);
                char shaped = (prevJoins, nextJoins) switch
                {
                    (false, false) => forms.Isolated,
                    (true, false) => forms.Final,
                    (false, true) => forms.Initial,
                    (true, true) => forms.Medial
                };
                sb.Append(shaped);
                i++;
            }

            return sb.ToString();
        }

        private static bool PrevLetterDualJoins(string text, int currentIndex)
        {
            int p = currentIndex - 1;
            while (p >= 0 && IsArabicDiacritic(text[p]))
                p--;
            if (p < 0) return false;
            return ArabicTable.TryGetValue(text[p], out GlyphForms prev) && prev.DualJoin;
        }

        private static int NextNonDiacriticIndex(string text, int start)
        {
            int idx = start;
            while (idx < text.Length && IsArabicDiacritic(text[idx]))
                idx++;
            return idx;
        }

        private static bool NextLetterAcceptsRightJoin(string text, int start)
        {
            int idx = NextNonDiacriticIndex(text, start);
            if (idx >= text.Length) return false;
            return ArabicTable.ContainsKey(text[idx]);
        }

        private static bool TryGetLamAlefLigature(char alefCandidate, bool prevJoins, out char ligature)
        {
            switch (alefCandidate)
            {
                case '\u0622': ligature = prevJoins ? '\uFEF6' : '\uFEF5'; return true; // لآ
                case '\u0623': ligature = prevJoins ? '\uFEF8' : '\uFEF7'; return true; // لأ
                case '\u0625': ligature = prevJoins ? '\uFEFA' : '\uFEF9'; return true; // لإ
                case '\u0627': ligature = prevJoins ? '\uFEFC' : '\uFEFB'; return true; // لا
                default: ligature = '\0'; return false;
            }
        }

        private enum VisualTokenKind { RtlChar, LtrRun, OpenTag, CloseTag }

        private struct VisualToken
        {
            public readonly VisualTokenKind Kind;
            public readonly string Text;
            public readonly string TagKey;

            public VisualToken(VisualTokenKind kind, string text, string tagKey = "")
            {
                Kind = kind;
                Text = text;
                TagKey = tagKey;
            }
        }

        private static string VisualReverseLinePreservingTagsAndLtr(string shapedLine)
        {
            var tokens = TokenizeForVisualOrder(shapedLine);
            if (tokens.Count == 0) return shapedLine;

            // Reverse the token sequence for LTR visual rendering, keeping LTR runs intact
            // and swapping paired OpenTag <-> CloseTag positions so <color=x>ABC</color> wraps the reversed glyphs cleanly!
            var pairedSwaps = FindPairedTagSwaps(tokens);
            var swapped = new VisualToken[tokens.Count];
            for (int i = 0; i < tokens.Count; i++)
                swapped[i] = tokens[i];

            for (int s = 0; s < pairedSwaps.Count; s++)
            {
                int openIdx = pairedSwaps[s].Key;
                int closeIdx = pairedSwaps[s].Value;
                var tmp = swapped[openIdx];
                swapped[openIdx] = swapped[closeIdx];
                swapped[closeIdx] = tmp;
            }

            var sb = new StringBuilder(shapedLine.Length);
            for (int i = swapped.Length - 1; i >= 0; i--)
            {
                var tok = swapped[i];
                if (tok.Kind == VisualTokenKind.RtlChar && tok.Text.Length == 1)
                    sb.Append(MirrorBracket(tok.Text[0]));
                else
                    sb.Append(tok.Text);
            }
            return sb.ToString();
        }

        private static List<KeyValuePair<int, int>> FindPairedTagSwaps(List<VisualToken> tokens)
        {
            var pairs = new List<KeyValuePair<int, int>>();
            var stack = new List<KeyValuePair<string, int>>();

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == VisualTokenKind.OpenTag)
                {
                    stack.Add(new KeyValuePair<string, int>(tokens[i].TagKey, i));
                }
                else if (tokens[i].Kind == VisualTokenKind.CloseTag)
                {
                    for (int k = stack.Count - 1; k >= 0; k--)
                    {
                        if (stack[k].Key == tokens[i].TagKey)
                        {
                            pairs.Add(new KeyValuePair<int, int>(stack[k].Value, i));
                            stack.RemoveAt(k);
                            break;
                        }
                    }
                }
            }
            return pairs;
        }

        private static List<VisualToken> TokenizeForVisualOrder(string line)
        {
            var tokens = new List<VisualToken>(line.Length);
            int i = 0;
            int n = line.Length;

            while (i < n)
            {
                if (TryClassifyTagAt(line, i, out int tagLen, out bool isClosing, out string tagKey, out bool isPlaceholder))
                {
                    if (isPlaceholder)
                        tokens.Add(new VisualToken(VisualTokenKind.LtrRun, line.Substring(i, tagLen)));
                    else
                        tokens.Add(new VisualToken(isClosing ? VisualTokenKind.CloseTag : VisualTokenKind.OpenTag,
                            line.Substring(i, tagLen), tagKey));
                    i += tagLen;
                    continue;
                }

                char c = line[i];
                if (IsLtrStrongOrDigit(c))
                {
                    int start = i;
                    i++;
                    while (i < n)
                    {
                        if (TryClassifyTagAt(line, i, out _, out _, out _, out _))
                            break;
                        char nc = line[i];
                        if (IsLtrStrongOrDigit(nc))
                        {
                            i++;
                            continue;
                        }
                        // Keep internal connectors (e.g. 100%, 12:30, 1,000, v1.4, HP/MP) attached when followed by LTR/digit or %
                        if ((nc == '%' || nc == '+') && char.IsDigit(line[i - 1]))
                        {
                            i++;
                            continue;
                        }
                        if ((nc == '.' || nc == ',' || nc == ':' || nc == '/' || nc == '-' || nc == '_')
                            && i + 1 < n && IsLtrStrongOrDigit(line[i + 1]))
                        {
                            i++;
                            continue;
                        }
                        if (nc == ' ' && i + 1 < n && IsAsciiLetter(line[i - 1]) && IsAsciiLetter(line[i + 1]))
                        {
                            i++;
                            continue;
                        }
                        break;
                    }
                    tokens.Add(new VisualToken(VisualTokenKind.LtrRun, line.Substring(start, i - start)));
                    continue;
                }

                // Attach trailing combining marks to their base Arabic glyph so diacritics stay over the right letter
                int glyphStart = i;
                i++;
                while (i < n && IsArabicDiacritic(line[i]))
                    i++;
                if (i - glyphStart == 1)
                {
                    tokens.Add(new VisualToken(VisualTokenKind.RtlChar, line.Substring(glyphStart, 1)));
                }
                else
                {
                    // Reverse combining sequence? In LTR visual rendering, base glyph followed by combining mark still attaches to base glyph!
                    tokens.Add(new VisualToken(VisualTokenKind.LtrRun, line.Substring(glyphStart, i - glyphStart)));
                }
            }

            return tokens;
        }

        private static bool TryMatchMarkupTagAt(string text, int index, out int length)
        {
            return TryClassifyTagAt(text, index, out length, out _, out _, out _);
        }

        private static bool TryClassifyTagAt(
            string text,
            int index,
            out int length,
            out bool isClosing,
            out string tagKey,
            out bool isPlaceholder)
        {
            length = 0;
            isClosing = false;
            tagKey = "";
            isPlaceholder = false;

            char c = text[index];
            if (c == '<')
            {
                var m = HtmlRichTagRegex.Match(text, index);
                if (m.Success && m.Index == index)
                {
                    length = m.Length;
                    string name = m.Groups["name"].Value.ToLowerInvariant();
                    if (name == "quad")
                    {
                        isPlaceholder = true;
                        return true;
                    }
                    isClosing = m.Groups["close"].Success;
                    tagKey = "html:" + name;
                    return true;
                }
            }
            else if (c == '[')
            {
                var m = NguiColorOrStyleTagRegex.Match(text, index);
                if (m.Success && m.Index == index)
                {
                    length = m.Length;
                    if (m.Groups["hex"].Success)
                    {
                        isClosing = false;
                        tagKey = "ngui:color";
                        return true;
                    }
                    if (m.Groups["reset"].Success)
                    {
                        isClosing = true;
                        tagKey = "ngui:color";
                        return true;
                    }
                    isClosing = m.Groups["close"].Success;
                    tagKey = "ngui:" + m.Groups["style"].Value.ToLowerInvariant();
                    return true;
                }
            }
            else if (c == '{')
            {
                int close = text.IndexOf('}', index + 1);
                if (close > index + 1 && close - index <= 32)
                {
                    bool valid = true;
                    for (int k = index + 1; k < close; k++)
                    {
                        if (text[k] == '\n' || text[k] == '\r' || text[k] == '{') { valid = false; break; }
                    }
                    if (valid)
                    {
                        length = close - index + 1;
                        isPlaceholder = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsAsciiLetter(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool IsLtrStrongOrDigit(char c) =>
            IsAsciiLetter(c) || (c >= '0' && c <= '9') || (c >= '\u0660' && c <= '\u0669');

        private static char MirrorBracket(char c) => c switch
        {
            '(' => ')',
            ')' => '(',
            '[' => ']',
            ']' => '[',
            '{' => '}',
            '}' => '{',
            '«' => '»',
            '»' => '«',
            '<' => '>',
            '>' => '<',
            _ => c
        };

        private static string NormalizeLf(string s) =>
            s.IndexOf('\r') >= 0 ? s.Replace("\r\n", "\n").Replace('\r', '\n') : s;

        private static string StripWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i]))
                    sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
