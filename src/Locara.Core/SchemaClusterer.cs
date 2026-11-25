using System.Security.Cryptography;
using System.Text;

namespace Locara.Core;

public sealed record FieldClusterStat(
    string Pattern,
    string LeafKey,
    FieldSemanticHint SemanticHint,
    int TotalCount,
    int HumanCount,
    int TechnicalCount,
    IReadOnlyList<string> Samples);

public sealed record FileSchemaCluster(
    string ClusterId,
    string Format,
    string DirectoryGroup,
    int FileCount,
    int TotalUnits,
    int HumanUnits,
    int TechnicalUnits,
    FileQualityVerdict DominantVerdict,
    bool AnyIncluded,
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<FieldClusterStat> Fields,
    IReadOnlyList<string> RecommendedHumanPatterns);

/// <summary>
/// Groups scanned structured files with matching normalized field schemas so rules
/// and AI Scout analysis operate on compact schema templates rather than hundreds
/// of repetitive individual files.
/// </summary>
public static class SchemaClusterer
{
    public static IReadOnlyList<FileSchemaCluster> Build(
        IReadOnlyList<FileQualitySummary> files,
        IReadOnlyDictionary<string, IReadOnlyList<(string Locator, string Source)>> entriesByFile)
    {
        var grouped = new Dictionary<string, (string Format, string DirGroup, List<FileQualitySummary> Files)>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (file.TotalUnits == 0) continue;
            var entries = entriesByFile.GetValueOrDefault(file.Path) ?? [];
            string dirGroup = NormalizeDirectoryGroup(file.Path);
            string fingerprint = ComputeSchemaFingerprint(file.Format, dirGroup, entries);
            if (!grouped.TryGetValue(fingerprint, out var bucket))
            {
                bucket = (file.Format, dirGroup, []);
                grouped[fingerprint] = bucket;
            }
            bucket.Files.Add(file);
        }

        var clusters = new List<FileSchemaCluster>(grouped.Count);
        foreach (var (clusterId, bucket) in grouped)
        {
            var fieldAccum = new Dictionary<string, FieldAccumulator>(StringComparer.OrdinalIgnoreCase);
            int totalUnits = 0, humanUnits = 0, techUnits = 0;
            bool anyIncluded = false;
            var verdictCounts = new Dictionary<FileQualityVerdict, int>();

            foreach (var f in bucket.Files)
            {
                totalUnits += f.TotalUnits;
                humanUnits += f.HumanCount;
                techUnits += f.TechnicalCount;
                if (f.Included) anyIncluded = true;
                verdictCounts[f.Verdict] = verdictCounts.GetValueOrDefault(f.Verdict) + 1;

                var entries = entriesByFile.GetValueOrDefault(f.Path) ?? [];
                foreach (var (locator, source) in entries)
                {
                    string pattern = FieldSemantics.NormalizeLocatorPattern(locator);
                    if (pattern.Length == 0) pattern = "(root)";
                    if (!fieldAccum.TryGetValue(pattern, out var acc))
                    {
                        string leaf = FieldSemantics.ExtractLeafKey(locator);
                        acc = new FieldAccumulator(pattern, leaf, FieldSemantics.ClassifyFieldKey(locator));
                        fieldAccum[pattern] = acc;
                    }
                    var quality = TextQualityClassifier.ClassifyEntry(locator, source, f.Format);
                    acc.Add(source, quality);
                }
            }

            var fields = fieldAccum.Values
                .Select(a => a.ToStat())
                .OrderByDescending(f => f.HumanCount)
                .ThenByDescending(f => f.TotalCount)
                .ThenBy(f => f.Pattern, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var recommendedPatterns = fields
                .Where(f => f.HumanCount > 0
                    && f.SemanticHint != FieldSemanticHint.LikelyTechnical
                    && (double)f.HumanCount / Math.Max(1, f.HumanCount + f.TechnicalCount) >= 0.65)
                .Select(f => f.Pattern)
                .ToList();

            var dominantVerdict = verdictCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Select(kv => kv.Key)
                .FirstOrDefault(FileQualityVerdict.Technical);

            clusters.Add(new FileSchemaCluster(
                clusterId,
                bucket.Format,
                bucket.DirGroup,
                bucket.Files.Count,
                totalUnits,
                humanUnits,
                techUnits,
                dominantVerdict,
                anyIncluded,
                bucket.Files.Select(f => f.Path).ToList(),
                fields,
                recommendedPatterns));
        }

        return clusters
            .OrderByDescending(c => c.DominantVerdict == FileQualityVerdict.Mixed)
            .ThenByDescending(c => c.AnyIncluded)
            .ThenByDescending(c => c.TotalUnits)
            .ThenBy(c => c.ClusterId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string NormalizeDirectoryGroup(string resourcePath)
    {
        int bar = resourcePath.LastIndexOf(" | ", StringComparison.Ordinal);
        string p = (bar >= 0 ? resourcePath[(bar + 3)..] : resourcePath).Replace('\\', '/');
        string? dir = Path.GetDirectoryName(p)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(dir)) return "(root)";
        string[] segs = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Keep up to the top 3 directory segments so sibling sub-folders under the same subtree stay readable
        return segs.Length <= 3 ? string.Join('/', segs) : string.Join('/', segs[^3..]);
    }

    internal static string ComputeSchemaFingerprint(string format, string dirGroup,
        IReadOnlyList<(string Locator, string Source)> entries)
    {
        var uniquePatterns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (locator, _) in entries)
        {
            string pattern = FieldSemantics.NormalizeLocatorPattern(locator);
            if (pattern.Length > 0) uniquePatterns.Add(pattern);
            if (uniquePatterns.Count >= 64) break;
        }
        string raw = format + "\n" + dirGroup.ToLowerInvariant() + "\n" + string.Join('\n', uniquePatterns);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..8].ToLowerInvariant();
        return $"{format}:{dirGroup}#{hash}";
    }

    private sealed class FieldAccumulator(string pattern, string leafKey, FieldSemanticHint hint)
    {
        private int total, human, technical;
        private readonly List<string> samples = [];
        private readonly HashSet<string> seenSamples = new(StringComparer.Ordinal);

        public void Add(string source, StringQuality quality)
        {
            total++;
            if (quality == StringQuality.Human) human++;
            else if (quality == StringQuality.Technical) technical++;

            if (samples.Count < 4)
            {
                string clean = string.Join(' ', (source ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (clean.Length > 80) clean = clean[..79].TrimEnd() + "…";
                if (clean.Length > 0 && seenSamples.Add(clean))
                    samples.Add(clean);
            }
        }

        public FieldClusterStat ToStat() => new(pattern, leafKey, hint, total, human, technical, samples);
    }
}
