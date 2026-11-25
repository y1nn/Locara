using System.Text;
using System.Text.Json;

namespace Locara.Core;

public sealed record ScoutClusterRecommendation(
    string ClusterId,
    string Action,
    IReadOnlyList<string> FieldPatterns,
    string Confidence,
    string Reason);

public sealed record ScoutPlan(
    string Engine,
    IReadOnlyList<FileSchemaCluster> AnalyzedClusters,
    IReadOnlyList<ScoutClusterRecommendation> Recommendations,
    TokenUsage Tokens,
    string Provider,
    string Model);

public sealed record ScoutApplyResult(
    int AppliedClusters,
    int IncludedFiles,
    int ExcludedFiles,
    int ConfiguredRuleFiles,
    int ActiveUnitsAfterScan);

/// <summary>
/// Layer 2 optional AI Localization Scout.
/// Sends a compact, token-budgeted structural summary of ambiguous/mixed schema clusters
/// (field names + 3-4 short samples per field, never the full game text) to the user's
/// configured AI provider and returns strictly validated extraction/inclusion recommendations.
/// </summary>
public static class LocalizationScout
{
    public const int MaxClustersPerRequest = 40;
    public const int MaxFieldsPerCluster = 16;

    private const string ScoutInstructions = """
        You are Locara's PC-game localization architect.
        You will receive a compact JSON summary of schema clusters discovered inside a PC game.
        Each cluster groups files sharing the same format, directory subtree, and normalized field patterns.
        Your task is to decide for each cluster whether it contains player-facing translatable text,
        mixed content (some player-facing fields + some technical engine/ID fields), or pure game-data/technical config.

        RULES:
        1. NEVER invent clusterId or fieldPattern values. Use ONLY the exact clusterId and pattern strings provided in the input.
        2. For each cluster, choose one action:
           - "include_all": nearly all fields are player-facing human prose (dialogue, UI labels, story, descriptions).
           - "include_fields": mixed file where ONLY specific fields are player-facing text and other fields are technical IDs/paths/enums/layout/style/prefabs. Provide the exact subset in "fieldPatterns".
           - "exclude": technical data, worldgen, shaders, references (e.g. STRINGS.* keys), internal enums, or secondary language files that must NOT be translated.
        3. When in doubt about an internal game-data key (like prefab, id, layout, style, alignment, element, biome, tag, sound), exclude that field or cluster to protect the game from crashing.
        4. Write "reason" concisely in Arabic (العربية).
        5. Return ONLY a valid JSON object matching this exact schema:
        {
          "clusters": [
            {
              "clusterId": "<exact clusterId from input>",
              "action": "include_all" | "include_fields" | "exclude",
              "fieldPatterns": ["<exact pattern from cluster.fields>"],
              "confidence": "high" | "medium" | "low",
              "reason": "<brief Arabic explanation>"
            }
          ]
        }
        """;

    public static IReadOnlyList<FileSchemaCluster> SelectCandidateClusters(
        IReadOnlyList<FileSchemaCluster> allClusters,
        bool includeTranslatable = false)
    {
        var candidates = allClusters.Where(c =>
            c.DominantVerdict != FileQualityVerdict.SecondaryLanguage
            && c.TotalUnits > 0
            && !ProjectService.IsDedicatedLocalizationFormat(c.FilePaths.FirstOrDefault() ?? "", c.Format)
            && (includeTranslatable
                || c.DominantVerdict is FileQualityVerdict.Mixed or FileQualityVerdict.AuxiliaryData
                || (c.DominantVerdict == FileQualityVerdict.Translatable && c.TechnicalUnits > 0)))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = allClusters
                .Where(c => c.DominantVerdict != FileQualityVerdict.SecondaryLanguage && c.TotalUnits > 0)
                .ToList();
        }

        return candidates
            .OrderByDescending(c => c.DominantVerdict == FileQualityVerdict.Mixed)
            .ThenByDescending(c => c.HumanUnits)
            .ThenByDescending(c => c.TotalUnits)
            .Take(MaxClustersPerRequest)
            .ToList();
    }

    public static string BuildCompactPayload(string engine, IReadOnlyList<FileSchemaCluster> clusters)
    {
        var payload = new
        {
            engine,
            clusters = clusters.Select(c => new
            {
                clusterId = c.ClusterId,
                format = c.Format,
                directory = c.DirectoryGroup,
                fileCount = c.FileCount,
                totalUnits = c.TotalUnits,
                humanUnits = c.HumanUnits,
                technicalUnits = c.TechnicalUnits,
                currentVerdict = c.DominantVerdict.ToString(),
                sampleFiles = c.FilePaths.Take(3).ToArray(),
                fields = c.Fields.Take(MaxFieldsPerCluster).Select(f => new
                {
                    pattern = f.Pattern,
                    leaf = f.LeafKey,
                    hint = f.SemanticHint.ToString(),
                    count = f.TotalCount,
                    human = f.HumanCount,
                    technical = f.TechnicalCount,
                    samples = f.Samples.Take(3).ToArray()
                }).ToArray()
            }).ToArray()
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    public static async Task<ScoutPlan> AnalyzeAsync(
        ProjectStore store,
        ProviderOptions options,
        CancellationToken cancellation = default,
        bool includeTranslatable = false,
        Func<string, string, CancellationToken, Task<StructuredResponse>>? send = null)
    {
        var service = new ProjectService(store);
        string engine = new ProjectSettings(store).ScannedEngine;
        if (string.IsNullOrWhiteSpace(engine)) engine = store.Info.Engine;

        var allClusters = service.BuildSchemaClusters();
        var candidates = SelectCandidateClusters(allClusters, includeTranslatable);
        if (candidates.Count == 0)
            return new ScoutPlan(engine, [], [], new TokenUsage(), options.Provider, options.Model);

        string data = BuildCompactPayload(engine, candidates);
        StructuredResponse response;
        if (send != null)
        {
            response = await send(ScoutInstructions, data, cancellation);
        }
        else
        {
            var provider = new ApiProvider(options, engine);
            response = await provider.RequestStructuredWithUsage(ScoutInstructions, data, cancellation);
        }

        if (response.Tokens.TotalTokens > 0 || response.Tokens.InputTokens > 0 || response.Tokens.OutputTokens > 0)
            store.RecordUsage(options.Provider, options.Model, response.Tokens, data.Length, 1);

        var recommendations = ParseAndValidateRecommendations(response.Content, candidates);
        return new ScoutPlan(engine, candidates, recommendations, response.Tokens, options.Provider, options.Model);
    }

    internal static IReadOnlyList<ScoutClusterRecommendation> ParseAndValidateRecommendations(
        string rawJson,
        IReadOnlyList<FileSchemaCluster> candidates)
    {
        var byId = candidates.ToDictionary(c => c.ClusterId, StringComparer.Ordinal);
        string cleaned = ExtractJsonObject(rawJson);
        using var doc = JsonDocument.Parse(cleaned);
        if (!doc.RootElement.TryGetProperty("clusters", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("رد كشّاف الذكاء الاصطناعي لا يحتوي مصفوفة clusters صالحة.");

        var results = new List<ScoutClusterRecommendation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string clusterId = item.TryGetProperty("clusterId", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? "" : "";
            if (!byId.TryGetValue(clusterId, out var cluster) || !seen.Add(clusterId))
                continue;

            string rawAction = item.TryGetProperty("action", out var actEl) && actEl.ValueKind == JsonValueKind.String
                ? (actEl.GetString() ?? "").Trim().ToLowerInvariant() : "exclude";
            string action = rawAction is "include_all" or "include_fields" or "exclude" ? rawAction : "exclude";

            var validPatterns = new HashSet<string>(cluster.Fields.Select(f => f.Pattern), StringComparer.OrdinalIgnoreCase);
            var validLeaves = new HashSet<string>(cluster.Fields.Select(f => f.LeafKey).Where(k => k.Length > 0), StringComparer.OrdinalIgnoreCase);
            var chosenPatterns = new List<string>();
            if (item.TryGetProperty("fieldPatterns", out var fpEl) && fpEl.ValueKind == JsonValueKind.Array)
            {
                var seenPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in fpEl.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.String) continue;
                    string pat = (p.GetString() ?? "").Trim();
                    if (pat.Length == 0 || !seenPatterns.Add(pat)) continue;
                    // Strict anti-hallucination check: pattern MUST exist in this cluster's actual schema
                    if (validPatterns.Contains(pat) || (!pat.Contains('/') && validLeaves.Contains(pat)))
                        chosenPatterns.Add(pat);
                }
            }

            if (action == "include_fields" && chosenPatterns.Count == 0)
                action = "exclude";
            if (action != "include_fields")
                chosenPatterns.Clear();

            string confidence = item.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.String
                ? (confEl.GetString() ?? "medium").Trim().ToLowerInvariant() : "medium";
            if (confidence is not ("high" or "medium" or "low")) confidence = "medium";

            string reason = item.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? (reasonEl.GetString() ?? "").Trim() : "";
            if (reason.Length == 0)
            {
                reason = action switch
                {
                    "include_all" => "ملف نصوص بشرية جاهزة للترجمة.",
                    "include_fields" => $"تضمين {chosenPatterns.Count} حقل نصي فقط وحماية الحقول التقنية.",
                    _ => "ملف إعدادات أو بيانات تقنية غير مخصّصة للترجمة."
                };
            }

            results.Add(new ScoutClusterRecommendation(clusterId, action, chosenPatterns, confidence, reason));
        }
        return results;
    }

    private static string ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidDataException("رد فارغ من كشّاف الذكاء الاصطناعي.");
        string cleaned = ApiProvider.CleanJsonPayload(raw);
        if (cleaned.Length >= 2 && cleaned[0] == '{' && cleaned[^1] == '}')
            return cleaned;
        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidDataException("تعذّر استخراج كائن JSON صالح من رد كشّاف الذكاء الاصطناعي.");
        return cleaned[start..(end + 1)];
    }
}
