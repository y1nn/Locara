using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Locara.Core;

public sealed record MinedTerm(string Term, int Count, bool IsSpeaker, string Sample);

public sealed record ContextLine(string Source, string Target);

public sealed record SceneLane(int LaneId, string Section, int PreferredWorker, TeamBatch[] Batches);

/// <summary>
/// Local zero-cost proper-noun and speaker miner that inspects project batches before translation
/// so the Lore &amp; Glossary Bot can build a consistent Arabic terminology sheet in a single pass.
/// </summary>
public static partial class GameTermMiner
{
    private static readonly HashSet<string> CommonEnglishStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "This", "That", "These", "Those", "There", "Their", "They", "Them", "Then", "Than",
        "You", "Your", "Yours", "Yourself", "We", "Our", "Ours", "She", "Her", "Hers", "Him", "His",
        "Who", "What", "Where", "When", "Why", "How", "Which", "While", "With", "Without", "Within",
        "From", "Into", "Onto", "Upon", "Over", "Under", "After", "Before", "Between", "Through",
        "And", "But", "For", "Nor", "Not", "Only", "Just", "Also", "Very", "More", "Most", "Some", "Any", "All",
        "Yes", "No", "Ok", "Okay", "Cancel", "Back", "Next", "Previous", "Continue", "Confirm", "Close",
        "Start", "Play", "Resume", "Pause", "Quit", "Exit", "Save", "Load", "Delete", "Reset", "Apply",
        "Menu", "Main", "Options", "Settings", "Audio", "Video", "Graphics", "Controls", "Language", "Credits",
        "Volume", "Music", "Sound", "Effects", "Fullscreen", "Windowed", "Resolution", "Quality",
        "Press", "Hold", "Click", "Tap", "Select", "Choose", "Enter", "Space", "Shift", "Ctrl", "Alt", "Tab", "Esc",
        "New", "Game", "Level", "Chapter", "Stage", "Part", "Day", "Night", "Time", "Score", "High", "Low", "Off", "On",
        "Seek", "Talk", "Speak", "Open", "Take", "Give", "Use", "Equip", "Drop", "Buy", "Sell", "Help", "Welcome"
    };

    private static readonly HashSet<string> LeadingActionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "A", "An", "To", "In", "On", "At", "Of", "For", "With", "From", "Near", "Inside",
        "Press", "Hold", "Click", "Seek", "Talk", "Speak", "Meet", "Find", "Visit", "Enter", "Leave", "Welcome"
    };

    [GeneratedRegex(@"^(?<sp>[A-Z][A-Za-z0-9_'\-]{1,22}):\s+\S", RegexOptions.Compiled)]
    private static partial Regex LineSpeakerPrefix();

    [GeneratedRegex(@"(?:(?i:speaker)\s*=\s*(?<sp>[A-Z][A-Za-z0-9_'\-]{1,22})|^\[(?<sp>[A-Z][A-Za-z0-9_'\-]{1,22})\]$)", RegexOptions.Compiled)]
    private static partial Regex ContextSpeakerPattern();

    [GeneratedRegex(@"\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\]|<[^<>\r\n]+>", RegexOptions.Compiled)]
    private static partial Regex InlineMarkupOrPlaceholderPattern();

    [GeneratedRegex(@"\b[A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,}){1,2}\b", RegexOptions.Compiled)]
    private static partial Regex TitlePhrasePattern();

    [GeneratedRegex(@"(?<![.!?""\n\r]\s*)(?<!^)\b[A-Z][a-z]{3,18}\b", RegexOptions.Compiled)]
    private static partial Regex MidSentenceProperNounPattern();

    public static IReadOnlyList<MinedTerm> ExtractRecurringTerms(
        IReadOnlyList<TeamBatch> batches,
        IReadOnlyDictionary<string, string>? manualGlossary = null,
        int maxTerms = 40)
    {
        var ignored = manualGlossary != null
            ? new HashSet<string>(manualGlossary.Keys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var counts = new Dictionary<string, (string Canonical, int Hits, bool Speaker, string Sample)>(StringComparer.OrdinalIgnoreCase);

        void Record(string rawTerm, bool isSpeaker, string sampleLine)
        {
            string cleaned = TrimLeadingStopwords(rawTerm.Trim());
            if (cleaned.Length < 3 || cleaned.Length > 45) return;
            if (cleaned.Contains("__LOCARA_", StringComparison.Ordinal)) return;
            if (ignored.Contains(cleaned) || CommonEnglishStopwords.Contains(cleaned)) return;

            string snippet = sampleLine.Length <= 110 ? sampleLine : sampleLine[..110] + "…";
            if (counts.TryGetValue(cleaned, out var prev))
            {
                counts[cleaned] = (prev.Canonical, prev.Hits + 1, prev.Speaker || isSpeaker, prev.Sample);
            }
            else
            {
                counts[cleaned] = (cleaned, 1, isSpeaker, snippet);
            }
        }

        foreach (var batch in batches)
        {
            foreach (var row in batch.Rows)
            {
                string src = row.Source ?? "";
                if (string.IsNullOrWhiteSpace(src)) continue;

                var lineSp = LineSpeakerPrefix().Match(src);
                if (lineSp.Success)
                    Record(lineSp.Groups["sp"].Value, isSpeaker: true, src);

                if (!string.IsNullOrWhiteSpace(row.Context))
                {
                    var ctxSp = ContextSpeakerPattern().Match(row.Context.Trim());
                    if (ctxSp.Success)
                        Record(ctxSp.Groups["sp"].Value, isSpeaker: true, src);
                }

                string cleanSrc = InlineMarkupOrPlaceholderPattern().Replace(src, " ");
                var phraseSpans = new List<(int Start, int End)>();
                foreach (Match m in TitlePhrasePattern().Matches(cleanSrc))
                {
                    phraseSpans.Add((m.Index, m.Index + m.Length));
                    Record(m.Value, isSpeaker: false, src);
                }

                foreach (Match m in MidSentenceProperNounPattern().Matches(cleanSrc))
                {
                    bool insidePhrase = phraseSpans.Any(s => m.Index >= s.Start && m.Index < s.End);
                    if (!insidePhrase)
                        Record(m.Value, isSpeaker: false, src);
                }
            }
        }

        return counts.Values
            .Where(v => (v.Speaker && v.Hits >= 2) || v.Hits >= 3)
            .OrderByDescending(v => v.Speaker)
            .ThenByDescending(v => v.Hits)
            .ThenBy(v => v.Canonical, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxTerms))
            .Select(v => new MinedTerm(v.Canonical, v.Hits, v.Speaker, v.Sample))
            .ToList();
    }

    private static string TrimLeadingStopwords(string phrase)
    {
        string[] parts = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int start = 0;
        while (start < parts.Length - 1 && LeadingActionVerbs.Contains(parts[start]))
            start++;
        return start == 0 ? phrase : string.Join(' ', parts[start..]);
    }
}

/// <summary>
/// Organizes batches into contiguous scene lanes so a single Translator Bot owns a consecutive
/// run of dialogue/UI lines from start to finish rather than interleaving workers mid-conversation.
/// </summary>
public static class SceneLaneBuilder
{
    public static IReadOnlyList<SceneLane> BuildLanes(
        IReadOnlyList<TeamBatch> batches,
        int workers,
        bool fastMode,
        IReadOnlyDictionary<string, int>? preferredWorkers = null)
    {
        if (batches.Count == 0) return [];
        int effectiveWorkers = Math.Max(1, workers);
        var bySection = batches
            .GroupBy(b => b.Section, StringComparer.Ordinal)
            .Select(g => (Section: g.Key, Batches: g.OrderBy(b => b.Id).ToArray()))
            .ToList();

        var lanes = new List<SceneLane>();
        int nextLaneId = 1;

        if (effectiveWorkers == 1)
        {
            foreach (var (sec, secBatches) in bySection)
                lanes.Add(new SceneLane(nextLaneId++, sec, 1, secBatches));
            return lanes;
        }

        // Split oversized sections into contiguous chapter slices while keeping smaller sections whole.
        // In smart/team modes, enforce >= 2 batches per slice to preserve rolling scene continuity;
        // in fastMode, allow 1-batch slices when needed to saturate all requested parallel workers.
        int minBatchesPerSlice = fastMode ? 1 : 2;
        int totalBatches = batches.Count;
        double avgPerWorker = Math.Max(1.0, (double)totalBatches / effectiveWorkers);
        int nextWorker = 0;

        foreach (var (sec, secBatches) in bySection)
        {
            int maxPossibleSlices = Math.Max(1, secBatches.Length / minBatchesPerSlice);
            int sliceCount = 1;
            if (bySection.Count < effectiveWorkers)
            {
                int proportionalTarget = Math.Max(1, (int)Math.Round(secBatches.Length / Math.Max(1.0, totalBatches) * effectiveWorkers));
                sliceCount = Math.Clamp(proportionalTarget, 1, Math.Min(effectiveWorkers, maxPossibleSlices));
            }
            else if (secBatches.Length >= Math.Max(4, (int)Math.Ceiling(avgPerWorker * 1.6)) && maxPossibleSlices >= 2)
            {
                int skewedTarget = Math.Max(2, (int)Math.Round(secBatches.Length / avgPerWorker));
                sliceCount = Math.Clamp(skewedTarget, 1, Math.Min(effectiveWorkers, maxPossibleSlices));
            }

            if (sliceCount <= 1)
            {
                int worker = preferredWorkers != null && preferredWorkers.TryGetValue(sec, out int pw) && pw >= 1 && pw <= effectiveWorkers
                    ? pw
                    : ((nextWorker++) % effectiveWorkers) + 1;
                lanes.Add(new SceneLane(nextLaneId++, sec, worker, secBatches));
                continue;
            }

            int baseSize = secBatches.Length / sliceCount;
            int remainder = secBatches.Length % sliceCount;
            int offset = 0;
            for (int s = 0; s < sliceCount; s++)
            {
                int take = baseSize + (s < remainder ? 1 : 0);
                var slice = secBatches[offset..(offset + take)];
                offset += take;
                int worker = ((nextWorker++) % effectiveWorkers) + 1;
                lanes.Add(new SceneLane(nextLaneId++, sec, worker, slice));
            }
        }

        return lanes;
    }
}

/// <summary>
/// Thread-safe shared terminology memory across all active bots in a run.
/// Manual user glossary entries always remain authoritative and can never be overwritten by bots.
/// </summary>
public sealed class LiveGlossary
{
    private readonly Dictionary<string, (string Key, string Value)> manual = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Key, string Value)> bot = new(StringComparer.OrdinalIgnoreCase);

    public LiveGlossary(
        IReadOnlyDictionary<string, string>? manualGlossary = null,
        IReadOnlyDictionary<string, string>? initialBotGlossary = null)
    {
        if (manualGlossary != null)
        {
            foreach (var (k, v) in manualGlossary)
            {
                string key = (k ?? "").Trim();
                string val = (v ?? "").Trim();
                if (key.Length > 0 && val.Length > 0)
                    manual[key] = (key, val);
            }
        }
        if (initialBotGlossary != null)
            TryAddBotTerms(initialBotGlossary);
    }

    public int BotTermCount => bot.Count;

    public Dictionary<string, string> ExportBotTerms() =>
        bot.Values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> TryAddBotTerms(IReadOnlyDictionary<string, string>? candidateTerms, bool overwrite = true)
    {
        var accepted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (candidateTerms == null || candidateTerms.Count == 0) return accepted;

        foreach (var (rawKey, rawVal) in candidateTerms)
        {
            string key = (rawKey ?? "").Trim();
            string val = (rawVal ?? "").Trim();
            if (key.Length < 2 || key.Length > 80 || val.Length < 1 || val.Length > 100) continue;
            if (key.Contains("__LOCARA_", StringComparison.Ordinal) || val.Contains("__LOCARA_", StringComparison.Ordinal)) continue;
            if (manual.ContainsKey(key)) continue; // Manual user glossary is strictly authoritative
            if (!key.Any(char.IsLetter)) continue;

            if (overwrite)
            {
                bot[key] = (key, val);
                accepted[key] = val;
            }
            else if (bot.TryAdd(key, (key, val)))
            {
                accepted[key] = val;
            }
        }
        return accepted;
    }

    public Dictionary<string, string> MatchForTexts(IEnumerable<string> texts, int maxMatches = 60)
    {
        string combined = string.Join('\n', texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (combined.Length == 0) return result;

        foreach (var (_, entry) in manual)
        {
            if (ContainsWholeTerm(combined, entry.Key))
            {
                result[entry.Key] = entry.Value;
                if (result.Count >= maxMatches) return result;
            }
        }

        foreach (var (_, entry) in bot)
        {
            if (result.ContainsKey(entry.Key)) continue;
            if (ContainsWholeTerm(combined, entry.Key))
            {
                result[entry.Key] = entry.Value;
                if (result.Count >= maxMatches) return result;
            }
        }

        return result;
    }

    internal static bool ContainsWholeTerm(string haystack, string term)
    {
        if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(haystack) || haystack.Length < term.Length)
            return false;
        int idx = 0;
        while ((idx = haystack.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            int after = idx + term.Length;
            bool endOk = after >= haystack.Length || !char.IsLetterOrDigit(haystack[after]);
            if (startOk && endOk) return true;
            idx += 1;
        }
        return false;
    }
}

/// <summary>
/// Maintains a bounded multi-turn conversation history and rolling translated-line window
/// for a single contiguous scene lane.
/// </summary>
public sealed class SceneSessionState(int maxTurns = 2, int maxHistoryChars = 2400, int maxContextLines = 6)
{
    private readonly Lock sync = new();
    private readonly List<(ChatTurn User, ChatTurn Assistant)> turns = [];
    private readonly List<ContextLine> contextTail = [];

    public void SeedContextTail(IEnumerable<ContextLine>? priorLines)
    {
        if (priorLines == null) return;
        lock (sync)
        {
            contextTail.Clear();
            foreach (var line in priorLines)
            {
                if (!string.IsNullOrWhiteSpace(line.Source) && !string.IsNullOrWhiteSpace(line.Target))
                    contextTail.Add(line);
            }
            TrimContextTailUnsafe();
        }
    }

    public void RecordCompletedBatch(IReadOnlyList<ContextLine>? completedLines)
    {
        if (completedLines == null || completedLines.Count == 0) return;
        var valid = completedLines
            .Where(l => !string.IsNullOrWhiteSpace(l.Source) && !string.IsNullOrWhiteSpace(l.Target))
            .ToList();
        if (valid.Count == 0) return;

        // Build compact trailing pair from the last <= 6 lines, shrinking from the front so even dense 30-line batches fit maxHistoryChars!
        int pairBudget = Math.Max(160, Math.Min(1200, maxHistoryChars));
        var slice = valid.Count <= 6 ? valid : valid[^6..];
        string userSummary = "";
        string assistantJson = "";
        for (int start = 0; start < slice.Count; start++)
        {
            var sub = slice[start..];
            var uEntries = sub.Select((l, i) => new { id = (i + 1).ToString(CultureInfo.InvariantCulture), source = l.Source });
            var aMap = sub.Select((l, i) => (Key: (i + 1).ToString(CultureInfo.InvariantCulture), l.Target))
                .ToDictionary(x => x.Key, x => x.Target, StringComparer.Ordinal);
            string uCand = System.Text.Json.JsonSerializer.Serialize(new { entries = uEntries });
            string aCand = System.Text.Json.JsonSerializer.Serialize(aMap);
            if (uCand.Length + aCand.Length <= pairBudget)
            {
                userSummary = uCand;
                assistantJson = aCand;
                break;
            }
        }
        RecordCompletedTurn(userSummary, assistantJson, valid);
    }

    public void RecordCompletedTurn(string userSummary, string assistantJson, IEnumerable<ContextLine>? completedLines)
    {
        lock (sync)
        {
            if (!string.IsNullOrWhiteSpace(userSummary) && !string.IsNullOrWhiteSpace(assistantJson)
                && userSummary.Length + assistantJson.Length <= maxHistoryChars)
            {
                turns.Add((new ChatTurn("user", userSummary), new ChatTurn("assistant", assistantJson)));
                while (turns.Count > maxTurns || TotalTurnCharsUnsafe() > maxHistoryChars)
                {
                    if (turns.Count == 0) break;
                    turns.RemoveAt(0);
                }
            }

            if (completedLines != null)
            {
                foreach (var line in completedLines)
                {
                    if (!string.IsNullOrWhiteSpace(line.Source) && !string.IsNullOrWhiteSpace(line.Target))
                        contextTail.Add(line);
                }
                TrimContextTailUnsafe();
            }
        }
    }

    public List<ChatTurn> GetHistorySnapshot()
    {
        lock (sync)
        {
            var list = new List<ChatTurn>(turns.Count * 2);
            foreach (var (u, a) in turns)
            {
                list.Add(u);
                list.Add(a);
            }
            return list;
        }
    }

    public List<ContextLine> GetContextTail()
    {
        lock (sync) return [.. contextTail];
    }

    private int TotalTurnCharsUnsafe() =>
        turns.Sum(t => t.User.Content.Length + t.Assistant.Content.Length);

    private void TrimContextTailUnsafe()
    {
        if (contextTail.Count > maxContextLines)
            contextTail.RemoveRange(0, contextTail.Count - maxContextLines);
    }
}

/// <summary>
/// Evaluates whether a translated line in Smart Bot mode can be auto-accepted locally
/// (clean short UI label) or should be routed to the contextual Smart QA Bot.
/// </summary>
public static class SmartQaEvaluator
{
    private static readonly SearchValues<char> NarrativePunctuation =
        SearchValues.Create(['?', '!', '؟', ',', '،', '"', '“', '”', '…', ';', '\n', '\r']);

    public static bool NeedsReview(TeamRow row, string draft, IReadOnlyDictionary<string, string>? matchedTerms)
    {
        string src = (row.Source ?? "").Trim();
        string tgt = (draft ?? "").Trim();
        if (src.Length == 0) return false;
        if (tgt.Length == 0) return true;

        // 1. Any sentence/dialogue length or narrative punctuation (including period-terminated sentences) goes to Smart QA Bot
        if (src.Length > 36 || src.AsSpan().IndexOfAny(NarrativePunctuation) >= 0
            || src.EndsWith('.') || src.Contains(". ", StringComparison.Ordinal)
            || src.Contains(": ", StringComparison.Ordinal))
            return true;

        int wordCount = src.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 4) return true;

        // 2. If source contains Latin letters, auto-skip requires pure Arabic script without leftover Latin words (otherwise let QA Bot verify!)
        bool srcHasLatin = src.Any(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));
        bool tgtHasLatin = tgt.Any(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));
        if (srcHasLatin && (!ArabicTextEngine.HasArabic(tgt) || tgtHasLatin))
            return true;

        // 3. Unusual expansion ratio routes to QA Bot
        if (tgt.Length > Math.Max(24, src.Length * 3))
            return true;

        // 4. Verify matched glossary terms (allowing natural Arabic prefixes like لـ / بـ / كـ / و / ال)
        if (matchedTerms is { Count: > 0 })
        {
            foreach (var (termSrc, termAr) in matchedTerms)
            {
                if (!LiveGlossary.ContainsWholeTerm(src, termSrc)) continue;
                if (!ContainsArabicTermOrStem(tgt, termAr))
                    return true;
            }
        }

        return false;
    }

    internal static bool ContainsArabicTermOrStem(string arabicDraft, string expectedArabicTerm)
    {
        if (arabicDraft.Contains(expectedArabicTerm, StringComparison.Ordinal))
            return true;

        string[] tokens = expectedArabicTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string tok in tokens)
        {
            string stem = tok.StartsWith("ال", StringComparison.Ordinal) && tok.Length >= 4
                ? tok[2..]
                : tok;
            if (stem.Length >= 1 && !arabicDraft.Contains(stem, StringComparison.Ordinal))
                return false;
        }
        return tokens.Length > 0;
    }
}
