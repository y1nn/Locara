using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Locara.Core;

public static partial class StarTruckerDisplay
{
    private const string LanguageHash = "4B5BC03AE070DEA72119D57536CB32680FAE6C80CB4A1A3C01BB22A587E2C82F";
    private const string FontName = "Noto Sans Arabic";
    private sealed record AssetFile(string Path, string Hash, int ObjectTable, int ObjectCount);
    private static readonly AssetFile Shared = new("Star Trucker_Data/sharedassets0.assets",
        "716B7CB70B4B59C6BF5BD19191CFAE1951257C6C7579C7339BBB4BB15BCAD458", 492, 453);
    private static readonly AssetFile Settings = new("Star Trucker_Data/resources.assets",
        "6CA3CD7945B3A84AF28996AF89C4746CC0D0539C38C9648DFF3FDB676162F36C", 3468, 2243);
    internal static readonly IReadOnlyList<string> AssetPaths = [Shared.Path, Settings.Path];

    internal static void ValidateTrialChanges(IReadOnlyList<PackageChange> changes)
    {
        if (changes.Count != 3
            || !changes.Any(change => change.Path == StarTruckerCatalog.LanguageArchive && change.OriginalHash == LanguageHash)
            || !changes.Any(change => change.Path == Shared.Path && change.OriginalHash == Shared.Hash)
            || !changes.Any(change => change.Path == Settings.Path && change.OriginalHash == Settings.Hash))
            throw new InvalidDataException("حزمة التجربة لا تطابق ملفات إصدار Star Trucker المعتمد.");
    }

    internal static string? UnsupportedReason(string root, string archive, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            return "تجهيز العرض العربي يحتاج إلى Windows 10 إصدار 1903 أو أحدث.";
        if (PackageInstaller.FileHash(archive, cancellation) != LanguageHash)
            return "ملف اللغة يختلف عن الإصدار المختبر 1.75.2.0.";
        foreach (var asset in new[] { Shared, Settings })
        {
            string path = Path.Combine(root, asset.Path);
            Identity.RequireNoLinks(path);
            if (!File.Exists(path) || PackageInstaller.FileHash(path, cancellation) != asset.Hash)
                return "ملف الخط أو العرض غائب أو مختلف عن الإصدار المختبر — " + asset.Path;
        }
        return null;
    }

    public const int AutoWrap = -1;

    public static bool HasArabic(string text) => ArabicLetters().IsMatch(text);

    public static bool Supports(Unit unit) => !string.IsNullOrWhiteSpace(unit.Target);

    public static string Shape(string text) => Shape(text, maxLineChars: 0);

    public static string Shape(string text, int maxLineChars)
    {
        if (!HasArabic(text)) return text;
        var parts = LineBreakPattern().Split(text);
        if (parts.Length == 1) return ShapeParagraph(text, maxLineChars);
        for (int i = 0; i < parts.Length; i += 2)
        {
            if (parts[i].Length > 0 && HasArabic(parts[i]))
                parts[i] = ShapeParagraph(parts[i], maxLineChars);
        }
        return string.Concat(parts);
    }

    private static string ShapeParagraph(string paragraph, int maxLineChars)
    {
        if (maxLineChars == 0) return ShapeLine(paragraph);
        var wrapped = WrapArabicParagraph(paragraph, maxLineChars);
        if (wrapped.Count == 1) return ShapeLine(wrapped[0]);
        for (int i = 0; i < wrapped.Count; i++)
        {
            if (wrapped[i].Length > 0 && HasArabic(wrapped[i]))
                wrapped[i] = ShapeLine(wrapped[i]);
        }
        return string.Join('\n', wrapped);
    }

    private enum WrapAtomKind { Text, Placeholder, OpenTag, CloseTag, VoidTag, Space }

    private sealed class WrapAtom(WrapAtomKind kind, string raw, int visibleWidth, string tagName = "", bool isBbCode = false)
    {
        public WrapAtomKind Kind { get; } = kind;
        public string Raw { get; } = raw;
        public int VisibleWidth { get; } = visibleWidth;
        public string TagName { get; } = tagName;
        public bool IsBbCode { get; } = isBbCode;
        public bool HasClosingPair { get; set; }
    }

    private sealed class WrapWord
    {
        public List<WrapAtom> Atoms { get; } = [];
        public int VisibleWidth { get; set; }
    }

    internal static List<string> WrapArabicParagraph(string paragraph, int maxLineChars)
    {
        if (string.IsNullOrEmpty(paragraph) || maxLineChars == 0) return [paragraph];

        var atoms = TokenizeForWrap(paragraph);
        int totalVisible = 0;
        foreach (var atom in atoms) totalVisible += atom.VisibleWidth;

        int limit = maxLineChars < 0
            ? (totalVisible <= 22 ? 0 : totalVisible <= 140 ? 22 : 34)
            : Math.Clamp(maxLineChars, 10, 120);

        if (limit == 0 || totalVisible <= limit) return [paragraph];

        // Group atoms into wrappable words separated by breakable spaces.
        // Keep standalone opening tags attached to the following word and closing tags attached to the preceding word
        // so wrapping never produces empty <tag></tag> shells.
        var words = new List<WrapWord>();
        var current = new WrapWord();
        var pendingLeadingTags = new List<WrapAtom>();
        int noBreakDepth = 0;

        static bool IsNonBreakableTag(string tagName) =>
            tagName.Equals("nobr", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("link", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("a", StringComparison.OrdinalIgnoreCase);

        foreach (var atom in atoms)
        {
            if (atom.Kind == WrapAtomKind.OpenTag && IsNonBreakableTag(atom.TagName))
                noBreakDepth++;
            else if (atom.Kind == WrapAtomKind.CloseTag && IsNonBreakableTag(atom.TagName) && noBreakDepth > 0)
                noBreakDepth--;

            if (atom.Kind == WrapAtomKind.Space && noBreakDepth == 0)
            {
                if (current.Atoms.Count > 0)
                {
                    // Pull any trailing unclosed opening tags off the end of this word onto the next word.
                    int splitAt = current.Atoms.Count;
                    while (splitAt > 0 && current.Atoms[splitAt - 1].Kind == WrapAtomKind.OpenTag)
                        splitAt--;
                    if (splitAt < current.Atoms.Count)
                    {
                        for (int k = splitAt; k < current.Atoms.Count; k++)
                            pendingLeadingTags.Add(current.Atoms[k]);
                        current.Atoms.RemoveRange(splitAt, current.Atoms.Count - splitAt);
                    }
                    if (current.Atoms.Count > 0)
                    {
                        words.Add(current);
                        current = new WrapWord();
                    }
                }
                continue;
            }

            if (atom.Kind == WrapAtomKind.CloseTag && current.Atoms.Count == 0 && pendingLeadingTags.Count == 0 && words.Count > 0)
            {
                words[^1].Atoms.Add(atom);
                continue;
            }

            if (pendingLeadingTags.Count > 0)
            {
                current.Atoms.AddRange(pendingLeadingTags);
                pendingLeadingTags.Clear();
            }

            current.Atoms.Add(atom);
            current.VisibleWidth += atom.VisibleWidth;
        }

        if (pendingLeadingTags.Count > 0)
        {
            current.Atoms.AddRange(pendingLeadingTags);
            pendingLeadingTags.Clear();
        }
        if (current.Atoms.Count > 0)
            words.Add(current);

        if (words.Count <= 1) return [paragraph];

        var ranges = ComputeBalancedLineRanges(words, limit);
        if (ranges.Count <= 1) return [paragraph];

        // Emit lines with balanced open/close tags across line breaks.
        var lines = new List<string>(ranges.Count);
        var activeTags = new List<WrapAtom>();
        var sb = new System.Text.StringBuilder(paragraph.Length + 32);

        for (int lineIdx = 0; lineIdx < ranges.Count; lineIdx++)
        {
            var (startWord, endWord) = ranges[lineIdx];
            sb.Clear();

            if (lineIdx > 0 && activeTags.Count > 0)
            {
                foreach (var tag in activeTags)
                    sb.Append(tag.Raw);
            }

            for (int w = startWord; w < endWord; w++)
            {
                if (w > startWord) sb.Append(' ');
                foreach (var atom in words[w].Atoms)
                {
                    sb.Append(atom.Raw);
                    if (atom.Kind == WrapAtomKind.OpenTag)
                    {
                        activeTags.Add(atom);
                    }
                    else if (atom.Kind == WrapAtomKind.CloseTag)
                    {
                        for (int t = activeTags.Count - 1; t >= 0; t--)
                        {
                            if (activeTags[t].TagName.Equals(atom.TagName, StringComparison.OrdinalIgnoreCase))
                            {
                                activeTags.RemoveAt(t);
                                break;
                            }
                        }
                    }
                }
            }

            if (lineIdx < ranges.Count - 1 && activeTags.Count > 0)
            {
                for (int t = activeTags.Count - 1; t >= 0; t--)
                {
                    if (activeTags[t].HasClosingPair)
                    {
                        if (activeTags[t].IsBbCode)
                            sb.Append("[/").Append(activeTags[t].TagName).Append(']');
                        else
                            sb.Append("</").Append(activeTags[t].TagName).Append('>');
                    }
                }
            }

            lines.Add(sb.ToString());
        }

        return lines;
    }

    private static HashSet<string> FindPairedBbCodeNames(IEnumerable<string> tokens)
    {
        var opens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string tok in tokens)
        {
            var m = BbCodeTagName().Match(tok);
            if (!m.Success) continue;
            string name = m.Groups["name"].Value;
            if (m.Groups["close"].Success) closes.Add(name);
            else opens.Add(name);
        }
        opens.IntersectWith(closes);
        return opens;
    }

    private static bool TryParseRichTag(string tok, IReadOnlySet<string> pairedBbNames,
        out string name, out bool isClose, out bool isSelfClose, out bool isBbCode)
    {
        var xmlMatch = XmlTagName().Match(tok);
        if (xmlMatch.Success)
        {
            name = xmlMatch.Groups[1].Value;
            isClose = tok.StartsWith("</", StringComparison.Ordinal);
            isSelfClose = tok.EndsWith("/>", StringComparison.Ordinal);
            isBbCode = false;
            return true;
        }
        if (pairedBbNames.Count > 0)
        {
            var bbMatch = BbCodeTagName().Match(tok);
            if (bbMatch.Success && pairedBbNames.Contains(bbMatch.Groups["name"].Value))
            {
                name = bbMatch.Groups["name"].Value;
                isClose = bbMatch.Groups["close"].Success;
                isSelfClose = false;
                isBbCode = true;
                return true;
            }
        }
        name = "";
        isClose = false;
        isSelfClose = false;
        isBbCode = false;
        return false;
    }

    private static List<WrapAtom> TokenizeForWrap(string paragraph)
    {
        var atoms = new List<WrapAtom>();
        var matches = Tokens.MatchTokens(paragraph);
        var pairedBbNames = FindPairedBbCodeNames(matches.Select(m => m.Value));
        int cursor = 0;

        void AddPlainSegment(string segment)
        {
            int pos = 0;
            while (pos < segment.Length)
            {
                if (segment[pos] is ' ' or '\t')
                {
                    int s = pos;
                    while (pos < segment.Length && segment[pos] is ' ' or '\t') pos++;
                    atoms.Add(new WrapAtom(WrapAtomKind.Space, " ", 1));
                }
                else
                {
                    int s = pos;
                    while (pos < segment.Length && segment[pos] is not (' ' or '\t')) pos++;
                    string text = segment[s..pos];
                    atoms.Add(new WrapAtom(WrapAtomKind.Text, text, CountVisibleChars(text)));
                }
            }
        }

        foreach (Match match in matches)
        {
            if (match.Index > cursor)
                AddPlainSegment(paragraph[cursor..match.Index]);

            string tok = match.Value;
            if (TryParseRichTag(tok, pairedBbNames, out string name, out bool isClose, out bool isSelfClose, out bool isBbCode))
            {
                if (isSelfClose || (!isClose && !isBbCode && IsInlineVoidTag(name)))
                {
                    int width = name.Equals("sprite", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("quad", StringComparison.OrdinalIgnoreCase) ? 2
                        : name.Equals("space", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    atoms.Add(new WrapAtom(WrapAtomKind.VoidTag, tok, width, name, isBbCode));
                }
                else if (isClose)
                {
                    atoms.Add(new WrapAtom(WrapAtomKind.CloseTag, tok, 0, name, isBbCode));
                }
                else
                {
                    atoms.Add(new WrapAtom(WrapAtomKind.OpenTag, tok, 0, name, isBbCode));
                }
            }
            else
            {
                // Game placeholder like {0}, {[AMOUNT]}, %s, [ICON]: atomic, approximate runtime visible width.
                atoms.Add(new WrapAtom(WrapAtomKind.Placeholder, tok, Math.Clamp(tok.Length, 1, 4)));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < paragraph.Length)
            AddPlainSegment(paragraph[cursor..]);

        // Pre-match open/close tag pairs so we only close paired tags across wrapped line breaks.
        var openStack = new Stack<WrapAtom>();
        foreach (var atom in atoms)
        {
            if (atom.Kind == WrapAtomKind.OpenTag)
            {
                openStack.Push(atom);
            }
            else if (atom.Kind == WrapAtomKind.CloseTag)
            {
                if (openStack.Count > 0 && openStack.Peek().TagName.Equals(atom.TagName, StringComparison.OrdinalIgnoreCase))
                {
                    openStack.Pop().HasClosingPair = true;
                }
                else
                {
                    // Search stack in case an unclosed tag (e.g. <color>) sits above a closed outer tag.
                    var temp = new List<WrapAtom>();
                    while (openStack.Count > 0)
                    {
                        var top = openStack.Pop();
                        if (top.TagName.Equals(atom.TagName, StringComparison.OrdinalIgnoreCase))
                        {
                            top.HasClosingPair = true;
                            break;
                        }
                        temp.Add(top);
                    }
                    for (int i = temp.Count - 1; i >= 0; i--)
                        openStack.Push(temp[i]);
                }
            }
        }

        return atoms;
    }

    private static List<(int Start, int End)> ComputeBalancedLineRanges(List<WrapWord> words, int maxLineChars)
    {
        int n = words.Count;
        var greedy = new List<(int Start, int End)>();
        int start = 0;
        while (start < n)
        {
            int len = words[start].VisibleWidth;
            int end = start + 1;
            while (end < n && len + 1 + words[end].VisibleWidth <= maxLineChars)
            {
                len += 1 + words[end].VisibleWidth;
                end++;
            }
            greedy.Add((start, end));
            start = end;
        }

        int lineCount = greedy.Count;
        if (lineCount <= 1 || n > 200) return greedy;

        // Prefix sums of word widths for O(1) line length lookup.
        var prefix = new int[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + words[i].VisibleWidth;
        int LineLen(int s, int e) => (prefix[e] - prefix[s]) + (e - s - 1);

        int totalLen = LineLen(0, n);
        double ideal = (double)totalLen / lineCount;

        // DP: dp[k, i] = minimum variance cost to lay out words[0..i) in exactly k lines,
        // subject to every multi-word line having length <= maxLineChars.
        var dp = new double[lineCount + 1, n + 1];
        var prev = new int[lineCount + 1, n + 1];
        for (int k = 0; k <= lineCount; k++)
            for (int i = 0; i <= n; i++)
                dp[k, i] = double.PositiveInfinity;
        dp[0, 0] = 0.0;

        for (int k = 1; k <= lineCount; k++)
        {
            for (int i = k; i <= n; i++)
            {
                for (int j = k - 1; j < i; j++)
                {
                    if (double.IsPositiveInfinity(dp[k - 1, j])) continue;
                    int len = LineLen(j, i);
                    if (len > maxLineChars && i - j > 1) continue;
                    double diff = len - ideal;
                    double cost = dp[k - 1, j] + diff * diff;
                    if (cost < dp[k, i])
                    {
                        dp[k, i] = cost;
                        prev[k, i] = j;
                    }
                }
            }
        }

        if (double.IsPositiveInfinity(dp[lineCount, n])) return greedy;

        var balanced = new (int Start, int End)[lineCount];
        int cur = n;
        for (int k = lineCount; k >= 1; k--)
        {
            int p = prev[k, cur];
            balanced[k - 1] = (p, cur);
            cur = p;
        }
        return [.. balanced];
    }

    private static int CountVisibleChars(string text)
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
        c is (>= '\u064B' and <= '\u065F') or '\u0670' or (>= '\u06D6' and <= '\u06ED')
            or '\u200B' or '\u200C' or '\u200D' or '\uFEFF';

    private static bool IsInlineVoidTag(string tagName) =>
        tagName.Equals("sprite", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("quad", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("space", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("pos", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("indent", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("line-indent", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("margin", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("voffset", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("page", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("br", StringComparison.OrdinalIgnoreCase);

    private static (string Masked, Dictionary<string, string> Map) MaskPureLtr(string text)
    {
        // Use ONLY A-Z letters (Unicode Bidi_Class = L, Strong Left-to-Right).
        // Never use underscores (ON) or digits (EN), which ICU ubidi splits when adjacent to Arabic (AL) characters like '؟'.
        string prefix = "LOCARATOK";
        while (text.Contains(prefix, StringComparison.OrdinalIgnoreCase)) prefix += "X";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string masked = Tokens.ReplaceTokens(text, match =>
        {
            int n = map.Count;
            string code = $"{(char)('A' + (n / 676) % 26)}{(char)('A' + (n / 26) % 26)}{(char)('A' + n % 26)}";
            string key = prefix + code + "END";
            map[key] = match;
            return key;
        });
        return (masked, map);
    }

    private static string ShapeLine(string text)
    {
        if (!HasArabic(text)) return text;
        string? shaped = TryShapeLineFull(text);
        if (shaped != null) return shaped;
        // Fallback for exotic cross-boundary tag layouts: shape each tag-free segment individually
        // so all rich-text tags preserve 100% valid nesting while Arabic glyphs still render shaped.
        string fallback = ShapeTextSegmentsOnly(text);
        if (!Tokens.ValidTagNesting(text, fallback))
            throw new InvalidDataException("تغيّرت رموز اللعبة أثناء تجهيز العرض العربي.");
        return fallback;
    }

    private static string? TryShapeLineFull(string text)
    {
        var (masked, tokens) = MaskPureLtr(text);
        // Strip unmatched outer opening tags at the start and unmatched closing tags at the end
        // (common when multi-line tags like <size=11>...\n...</size> span line breaks).
        StripUnmatchedOuterTags(masked, tokens, out string prefix, out string core, out string suffix);
        if (core.Length == 0 || !HasArabic(core))
        {
            string restored = Tokens.Restore(prefix + core + suffix, tokens);
            return Tokens.ValidTagNesting(text, restored) ? restored : null;
        }

        var shaped = new char[core.Length];
        int error = 0;
        const uint shapeLetters = 8;
        int length = ShapeArabic(core, core.Length, shaped, shaped.Length, shapeLetters, ref error);
        CheckIcu(error);

        nint layout = OpenBidi();
        if (layout == 0) throw new InvalidOperationException("تعذّر إنشاء سياق ترتيب العربية.");
        var pinned = GCHandle.Alloc(shaped, GCHandleType.Pinned);
        try
        {
            SetParagraph(layout, pinned.AddrOfPinnedObject(), length, 1, 0, ref error);
            CheckIcu(error);
            const ushort keepCombiningMarksAndMirror = 3;
            var display = new char[length];
            int written = WriteDisplay(layout, display, length, keepCombiningMarksAndMirror, ref error);
            CheckIcu(error);
            string reorderedCore = FixReversedTagPairs(core, new(display, 0, written), tokens);
            string reordered = prefix + reorderedCore + suffix;
            foreach (string key in tokens.Keys)
            {
                if (!reordered.Contains(key, StringComparison.Ordinal))
                    return null;
            }
            string result = Tokens.Restore(reordered, tokens);
            return Tokens.ValidTagNesting(text, result) ? result : null;
        }
        finally
        {
            CloseBidi(layout);
            pinned.Free();
        }
    }

    private static void StripUnmatchedOuterTags(string masked, IReadOnlyDictionary<string, string> tokens,
        out string prefix, out string core, out string suffix)
    {
        prefix = "";
        suffix = "";
        core = masked;
        if (tokens.Count == 0) return;

        var pairedBbNames = FindPairedBbCodeNames(tokens.Values);
        var openStack = new Stack<(string Key, string TagName)>();
        var unmatchedClose = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in tokens)
        {
            if (!TryParseRichTag(value, pairedBbNames, out string name, out bool isClose, out bool isSelfClose, out bool isBbCode))
                continue;
            if (isSelfClose || (!isClose && !isBbCode && IsInlineVoidTag(name))) continue;
            if (!isClose)
                openStack.Push((key, name));
            else if (openStack.Count > 0 && openStack.Peek().TagName.Equals(name, StringComparison.OrdinalIgnoreCase))
                openStack.Pop();
            else
                unmatchedClose.Add(key);
        }
        var unmatchedOpen = new HashSet<string>(openStack.Select(o => o.Key), StringComparer.Ordinal);

        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            foreach (string key in unmatchedOpen)
            {
                if (core.StartsWith(key, StringComparison.Ordinal))
                {
                    prefix += key;
                    core = core[key.Length..];
                    progressed = true;
                    break;
                }
            }
            foreach (string key in unmatchedClose)
            {
                if (core.EndsWith(key, StringComparison.Ordinal))
                {
                    suffix = key + suffix;
                    core = core[..^key.Length];
                    progressed = true;
                    break;
                }
            }
        }
    }

    private static string ShapeTextSegmentsOnly(string text)
    {
        var matches = XmlTagToken().Matches(text);
        if (matches.Count == 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int cursor = 0;
        foreach (Match m in matches)
        {
            if (m.Index > cursor)
            {
                string seg = text[cursor..m.Index];
                sb.Append(HasArabic(seg) ? (TryShapeLineFull(seg) ?? seg) : seg);
            }
            sb.Append(m.Value);
            cursor = m.Index + m.Length;
        }
        if (cursor < text.Length)
        {
            string tail = text[cursor..];
            sb.Append(HasArabic(tail) ? (TryShapeLineFull(tail) ?? tail) : tail);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9_-]*(?:\s*=[^<>]*|\s+[^<>]*)?/?>")]
    private static partial Regex XmlTagToken();

    private static string FixReversedTagPairs(string masked, string reordered, IReadOnlyDictionary<string, string> tokens)
    {
        if (tokens.Count == 0) return reordered;
        var pairedBbNames = FindPairedBbCodeNames(tokens.Values);
        var openStack = new Stack<(string Key, string TagName)>();
        var pairs = new List<(string OpenKey, string CloseKey)>();
        var unmatchedClose = new List<string>();
        foreach (var (key, value) in tokens)
        {
            if (!masked.Contains(key, StringComparison.Ordinal)) continue;
            if (!TryParseRichTag(value, pairedBbNames, out string name, out bool isClose, out bool isSelfClose, out bool isBbCode))
                continue;
            if (isSelfClose || (!isClose && !isBbCode && IsInlineVoidTag(name))) continue;
            if (!isClose)
            {
                openStack.Push((key, name));
            }
            else if (openStack.Count > 0 && openStack.Peek().TagName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var open = openStack.Pop();
                pairs.Add((open.Key, key));
            }
            else
            {
                unmatchedClose.Add(key);
            }
        }
        foreach (var (openKey, closeKey) in pairs)
        {
            int openPos = reordered.IndexOf(openKey, StringComparison.Ordinal);
            int closePos = reordered.IndexOf(closeKey, StringComparison.Ordinal);
            if (openPos >= 0 && closePos >= 0 && closePos < openPos)
            {
                const string tempMarker = "LOCARASWAPMARKER";
                reordered = reordered.Replace(openKey, tempMarker, StringComparison.Ordinal)
                    .Replace(closeKey, openKey, StringComparison.Ordinal)
                    .Replace(tempMarker, closeKey, StringComparison.Ordinal);
            }
        }
        // Fix adjacent nested opening or closing tags that ICU kept in LTR order (e.g. <color><b>...</b></color> -> <b><color>...</color></b>)
        if (pairs.Count >= 2)
        {
            for (int pass = 0; pass < pairs.Count; pass++)
            {
                bool swapped = false;
                for (int i = 0; i < pairs.Count; i++)
                {
                    for (int j = i + 1; j < pairs.Count; j++)
                    {
                        var (o1, c1) = pairs[i];
                        var (o2, c2) = pairs[j];
                        int op1 = reordered.IndexOf(o1, StringComparison.Ordinal);
                        int cp1 = reordered.IndexOf(c1, StringComparison.Ordinal);
                        int op2 = reordered.IndexOf(o2, StringComparison.Ordinal);
                        int cp2 = reordered.IndexOf(c2, StringComparison.Ordinal);
                        if (op1 < 0 || cp1 < 0 || op2 < 0 || cp2 < 0) continue;

                        // Adjacent openings with crossed closes: o1 directly before o2, but c1 < c2 -> swap o1 & o2
                        if (op1 + o1.Length == op2 && cp1 < cp2)
                        {
                            reordered = reordered[..op1] + o2 + o1 + reordered[(op2 + o2.Length)..];
                            swapped = true;
                        }
                        else if (op2 + o2.Length == op1 && cp2 < cp1)
                        {
                            reordered = reordered[..op2] + o1 + o2 + reordered[(op1 + o1.Length)..];
                            swapped = true;
                        }
                        // Adjacent closes with crossed opens: c1 directly before c2, but o1 < o2 -> swap c1 & c2
                        else if (cp1 + c1.Length == cp2 && op1 < op2)
                        {
                            reordered = reordered[..cp1] + c2 + c1 + reordered[(cp2 + c2.Length)..];
                            swapped = true;
                        }
                        else if (cp2 + c2.Length == cp1 && op2 < op1)
                        {
                            reordered = reordered[..cp2] + c1 + c2 + reordered[(cp1 + c1.Length)..];
                            swapped = true;
                        }
                    }
                }
                if (!swapped) break;
            }
        }
        while (openStack.Count > 0)
        {
            var (openKey, _) = openStack.Pop();
            if (masked.StartsWith(openKey, StringComparison.Ordinal) && reordered.EndsWith(openKey, StringComparison.Ordinal))
                reordered = openKey + reordered[..^openKey.Length];
        }
        foreach (string closeKey in unmatchedClose)
        {
            if (masked.EndsWith(closeKey, StringComparison.Ordinal) && reordered.StartsWith(closeKey, StringComparison.Ordinal))
                reordered = reordered[closeKey.Length..] + closeKey;
        }
        return reordered;
    }

    private static void CheckIcu(int error)
    {
        if (error > 0) throw new InvalidDataException($"تعذّر تجهيز اتجاه النص العربي (ICU {error}).");
    }

    internal static IReadOnlyList<PackageChange> WriteAssets(string root, string payload, CancellationToken cancellation,
        ArabicFontChoice? fontChoice = null)
    {
        var chosen = fontChoice ?? ArabicFontCatalog.BuiltInFonts[0];
        byte[] fontBytes = ArabicFontCatalog.LoadBytes(chosen);
        string fontName = string.IsNullOrWhiteSpace(chosen.FontFamilyName) ? FontName : chosen.FontFamilyName;
        var changes = new List<PackageChange>();
        foreach (var layout in new[] { Shared, Settings })
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(root, layout.Path);
            Identity.RequireNoLinks(path);
            byte[] original = File.ReadAllBytes(path);
            if (Identity.Hash(original) != layout.Hash)
                throw new InvalidDataException("تغيّر ملف العرض منذ المعاينة — " + layout.Path);
            byte[] updated = RewriteAssets(original, layout, fontBytes, fontName, cancellation);
            string destination = Path.Combine(payload, layout.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, updated);
            string hash = PackageInstaller.FileHash(destination, cancellation);
            if (hash != Identity.Hash(updated))
                throw new IOException("ملف العرض الناتج لا يطابق البناء — " + layout.Path);
            changes.Add(new(layout.Path, layout.Hash, hash));
        }
        return changes;
    }

    internal static void WriteLicense(string staging, ArabicFontChoice? choice = null) =>
        ArabicFontCatalog.WriteLicense(staging, choice);

    private record struct ObjectEntry(long Id, int Offset, int Size);
    private const int ObjectRecordSize = 24;
    private const int HeaderFileSize = 24;
    private const int HeaderDataOffset = 32;

    // Unity serialized files v22. The layout and modifications apply only to the hashes above.
    private static byte[] RewriteAssets(byte[] original, AssetFile layout, byte[] fontBytes, string fontName, CancellationToken cancellation)
    {
        int start = checked((int)BinaryPrimitives.ReadInt64BigEndian(original.AsSpan(HeaderDataOffset)));
        if (BinaryPrimitives.ReadInt32BigEndian(original.AsSpan(8)) != 22 || original[16] != 0
            || BinaryPrimitives.ReadInt64BigEndian(original.AsSpan(HeaderFileSize)) != original.Length
            || layout.ObjectTable + layout.ObjectCount * ObjectRecordSize > start)
            throw new InvalidDataException("جدول موارد عرض Star Trucker غير مطابق.");

        byte[] header = original[..start];
        using var output = new MemoryStream(original.Length);
        output.Write(header);
        var edits = new Dictionary<long, byte[]>();
        int expectedOffset = 0;
        for (int index = 0; index < layout.ObjectCount; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            int recordOffset = layout.ObjectTable + index * ObjectRecordSize;
            var entry = ReadObject(original.AsSpan(recordOffset));
            if (entry.Offset != expectedOffset || entry.Size < 0 || entry.Size > original.Length - start - entry.Offset)
                throw new InvalidDataException("موضع مورد Unity خارج ملف العرض.");
            var data = original.AsSpan(start + entry.Offset, entry.Size);
            byte[]? replacement = layout == Shared ? entry.Id switch
            {
                21 => ExpandFallbackMaterial(data),
                78 => ReplaceFont(data, fontBytes, fontName),
                439 => ExpandFallbackAtlas(data),
                441 => AddFallback(data, 21760, fileId: 0, expectedCount: 3),
                445 => AddFallback(data, 21460, fileId: 0, expectedCount: 3),
                _ => null
            } : entry.Id == 1602 ? AddFallback(data, 0x98, fileId: 2, expectedCount: 0) : null;
            if (replacement != null) edits.Add(entry.Id, replacement);

            var record = header.AsSpan(recordOffset);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], output.Position - start);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], replacement?.Length ?? entry.Size);
            output.Write(replacement is null ? data : replacement);
            output.Write(new byte[(int)((8 - output.Position % 8) % 8)]);
            expectedOffset = checked((entry.Offset + entry.Size + 7) & ~7);
        }
        if (edits.Count != (layout == Shared ? 5 : 1))
            throw new InvalidDataException("لم يُعثر على جميع موارد الخط المطلوبة.");

        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(HeaderFileSize), output.Length);
        output.Position = 0;
        output.Write(header);
        byte[] result = output.ToArray();
        for (int index = 0; index < layout.ObjectCount; index++)
        {
            int recordOffset = layout.ObjectTable + index * ObjectRecordSize;
            var before = ReadObject(original.AsSpan(recordOffset));
            var after = ReadObject(result.AsSpan(recordOffset));
            ReadOnlySpan<byte> expected = edits.TryGetValue(before.Id, out var patched)
                ? patched : original.AsSpan(start + before.Offset, before.Size);
            if (after.Id != before.Id || !result.AsSpan(start + after.Offset, after.Size).SequenceEqual(expected))
                throw new InvalidDataException("تعذّر التحقق من كائنات ملف العرض بعد الكتابة.");
        }
        return result;
    }

    private static ObjectEntry ReadObject(ReadOnlySpan<byte> record) => new(
        BinaryPrimitives.ReadInt64LittleEndian(record),
        checked((int)BinaryPrimitives.ReadInt64LittleEndian(record[8..])),
        BinaryPrimitives.ReadInt32LittleEndian(record[16..]));

    internal static byte[] ExpandFallbackAtlas(ReadOnlySpan<byte> data)
    {
        // Use a compact, fast 1024x1024 dynamic SDFAA atlas at 48pt / 5px padding:
        // - ~3.2x faster CPU FreeType SDFAA rasterization per new glyph (58x58 vs 104x104 px)
        // - 4x smaller GPU Texture2D.Apply() upload per new glyph (1 MB vs 4 MB)
        // - Still holds ~310+ glyphs in ONE atlas (>2x all 141 Arabic forms + punctuation), with multi-atlas safety net.
        const int targetAtlas = 1024;
        const int targetPointSize = 48;
        const int targetPadding = 5;
        const float metricScale = targetPointSize / 86f;

        if (data.Length < 528
            || data[284] != 0
            || BinaryPrimitives.ReadInt32LittleEndian(data[184..]) != 86
            || BinaryPrimitives.ReadInt32LittleEndian(data[308..]) != 511
            || BinaryPrimitives.ReadInt32LittleEndian(data[312..]) != 511
            || BinaryPrimitives.ReadSingleLittleEndian(data[336..]) != 86f
            || BinaryPrimitives.ReadSingleLittleEndian(data[404..]) != 9f
            || BinaryPrimitives.ReadInt32LittleEndian(data[428..]) != 512
            || BinaryPrimitives.ReadInt32LittleEndian(data[432..]) != 512
            || BinaryPrimitives.ReadInt32LittleEndian(data[436..]) != 9
            || BinaryPrimitives.ReadInt32LittleEndian(data[508..]) != 86
            || BinaryPrimitives.ReadInt32LittleEndian(data[512..]) != 9
            || BinaryPrimitives.ReadInt32LittleEndian(data[520..]) != 512
            || BinaryPrimitives.ReadInt32LittleEndian(data[524..]) != 512)
            throw new InvalidDataException("إعدادات أطلس الخط الاحتياطي غير مطابقة.");

        var updated = data.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(184), targetPointSize);
        // Scale m_FaceInfo vertical/horizontal metrics proportionally (keep m_Scale=1f and 0.5f sub/sup size ratios intact).
        ReadOnlySpan<int> faceMetricOffsets = [196, 200, 204, 208, 212, 216, 220, 228, 236, 240, 244, 248, 252];
        foreach (int offset in faceMetricOffsets)
            ScaleFloat(updated, offset, metricScale);

        updated[284] = 1; // Enable m_IsMultiAtlasTexturesEnabled as a safety net so TMP never runs out of glyph space.
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(308), targetAtlas - 1);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(312), targetAtlas - 1);

        // Scale legacy m_fontInfo fields proportionally (keep Scale=1f and SubSize=0.5f intact).
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(336), targetPointSize);
        ReadOnlySpan<int> legacyMetricOffsets = [348, 356, 360, 364, 372, 376, 384, 388, 392, 400];
        foreach (int offset in legacyMetricOffsets)
            ScaleFloat(updated, offset, metricScale);
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(404), targetPadding);
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(408), targetAtlas);
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(412), targetAtlas);

        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(428), targetAtlas);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(432), targetAtlas);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(436), targetPadding);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(508), targetPointSize);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(512), targetPadding);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(520), targetAtlas);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(524), targetAtlas);
        return updated;
    }

    private static void ScaleFloat(byte[] buffer, int offset, float scale)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset));
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), value * scale);
    }

    internal static byte[] ExpandFallbackMaterial(ReadOnlySpan<byte> data)
    {
        const float targetAtlas = 1024f;
        const float targetGradientScale = 6f; // padding (5) + 1
        if (data.Length < 1364
            || BinaryPrimitives.ReadSingleLittleEndian(data[664..]) != 10f
            || BinaryPrimitives.ReadSingleLittleEndian(data[1336..]) != 512f
            || BinaryPrimitives.ReadSingleLittleEndian(data[1360..]) != 512f)
            throw new InvalidDataException("إعدادات خامة الخط الاحتياطي غير مطابقة.");

        var updated = data.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(664), targetGradientScale);
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(1336), targetAtlas);
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(1360), targetAtlas);
        return updated;
    }

    internal static byte[] AddFallback(ReadOnlySpan<byte> data, int listOffset, int fileId, int expectedCount)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[listOffset..]);
        if (count != expectedCount) throw new InvalidDataException("قائمة الخطوط الاحتياطية غير مطابقة.");
        // Prepend Arabic fallback (439) at index 0 so TMP hits Arabic immediately instead of probing Cyrillic + 2 CJK fonts first.
        int firstEntry = listOffset + 4;
        var updated = new byte[data.Length + 12];
        data[..firstEntry].CopyTo(updated);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(listOffset), count + 1);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(firstEntry), fileId);
        BinaryPrimitives.WriteInt64LittleEndian(updated.AsSpan(firstEntry + 4), 439);
        data[firstEntry..].CopyTo(updated.AsSpan(firstEntry + 12));
        return updated;
    }

    internal static byte[] ReplaceFont(ReadOnlySpan<byte> data, byte[] fontBytes, string fontName)
    {
        const int fontStart = 2216;
        int oldFontSize = BinaryPrimitives.ReadInt32LittleEndian(data[(fontStart - 4)..]);
        int oldFontAlignedEnd = (fontStart + oldFontSize + 3) & ~3;
        const int middleSize = 16; // m_Ascent + m_Descent + PPtr + m_FontNames.Array.size
        int oldNameSizeOffset = oldFontAlignedEnd + middleSize;
        int oldNameStart = oldNameSizeOffset + 4;
        int oldNameSize = BinaryPrimitives.ReadInt32LittleEndian(data[oldNameSizeOffset..]);
        if (!data.Slice(oldNameStart, oldNameSize).SequenceEqual("Liberation Sans"u8))
            throw new InvalidDataException("اسم الخط الأصلي غير مطابق.");
        int oldNameAlignedEnd = oldNameStart + ((oldNameSize + 3) & ~3);

        if (!ArabicFontCatalog.HasValidFontHeader(fontBytes))
            throw new InvalidDataException("ملف الخط المختار ليس ملف TrueType/OpenType (.ttf / .otf) صالحاً.");

        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(fontName) ? FontName : fontName.Trim());
        int newFontPad = (4 - fontBytes.Length % 4) % 4;
        int newNamePad = (4 - nameBytes.Length % 4) % 4;
        int suffixLen = data.Length - oldNameAlignedEnd;

        using var ms = new MemoryStream(fontStart + fontBytes.Length + newFontPad + middleSize + 4 + nameBytes.Length + newNamePad + suffixLen);
        ms.Write(data[..(fontStart - 4)]);
        Span<byte> intBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(intBuf, fontBytes.Length);
        ms.Write(intBuf);
        ms.Write(fontBytes);
        if (newFontPad > 0) ms.Write(new byte[newFontPad]);
        ms.Write(data.Slice(oldFontAlignedEnd, middleSize));
        BinaryPrimitives.WriteInt32LittleEndian(intBuf, nameBytes.Length);
        ms.Write(intBuf);
        ms.Write(nameBytes);
        if (newNamePad > 0) ms.Write(new byte[newNamePad]);
        ms.Write(data[oldNameAlignedEnd..]);
        return ms.ToArray();
    }

    private static byte[] ReadResource(string name)
    {
        using var resource = typeof(StarTruckerDisplay).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("ملفات الخط العربي مفقودة من إصدار Locara.");
        var bytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(bytes);
        return bytes;
    }

    [GeneratedRegex("[\\u0600-\\u06ff\\u0750-\\u077f\\u0870-\\u08ff\\ufb50-\\ufdff\\ufe70-\\ufefc]")]
    private static partial Regex ArabicLetters();
    [GeneratedRegex(@"^</?([A-Za-z][A-Za-z0-9_-]*)\b[^>]*>$")]
    private static partial Regex XmlTagName();
    [GeneratedRegex(@"^\[(?<close>/)?(?<name>[a-z][a-z0-9_-]*)(?:=[^\]\r\n]+)?\]$", RegexOptions.CultureInvariant)]
    private static partial Regex BbCodeTagName();
    [GeneratedRegex(@"(\r\n|\n|<br\s*/?>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakPattern();

    [DllImport("icu.dll", EntryPoint = "u_shapeArabic", ExactSpelling = true)]
    private static extern int ShapeArabic([MarshalAs(UnmanagedType.LPWStr)] string text, int length,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] output, int capacity,
        uint options, ref int error);
    [DllImport("icu.dll", EntryPoint = "ubidi_open", ExactSpelling = true)]
    private static extern nint OpenBidi();
    [DllImport("icu.dll", EntryPoint = "ubidi_setPara", ExactSpelling = true)]
    private static extern void SetParagraph(nint layout, nint text, int length, byte direction, nint levels, ref int error);
    [DllImport("icu.dll", EntryPoint = "ubidi_writeReordered", ExactSpelling = true)]
    private static extern int WriteDisplay(nint layout,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] output, int capacity,
        ushort options, ref int error);
    [DllImport("icu.dll", EntryPoint = "ubidi_close", ExactSpelling = true)]
    private static extern void CloseBidi(nint layout);
}
