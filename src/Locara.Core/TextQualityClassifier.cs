using System.Text.RegularExpressions;

namespace Locara.Core;

public enum StringQuality
{
    Human,
    Neutral,
    Technical
}

public enum FileQualityVerdict
{
    Translatable,
    Mixed,
    AuxiliaryData,
    SecondaryLanguage,
    Technical
}

public sealed record FileQualitySummary(
    string Path,
    string Format,
    int TotalUnits,
    int PendingUnits,
    int TranslatedUnits,
    int ApprovedUnits,
    int HumanCount,
    int TechnicalCount,
    double TechnicalRatio,
    FileQualityVerdict Verdict,
    string Reason,
    IReadOnlyList<string> Samples,
    bool Included,
    int IncludedUnits = 0,
    bool HumanOnlyFilter = false);

public sealed record FileClassification(
    FileQualityVerdict Verdict,
    int HumanCount,
    int TechnicalCount,
    double TechnicalRatio,
    string Reason,
    IReadOnlyList<string> Samples);

public static partial class TextQualityClassifier
{
    private static readonly HashSet<string> TechnicalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".so", ".dylib", ".pdb", ".lib", ".bin", ".dat",
        ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml",
        ".bundle", ".assets", ".asset", ".prefab", ".unity", ".mat", ".shader", ".meta",
        ".pak", ".uasset", ".umap", ".locres", ".pck", ".rpa", ".rpyc", ".zip",
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".webp", ".svg", ".ico",
        ".wav", ".ogg", ".mp3", ".flac", ".mp4", ".webm", ".avi",
        ".ttf", ".otf", ".woff", ".woff2", ".fnt",
        ".cs", ".cpp", ".h", ".hpp", ".js", ".ts", ".py", ".lua", ".gd", ".rpy", ".glsl", ".hlsl"
    };

    public static StringQuality ClassifyString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return StringQuality.Neutral;
        string trimmed = text.Trim();

        // Non-ASCII natural scripts (Arabic, CJK, Cyrillic, Greek, Hebrew, Hangul, Kana, etc.) are human text.
        if (NonLatinLetterPattern().IsMatch(trimmed))
            return StringQuality.Human;

        // Strip game placeholders/tags (like {0}, [player_name], <color=#fff>, %s) to inspect the underlying text.
        string stripped = Tokens.ReplaceTokens(trimmed, _ => " ").Trim();
        if (stripped.Length == 0) return StringQuality.Neutral;

        // Pure numbers, versions, percentages, dates, or punctuation-only values (neutral unless clearly a hash/GUID).
        if (GuidPattern().IsMatch(trimmed)) return StringQuality.Technical;
        if (HexColorPattern().IsMatch(trimmed)) return StringQuality.Technical;
        if (HexHashPattern().IsMatch(trimmed)) return StringQuality.Technical;
        if (UrlOrUriPattern().IsMatch(trimmed)) return StringQuality.Technical;
        if (EmailPattern().IsMatch(trimmed)) return StringQuality.Technical;
        if (PureNumericOrPunctuationPattern().IsMatch(stripped)) return StringQuality.Neutral;

        // Inline JSON / code blobs
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}') && trimmed.Contains(':'))
            || (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Contains('"')))
            return StringQuality.Technical;

        // File / asset paths (e.g. Assets/Prefabs/UI.prefab, C:\Games\foo.dll, /Script/Engine.Actor)
        if (LooksLikePath(trimmed))
            return StringQuality.Technical;

        bool hasWhitespace = trimmed.Any(char.IsWhiteSpace);
        if (!hasWhitespace)
        {
            // File name with known technical/asset extension (e.g. Assembly-CSharp.dll, catalog.bin, hero.png)
            string ext = Path.GetExtension(trimmed);
            if (ext.Length > 1 && TechnicalExtensions.Contains(ext))
                return StringQuality.Technical;

            // Dotted namespace / qualified type / member path (e.g. Sirenix.Serialization, UnityEngine.UI.Button)
            if (LooksLikeDottedIdentifier(trimmed))
                return StringQuality.Technical;

            // Single-token underscore identifiers (snake_case, SCREAMING_SNAKE_CASE, m_FieldName)
            if (stripped.Contains('_') && AsciiIdentifierPattern().IsMatch(stripped))
                return StringQuality.Technical;

            // Single-token camelCase / PascalCase with internal lowercase->uppercase transition (e.g. InitializeRuntime, mainMenu)
            if (AsciiIdentifierPattern().IsMatch(stripped) && InternalCamelHumpPattern().IsMatch(stripped))
                return StringQuality.Technical;

            // Multi-hyphen technical slug / kebab-case (e.g. localization-string-tables-english, ui-btn-start)
            if (KebabIdentifierPattern().IsMatch(stripped))
                return StringQuality.Technical;

            // Long single opaque token / base64 / hash-like ID (no spaces, 24+ chars)
            if (stripped.Length >= 24 && OpaqueTokenPattern().IsMatch(stripped))
                return StringQuality.Technical;

            // Alphanumeric code ID like "OBJ001", "ID9842A", "v1a2b3" (mix of letters and digits in one token)
            if (stripped.Length >= 4 && AlphanumericCodePattern().IsMatch(stripped)
                && stripped.Any(char.IsLetter) && stripped.Any(char.IsDigit))
                return StringQuality.Technical;

            // Simple single word like "Start", "Back", "OK", "HP", "Options", "Play", "Alpha"
            if (SimpleHumanWordPattern().IsMatch(stripped))
                return StringQuality.Human;

            return StringQuality.Neutral;
        }

        // Multi-word text: check if it's actually a code signature or natural human prose.
        string[] words = stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return StringQuality.Neutral;

        int techWords = 0;
        int humanWords = 0;
        foreach (string w in words)
        {
            string cleanWord = w.Trim('"', '\'', ',', '.', '!', '?', ':', ';', '(', ')', '[', ']', '-', '—', '…');
            if (cleanWord.Length == 0) continue;
            if (LooksLikePath(cleanWord) || LooksLikeDottedIdentifier(cleanWord)
                || GuidPattern().IsMatch(cleanWord) || HexHashPattern().IsMatch(cleanWord)
                || UrlOrUriPattern().IsMatch(cleanWord)
                || (cleanWord.Contains('_') && AsciiIdentifierPattern().IsMatch(cleanWord))
                || (AsciiIdentifierPattern().IsMatch(cleanWord) && InternalCamelHumpPattern().IsMatch(cleanWord)))
            {
                techWords++;
            }
            else if (SimpleHumanWordPattern().IsMatch(cleanWord))
            {
                humanWords++;
            }
        }

        if (techWords > 0 && humanWords == 0)
            return StringQuality.Technical;
        if (humanWords > 0)
            return StringQuality.Human;

        return StringQuality.Neutral;
    }

    private static bool IsOpenDataFormat(string? format) =>
        string.IsNullOrEmpty(format)
        || (LocalizationProviderRegistry.TryGetByName(format, out var provider)
            ? provider.Metadata.IsOpenDataFormat
            : format is "json" or "yaml" or "toml" or "lua" or "xml/xliff" or "ini" or "csv" or "tsv" or "xlsx" or "unity-assets");

    public static StringQuality ClassifyEntry(string? locator, string? text, string? format = null)
    {
        var baseQuality = ClassifyString(text);
        if (baseQuality != StringQuality.Human || !IsOpenDataFormat(format) || string.IsNullOrWhiteSpace(locator))
            return baseQuality;

        if (FieldSemantics.ClassifyFieldKey(locator) == FieldSemanticHint.LikelyTechnical)
        {
            string trimmed = (text ?? "").Trim();
            if (NonLatinLetterPattern().IsMatch(trimmed))
                return StringQuality.Human;
            string[] words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            bool looksLikeSentence = words.Length >= 3
                || (words.Length >= 2 && trimmed.IndexOfAny(['.', '!', '?', ',', ':', ';']) >= 0);
            if (!looksLikeSentence)
                return StringQuality.Technical;
        }
        return baseQuality;
    }

    public static FileClassification ClassifyFile(string path, IReadOnlyList<string> sources) =>
        ClassifyFile(path, sources.Select(s => ("", s)).ToArray(), format: null);

    public static FileClassification ClassifyFile(string path, IReadOnlyList<(string Locator, string Source)> entries, string? format = null)
    {
        var samples = BuildSamples(entries.Select(e => e.Source).ToArray(), maxSamples: 5);
        if (entries.Count == 0)
        {
            return new(FileQualityVerdict.Technical, 0, 0, 1.0,
                "لا يحتوي هذا الملف على نصوص قابلة للترجمة.", samples);
        }

        int human = 0;
        int multiWordOrNaturalHuman = 0;
        int technical = 0;
        int neutral = 0;
        foreach (var (locator, src) in entries)
        {
            switch (ClassifyEntry(locator, src, format))
            {
                case StringQuality.Human:
                    human++;
                    string trimmed = (src ?? "").Trim();
                    if (NonLatinLetterPattern().IsMatch(trimmed) || trimmed.Any(char.IsWhiteSpace))
                        multiWordOrNaturalHuman++;
                    break;
                case StringQuality.Technical: technical++; break;
                default: neutral++; break;
            }
        }

        int classified = human + technical;
        double techRatio = classified > 0
            ? (double)technical / classified
            : (neutral == entries.Count ? 1.0 : 0.0);

        FileQualityVerdict verdict;
        string reason;
        bool singleTokenOnly = multiWordOrNaturalHuman == 0 && entries.Count >= 4 && technical > 0;

        if (classified == 0)
        {
            verdict = FileQualityVerdict.Technical;
            reason = "يحتوي قيماً رقمية أو رموزاً فقط دون نصوص بشرية.";
        }
        else if ((human == 0 && technical > 0) || (entries.Count >= 3 && techRatio >= 0.80) || (singleTokenOnly && techRatio >= 0.50))
        {
            verdict = FileQualityVerdict.Technical;
            int pct = (int)Math.Round(techRatio * 100);
            reason = $"ملف تقني/برمجي على الأرجح ({pct}% معرّفات أو مسارات أو حقول إعدادات) — لا يُنصح بترجمته.";
        }
        else if (techRatio >= 0.35 || (singleTokenOnly && techRatio >= 0.20))
        {
            verdict = FileQualityVerdict.Mixed;
            int pct = (int)Math.Round(techRatio * 100);
            reason = $"محتوى مختلط ({pct}% قيم/حقول تقنية و {100 - pct}% نصوص بشرية) — استُبعد افتراضياً لحماية اللعبة وتوفير رصيدك؛ راجع العينة أو فعّل النصوص البشرية فقط.";
        }
        else
        {
            verdict = FileQualityVerdict.Translatable;
            reason = "نصوص بشرية واضحة وجاهزة للترجمة.";
        }

        return new(verdict, human, technical, techRatio, reason, samples);
    }

    public static IReadOnlyList<string> BuildSamples(IReadOnlyList<string> sources, int maxSamples = 5)
    {
        var samples = new List<string>(maxSamples);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in sources)
        {
            if (samples.Count >= maxSamples) break;
            string singleLine = MultiWhitespacePattern().Replace((raw ?? "").Trim(), " ");
            if (singleLine.Length == 0 || !seen.Add(singleLine)) continue;
            if (singleLine.Length > 90) singleLine = singleLine[..89].TrimEnd() + "…";
            samples.Add(singleLine);
        }
        return samples;
    }

    private static bool LooksLikePath(string value)
    {
        if (value.Contains('\\') && !value.Contains(' ')) return true;
        if (WindowsDrivePathPattern().IsMatch(value)) return true;
        if (value.Contains('/') && !value.Contains(' '))
        {
            // Avoid flagging simple UI labels like "Yes/No", "On/Off", "HP/MP", "10/20"
            string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3) return true;
            if (segments.Length == 2)
            {
                string ext = Path.GetExtension(segments[^1]);
                if (ext.Length > 1 && TechnicalExtensions.Contains(ext)) return true;
                if (value.StartsWith('/') || segments[0].Contains('_') || segments[1].Contains('_')
                    || InternalCamelHumpPattern().IsMatch(segments[0]) || InternalCamelHumpPattern().IsMatch(segments[1]))
                    return true;
            }
        }
        return false;
    }

    private static bool LooksLikeDottedIdentifier(string value)
    {
        if (!value.Contains('.') || value.EndsWith('.') || value.StartsWith('.')) return false;
        string[] parts = value.Split('.');
        if (parts.Length < 2) return false;
        if (parts.Any(p => p.Length == 0 || !DottedSegmentPattern().IsMatch(p))) return false;
        // Avoid abbreviations like "U.S.A" or "A.I" where every segment is 1 letter.
        if (parts.All(p => p.Length == 1)) return false;
        // 3+ segments (System.Collections.Generic) or any segment with internal CamelCase/underscore/digits, or 2+ segments with length >= 2
        if (parts.Length >= 3) return true;
        return parts.Any(p => p.Contains('_') || p.Contains('-') || InternalCamelHumpPattern().IsMatch(p) || p.Any(char.IsDigit))
            || (parts[0].Length >= 3 && parts[1].Length >= 3);
    }

    [GeneratedRegex(@"[\u0600-\u06FF\u0750-\u077F\u08A0-\u08FF\uFB50-\uFDFF\uFE70-\uFEFF\u0400-\u04FF\u0370-\u03FF\u0590-\u05FF\u3040-\u30FF\u4E00-\u9FFF\uAC00-\uD7AF]")]
    private static partial Regex NonLatinLetterPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorPattern();

    [GeneratedRegex(@"^(?:0x)?[0-9a-fA-F]{16,}$")]
    private static partial Regex HexHashPattern();

    [GeneratedRegex(@"^(?:https?|ftp|file|steam|res|user|wss?)://\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlOrUriPattern();

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[0-9\s.,:%+\-*/=<>()\[\]{}|!?#$_~^\\/@&]+$")]
    private static partial Regex PureNumericOrPunctuationPattern();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex WindowsDrivePathPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex AsciiIdentifierPattern();

    [GeneratedRegex(@"[a-z][A-Z]")]
    private static partial Regex InternalCamelHumpPattern();

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+){2,}$")]
    private static partial Regex KebabIdentifierPattern();

    [GeneratedRegex(@"^[A-Za-z0-9+/=_-]{24,}$")]
    private static partial Regex OpaqueTokenPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex AlphanumericCodePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex DottedSegmentPattern();

    [GeneratedRegex(@"^[A-Za-zÀ-ÖØ-öø-ÿ][A-Za-zÀ-ÖØ-öø-ÿ'’-]*$")]
    private static partial Regex SimpleHumanWordPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespacePattern();
}
