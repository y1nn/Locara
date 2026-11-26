using System.Text;
using System.Text.RegularExpressions;

namespace Locara.Core;

/// <summary>
/// Universal Unity TextAsset (classId = 49) extractor and rewriter for standalone
/// resources.assets / sharedassets*.assets files and data.unity3d archives.
/// Detects structured embedded formats (JSON, XML, CSV, TSV, PO, INI, YAML) as well as
/// human-readable TXT assets, filters out binary/shader/technical assets via Layer 1 TextQualityClassifier,
/// and integrates with UnityTmpArabicPatcher + UnityArabicDisplayOptions.
/// </summary>
public sealed partial class UnityAssetsFormat : IFormatHandler
{
    public static UnityAssetsFormat Instance { get; } = new();
    public string Name => "unity-assets";

    private static readonly JsonFormat Json = new();
    private static readonly XmlFormat Xml = new();
    private static readonly DelimitedFormat Csv = new(',');
    private static readonly DelimitedFormat Tsv = new('\t');
    private static readonly PoFormat Po = new();
    private static readonly IniFormat Ini = new();
    private static readonly YamlFormat Yaml = new();

    public bool Supports(string path)
    {
        string file = Path.GetFileName(path);
        return file.Equals("resources.assets", StringComparison.OrdinalIgnoreCase)
            || file.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase)
            || SharedAssetsNamePattern().IsMatch(file);
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        if (UnityFsBundle.IsUnityFs(bytes))
        {
            var bundle = UnityFsBundle.Read(bytes);
            var slots = new List<TextSlot>();
            foreach (var node in bundle.Nodes)
            {
                if ((node.Flags & 4) == 0 || !IsCandidateNodeName(node.Name)) continue;
                ExtractFromSerializedBytes(node.Name, node.Bytes, slots);
            }
            return slots;
        }
        var result = new List<TextSlot>();
        ExtractFromSerializedBytes("self", bytes, result);
        return result;
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations) =>
        ApplyWithDisplay(bytes, translations, fontChoice: null, displayOptions: null, applyShaping: false, out _);

    internal static byte[] ApplyWithDisplay(byte[] bytes, IReadOnlyDictionary<string, string> translations,
        ArabicFontChoice? fontChoice, UnityArabicDisplayOptions? displayOptions, bool applyShaping,
        out bool arabicFontPatched)
    {
        arabicFontPatched = false;
        if (translations.Count == 0) return bytes;

        if (UnityFsBundle.IsUnityFs(bytes))
        {
            var bundle = UnityFsBundle.Read(bytes);
            var updatedNodes = new List<UnityFsNode>(bundle.Nodes.Count);
            bool anyChanged = false;
            foreach (var node in bundle.Nodes)
            {
                if ((node.Flags & 4) != 0 && IsCandidateNodeName(node.Name))
                {
                    byte[] rewritten = ApplyToSerializedBytes(node.Name, node.Bytes, translations, fontChoice, displayOptions, applyShaping, out bool patchedHere);
                    if (patchedHere) arabicFontPatched = true;
                    if (!ReferenceEquals(rewritten, node.Bytes)) anyChanged = true;
                    updatedNodes.Add(node with { Bytes = rewritten });
                }
                else
                {
                    updatedNodes.Add(node);
                }
            }
            return anyChanged ? bundle.Write(updatedNodes, out _) : bytes;
        }

        return ApplyToSerializedBytes("self", bytes, translations, fontChoice, displayOptions, applyShaping, out arabicFontPatched);
    }

    internal static bool HasTranslatableTextAssets(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 256) return false;
            return ExtractFromFile(filePath).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static IReadOnlyList<TextSlot> ExtractFromFile(string filePath, Action<Diagnostic>? onDiagnostic = null)
    {
        string file = Path.GetFileName(filePath);
        var slots = new List<TextSlot>();
        if (file.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string nodeName in UnityFsStream.ListSerializedNodeNames(filePath))
            {
                if (!IsCandidateNodeName(nodeName)) continue;
                byte[]? nodeBytes = UnityFsStream.TryReadNode(filePath, nodeName);
                if (nodeBytes != null)
                    ExtractFromSerializedBytes(nodeName, nodeBytes, slots, onDiagnostic);
            }
            return slots;
        }

        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length > 128L * 1024 * 1024) return [];
        ExtractFromSerializedBytes("self", File.ReadAllBytes(filePath), slots, onDiagnostic);
        return slots;
    }

    internal static void ApplyToFile(string sourceFilePath, string destinationFilePath,
        IReadOnlyDictionary<string, string> translations, ArabicFontChoice? fontChoice,
        UnityArabicDisplayOptions? displayOptions, bool applyShaping,
        CancellationToken cancellation, out bool arabicFontPatched)
    {
        arabicFontPatched = false;
        string file = Path.GetFileName(sourceFilePath);
        if (file.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase))
        {
            var nodesToPatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string locator in translations.Keys)
            {
                if (TryParseLocator(locator, out string nodeName, out _, out _, out _))
                    nodesToPatch.Add(nodeName);
            }
            if (nodesToPatch.Count == 0) return;

            bool anyArabicInFile = translations.Values.Any(ArabicTextEngine.HasArabic);
            if (anyArabicInFile && !nodesToPatch.Contains("resources.assets")
                && UnityFsStream.ListSerializedNodeNames(sourceFilePath)
                    .Any(n => n.Equals("resources.assets", StringComparison.OrdinalIgnoreCase)))
            {
                nodesToPatch.Add("resources.assets");
            }

            string currentSource = sourceFilePath;
            string? intermediate = null;
            try
            {
                var orderedNodes = nodesToPatch.ToList();
                for (int i = 0; i < orderedNodes.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string nodeName = orderedNodes[i];
                    byte[] nodeBytes = UnityFsStream.TryReadNode(currentSource, nodeName)
                        ?? throw new InvalidDataException($"لم يُعثر على العقدة {nodeName} داخل data.unity3d.");
                    bool allowTmpOnly = anyArabicInFile && nodeName.Equals("resources.assets", StringComparison.OrdinalIgnoreCase);
                    byte[] rewritten = ApplyToSerializedBytes(nodeName, nodeBytes, translations, fontChoice, displayOptions, applyShaping, out bool patchedHere, allowTmpOnly);
                    if (patchedHere) arabicFontPatched = true;
                    if (ReferenceEquals(rewritten, nodeBytes)) continue;

                    string nextDest = destinationFilePath + $".step{i}.tmp";
                    UnityFsStream.PatchNode(currentSource, nextDest, nodeName, rewritten, cancellation);
                    if (intermediate != null && File.Exists(intermediate)) File.Delete(intermediate);
                    intermediate = nextDest;
                    currentSource = nextDest;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
                if (intermediate != null)
                {
                    File.Move(intermediate, destinationFilePath, overwrite: true);
                    intermediate = null;
                }
                else
                {
                    File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
                }
            }
            finally
            {
                if (intermediate != null && File.Exists(intermediate))
                    try { File.Delete(intermediate); } catch (IOException) { }
            }
            return;
        }

        byte[] orig = File.ReadAllBytes(sourceFilePath);
        byte[] updated = ApplyToSerializedBytes("self", orig, translations, fontChoice, displayOptions, applyShaping, out arabicFontPatched);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        File.WriteAllBytes(destinationFilePath, updated);
    }

    private static bool IsCandidateNodeName(string nodeName) =>
        nodeName.Equals("resources.assets", StringComparison.OrdinalIgnoreCase)
        || SharedAssetsNamePattern().IsMatch(nodeName);

    private static void ExtractFromSerializedBytes(string nodeName, byte[] serializedBytes, List<TextSlot> slots,
        Action<Diagnostic>? onDiagnostic = null)
    {
        var sf = UnitySerializedFile.TryRead(serializedBytes);
        if (sf == null) return;

        foreach (var obj in sf.Objects)
        {
            if (!sf.IsTextAssetType(obj.TypeIndex) || obj.ByteSize < 16 || obj.ByteSize > 2 * 1024 * 1024)
                continue;
            var ta = UnityBundleFormat.ParseTextAsset(sf.Slice(obj));
            if (ta == null || !IsCandidateTextAsset(ta.Name, ta.Text)) continue;

            var (kind, innerSlots) = ExtractEmbeddedSlots(ta.Name, ta.Text, onDiagnostic);
            if (innerSlots == null || innerSlots.Count == 0) continue;

            // Apply Layer 1 TextQualityClassifier per TextAsset so shader/code/config JSONs are automatically filtered out!
            var quality = TextQualityClassifier.ClassifyFile(ta.Name, innerSlots.Select(s => s.Source).ToArray());
            if (quality.Verdict == FileQualityVerdict.Technical) continue;

            foreach (var inner in innerSlots)
            {
                string locator = MakeLocator(nodeName, obj.PathId, kind, inner.Locator);
                string context = string.IsNullOrWhiteSpace(inner.Context)
                    ? $"TextAsset: {ta.Name}"
                    : $"{ta.Name} · {inner.Context}";
                slots.Add(new TextSlot(locator, inner.Source, context, inner.ExistingTarget));
            }
        }
    }

    private static byte[] ApplyToSerializedBytes(string nodeName, byte[] serializedBytes,
        IReadOnlyDictionary<string, string> translations, ArabicFontChoice? fontChoice,
        UnityArabicDisplayOptions? displayOptions, bool applyShaping, out bool arabicFontPatched,
        bool allowTmpOnly = false)
    {
        arabicFontPatched = false;
        var sf = UnitySerializedFile.TryRead(serializedBytes);
        if (sf == null) return serializedBytes;

        var grouped = new Dictionary<long, (string Kind, Dictionary<string, string> Inner)>();
        bool anyArabic = false;
        foreach (var (locator, target) in translations)
        {
            if (!TryParseLocator(locator, out string locNode, out long pathId, out string kind, out string innerLocator))
                continue;
            if (!locNode.Equals(nodeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (ArabicTextEngine.HasArabic(target)) anyArabic = true;
            if (!grouped.TryGetValue(pathId, out var entry))
            {
                entry = (kind, new Dictionary<string, string>(StringComparer.Ordinal));
                grouped[pathId] = entry;
            }
            entry.Inner[innerLocator] = target;
        }

        if (grouped.Count == 0 && !allowTmpOnly) return serializedBytes;

        var opts = displayOptions ?? UnityArabicDisplayOptions.Default;
        var replacements = new Dictionary<long, byte[]>();
        if (anyArabic || allowTmpOnly)
            arabicFontPatched = UnityTmpArabicPatcher.TryPatch(sf, fontChoice, replacements, opts.FontScale, opts.ContrastBoost);

        bool shouldShape = (arabicFontPatched || applyShaping) && opts.ShapeArabic;
        bool anyTextChanged = false;

        foreach (var obj in sf.Objects)
        {
            if (!grouped.TryGetValue(obj.PathId, out var edit) || !sf.IsTextAssetType(obj.TypeIndex))
                continue;
            var ta = UnityBundleFormat.ParseTextAsset(sf.Slice(obj));
            if (ta == null) continue;

            var effectiveMap = new Dictionary<string, string>(edit.Inner.Count, StringComparer.Ordinal);
            foreach (var (k, v) in edit.Inner)
            {
                effectiveMap[k] = shouldShape && ArabicTextEngine.HasArabic(v)
                    ? (edit.Kind == "ini" ? ArabicTextEngine.Shape(v) : opts.TransformArabic(v))
                    : v;
            }

            string updatedText = ApplyEmbeddedTranslations(edit.Kind, ta.Text, effectiveMap);
            if (updatedText != ta.Text)
            {
                replacements[obj.PathId] = UnityBundleFormat.RewriteTextAsset(ta, updatedText);
                anyTextChanged = true;
            }
        }

        if (!anyTextChanged && !(allowTmpOnly && arabicFontPatched))
        {
            arabicFontPatched = false;
            return serializedBytes;
        }
        return sf.Rewrite(replacements);
    }

    private static (string Kind, IReadOnlyList<TextSlot>? Slots) ExtractEmbeddedSlots(string name, string text,
        Action<Diagnostic>? onDiagnostic = null)
    {
        string trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        // 1. JSON
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var slots = Json.Extract(utf8);
                if (slots.Count > 0) return ("json", slots);
            }
            catch (Exception ex) { onDiagnostic?.Invoke(new($"TextAsset:{name}", ex.Message)); }
        }

        // 2. XML
        if (trimmed.StartsWith('<') && trimmed.Contains('>'))
        {
            try
            {
                var slots = Xml.Extract(utf8);
                if (slots.Count > 0) return ("xml", slots);
            }
            catch (Exception ex) { onDiagnostic?.Invoke(new($"TextAsset:{name}", ex.Message)); }
        }

        // 3. PO / Gettext
        if (trimmed.Contains("msgid \"", StringComparison.Ordinal) && trimmed.Contains("msgstr \"", StringComparison.Ordinal))
        {
            try
            {
                var slots = Po.Extract(utf8);
                if (slots.Count > 0) return ("po", slots);
            }
            catch (Exception ex) { onDiagnostic?.Invoke(new($"TextAsset:{name}", ex.Message)); }
        }

        // 4. TSV / CSV (multi-line tabular with recognizable source column header)
        if (trimmed.Contains('\n'))
        {
            string firstLine = trimmed[..trimmed.IndexOf('\n')];
            if (firstLine.Contains('\t'))
            {
                try
                {
                    var slots = Tsv.Extract(utf8);
                    if (slots.Count > 0) return ("tsv", slots);
                }
                catch (Exception ex) { onDiagnostic?.Invoke(new($"TextAsset:{name}", ex.Message)); }
            }
            if (firstLine.Contains(','))
            {
                try
                {
                    var slots = Csv.Extract(utf8);
                    if (slots.Count > 0) return ("csv", slots);
                }
                catch (Exception ex) { onDiagnostic?.Invoke(new($"TextAsset:{name}", ex.Message)); }
            }
        }

        // 5. INI (must contain at least one [Section] header or multiple key=value lines)
        if (IniSectionOrKeyPattern().IsMatch(trimmed))
        {
            try
            {
                var slots = Ini.Extract(utf8);
                if (slots.Count >= 2) return ("ini", slots);
            }
            catch (Exception ex) { onDiagnostic?.Invoke(new($"TextAsset:{name}", ex.Message)); }
        }

        // 6. YAML (multi-line key: value mappings)
        if (trimmed.Contains('\n') && trimmed.Contains(": ", StringComparison.Ordinal) && !trimmed.StartsWith('{'))
        {
            try
            {
                var slots = Yaml.Extract(utf8);
                if (slots.Count >= 2) return ("yaml", slots);
            }
            catch (Exception ex) { onDiagnostic?.Invoke(new($"TextAsset:{name}", ex.Message)); }
        }

        // 7. Plain human-readable TXT (e.g. story letter, dialogue script, book page)
        if (LooksLikeCodeOrShader(trimmed)) return ("raw", null);
        if (TextQualityClassifier.ClassifyString(trimmed) == StringQuality.Human)
            return ("raw", [new TextSlot("raw", text, $"TextAsset: {name}")]);

        return ("raw", null);
    }

    private static string ApplyEmbeddedTranslations(string kind, string originalText, IReadOnlyDictionary<string, string> translations)
    {
        if (kind == "raw")
            return translations.TryGetValue("raw", out string? replacement) ? replacement : originalText;

        byte[] utf8 = Encoding.UTF8.GetBytes(originalText);
        IFormatHandler? handler = kind switch
        {
            "json" => Json,
            "xml" => Xml,
            "csv" => Csv,
            "tsv" => Tsv,
            "po" => Po,
            "ini" => Ini,
            "yaml" => Yaml,
            _ => null
        };
        if (handler == null) return originalText;
        byte[] updated = handler.Apply(utf8, translations);
        return Encoding.UTF8.GetString(updated);
    }

    private static bool IsCandidateTextAsset(string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2 || text.Length > 1_500_000 || text.Contains('\0'))
            return false;
        if (IgnoredAssetNamePattern().IsMatch(name))
            return false;
        foreach (char c in text)
        {
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
                return false;
        }
        return true;
    }

    private static bool LooksLikeCodeOrShader(string text) =>
        CodeOrShaderPattern().IsMatch(text);

    internal static string MakeLocator(string nodeName, long pathId, string kind, string innerLocator) =>
        $"uta:{nodeName.Length}:{nodeName}:{pathId}:{kind}:{innerLocator}";

    internal static bool TryParseLocator(string locator, out string nodeName, out long pathId, out string kind, out string innerLocator)
    {
        nodeName = "";
        pathId = 0;
        kind = "";
        innerLocator = "";
        const string prefix = "uta:";
        if (!locator.StartsWith(prefix, StringComparison.Ordinal)) return false;

        int c1 = locator.IndexOf(':', prefix.Length);
        if (c1 < 0 || !int.TryParse(locator.AsSpan(prefix.Length, c1 - prefix.Length), out int nodeLen) || nodeLen <= 0)
            return false;

        int nodeStart = c1 + 1;
        if (nodeStart + nodeLen + 1 >= locator.Length || locator[nodeStart + nodeLen] != ':')
            return false;
        nodeName = locator.Substring(nodeStart, nodeLen);

        int idStart = nodeStart + nodeLen + 1;
        int c2 = locator.IndexOf(':', idStart);
        if (c2 < 0 || !long.TryParse(locator.AsSpan(idStart, c2 - idStart), out pathId))
            return false;

        int kindStart = c2 + 1;
        int c3 = locator.IndexOf(':', kindStart);
        if (c3 < 0) return false;
        kind = locator.Substring(kindStart, c3 - kindStart);
        innerLocator = locator[(c3 + 1)..];
        return kind.Length > 0 && innerLocator.Length > 0;
    }

    [GeneratedRegex(@"^sharedassets\d+\.assets$", RegexOptions.IgnoreCase)]
    private static partial Regex SharedAssetsNamePattern();

    [GeneratedRegex(@"(?:^|[._-])(?:shader|font|atlas|sdf|license|licence|notice|third.?party|changelog|readme|version|manifest|assembly|metadata|guid|tmp_|fallback|sprite|material|preset|curve|gradient|lut|noise|terrain|navmesh|occlusion|lightmap|prefab|scene|inputactions|keymap|keybind|steam_appid|link|boot|settings|config|schema|cert|pem|key|crt)$", RegexOptions.IgnoreCase)]
    private static partial Regex IgnoredAssetNamePattern();

    [GeneratedRegex(@"^\s*(?:\[[^\]\r\n]+\]\s*\r?\n[A-Za-z0-9_.-]+\s*=|[A-Za-z0-9_.-]+\s*=\s*.+\r?\n[A-Za-z0-9_.-]+\s*=)")]
    private static partial Regex IniSectionOrKeyPattern();

    [GeneratedRegex(@"(?:\b(?:void|float4|half4|fixed4|sampler2D|cbuffer|struct\s+\w+|#pragma|#include|Shader\s+""|SubShader|CGPROGRAM|HLSLPROGRAM|using\s+System|namespace\s+\w+|public\s+class|function\s+\w+\s*\(|<!DOCTYPE\s+html|<html\b)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeOrShaderPattern();
}
