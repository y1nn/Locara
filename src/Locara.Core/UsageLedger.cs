using System.Text.RegularExpressions;

namespace Locara.Core;

public sealed record ModelUsageEntry(
    string Provider,
    string Model,
    long Requests,
    long InputTokens,
    long OutputTokens,
    long ThinkingTokens,
    long TotalTokens,
    long Characters);

public sealed record ModelPrice(double InputPerMillion, double OutputPerMillion);

public static partial class ModelPricing
{
    public static ModelPrice? Suggest(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        string m = model.Trim().ToLowerInvariant();

        if (m.Contains("gemini"))
        {
            if (m.Contains("flash-lite") || m.Contains("lite")) return new(0.10, 0.40);
            if (m.Contains("flash")) return new(0.30, 2.50);
            if (m.Contains("pro")) return new(1.25, 10.00);
            return new(0.30, 2.50);
        }
        if (m.Contains("deepseek"))
        {
            if (m.Contains("reasoner") || m.Contains("r1")) return new(0.55, 2.19);
            return new(0.27, 1.10);
        }
        if (m.Contains("claude"))
        {
            if (m.Contains("opus")) return new(15.00, 75.00);
            if (m.Contains("haiku")) return new(0.80, 4.00);
            return new(3.00, 15.00);
        }
        if (m.Contains("gpt-4o-mini") || m.Contains("gpt-4.1-mini") || m.Contains("gpt-5-mini"))
            return new(0.15, 0.60);
        if (m.Contains("gpt-4.1-nano") || m.Contains("gpt-5-nano"))
            return new(0.10, 0.40);
        if (m.Contains("gpt-4") || m.Contains("gpt-5"))
            return new(2.50, 10.00);
        if (m.Contains("o3-mini") || m.Contains("o4-mini") || m.Contains("o1-mini"))
            return new(1.10, 4.40);
        if (m.Contains("o1") || m.Contains("o3"))
            return new(10.00, 40.00);
        if (m.Contains("qwen"))
            return new(0.20, 0.60);
        if (m.Contains("llama"))
            return new(0.15, 0.40);

        return null;
    }

    public static double CalculateCost(ModelUsageEntry entry, ModelPrice price)
    {
        long billableOutput = entry.OutputTokens + entry.ThinkingTokens;
        if (billableOutput == 0 && entry.TotalTokens > entry.InputTokens)
            billableOutput = entry.TotalTokens - entry.InputTokens;
        return (entry.InputTokens / 1_000_000.0) * Math.Max(0, price.InputPerMillion)
             + (billableOutput / 1_000_000.0) * Math.Max(0, price.OutputPerMillion);
    }

    [GeneratedRegex(@"target=([^\s]+)[^\r\n]*?tok=(\d+)\s+\(in=(\d+)\s+out=(\d+)\s+think=(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex TargetLogRegex();

    [GeneratedRegex(@"=== START [^\r\n]*?(?:primary|model)=([^\s=]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StartModelRegex();

    [GeneratedRegex(@"tok=(\d+)\s+\(in=(\d+)\s+out=(\d+)\s+think=(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex GenericLogRegex();

    internal static Dictionary<string, (long Reqs, long In, long Out, long Think, long Total)> ParseLegacyLog(string logPath)
    {
        var totals = new Dictionary<string, (long Reqs, long In, long Out, long Think, long Total)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(logPath)) return totals;

        string currentModel = "unknown";
        foreach (string line in File.ReadLines(logPath))
        {
            var startMatch = StartModelRegex().Match(line);
            if (startMatch.Success)
            {
                currentModel = startMatch.Groups[1].Value.Trim();
                continue;
            }

            string model;
            long total, input, output, think;
            var targetMatch = TargetLogRegex().Match(line);
            if (targetMatch.Success)
            {
                string rawTarget = targetMatch.Groups[1].Value.Trim();
                int slash = rawTarget.IndexOf('/');
                model = slash >= 0 && slash + 1 < rawTarget.Length ? rawTarget[(slash + 1)..] : rawTarget;
                total = long.Parse(targetMatch.Groups[2].Value);
                input = long.Parse(targetMatch.Groups[3].Value);
                output = long.Parse(targetMatch.Groups[4].Value);
                think = long.Parse(targetMatch.Groups[5].Value);
            }
            else
            {
                var genericMatch = GenericLogRegex().Match(line);
                if (!genericMatch.Success) continue;
                model = currentModel;
                total = long.Parse(genericMatch.Groups[1].Value);
                input = long.Parse(genericMatch.Groups[2].Value);
                output = long.Parse(genericMatch.Groups[3].Value);
                think = long.Parse(genericMatch.Groups[4].Value);
            }

            var prev = totals.GetValueOrDefault(model);
            totals[model] = (prev.Reqs + 1, prev.In + input, prev.Out + output, prev.Think + think, prev.Total + total);
        }
        return totals;
    }
}
