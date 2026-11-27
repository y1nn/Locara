namespace Locara.Core;

/// <summary>
/// Declarative capability metadata for a localization format provider,
/// replacing scattered <c>is &lt;Format&gt;</c> and <c>format == "..."</c> checks across services.
/// </summary>
public sealed record FormatMetadata(
    string Name,
    bool IsDedicatedLocalization,
    bool IsOpenDataFormat,
    bool IsConfigurable,
    bool UsesStreamedFileAccess,
    string? DedicatedScanReason = null);

public sealed record FormatProbeResult(
    bool Supported,
    IFormatHandler? ResolvedHandler = null,
    string? Diagnostic = null);

/// <summary>
/// Rich localization resource provider contract (Core v2) unifying extension matching,
/// content-level binary probing, streamed disk extraction, and declarative format metadata.
/// </summary>
public interface ILocalizationProvider
{
    FormatMetadata Metadata { get; }
    IFormatHandler Handler { get; }
    bool MatchesPath(string relativePath);
    FormatProbeResult Probe(string relativePath, string? fullDiskPath, byte[]? unrealAesKey = null);
    IReadOnlyList<TextSlot> ExtractFromDisk(string fullDiskPath, byte[]? unrealAesKey = null,
        Action<Diagnostic>? onDiagnostic = null, CancellationToken cancellation = default);
}

internal sealed class StandardFormatProvider(
    IFormatHandler handler,
    FormatMetadata metadata,
    Func<string, string?, byte[]?, FormatProbeResult>? customProbe = null,
    Func<string, byte[]?, Action<Diagnostic>?, CancellationToken, IReadOnlyList<TextSlot>>? diskExtractor = null) : ILocalizationProvider
{
    public FormatMetadata Metadata { get; } = metadata;
    public IFormatHandler Handler { get; } = handler;

    public bool MatchesPath(string relativePath) => Handler.Supports(relativePath);

    public FormatProbeResult Probe(string relativePath, string? fullDiskPath, byte[]? unrealAesKey = null)
    {
        if (!MatchesPath(relativePath)) return new(false);
        if (customProbe != null) return customProbe(relativePath, fullDiskPath, unrealAesKey);
        return new(true, Handler);
    }

    public IReadOnlyList<TextSlot> ExtractFromDisk(string fullDiskPath, byte[]? unrealAesKey = null,
        Action<Diagnostic>? onDiagnostic = null, CancellationToken cancellation = default)
    {
        if (diskExtractor != null) return diskExtractor(fullDiskPath, unrealAesKey, onDiagnostic, cancellation);
        return Handler.Extract(File.ReadAllBytes(fullDiskPath));
    }
}

/// <summary>
/// Single source of truth for all localization format providers and their metadata.
/// </summary>
public static class LocalizationProviderRegistry
{
    public static IReadOnlyList<ILocalizationProvider> All { get; } = BuildProviders();

    private static readonly Dictionary<string, ILocalizationProvider> ByName = BuildByName();

    private static Dictionary<string, ILocalizationProvider> BuildByName()
    {
        var map = new Dictionary<string, ILocalizationProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in All)
            map.TryAdd(p.Metadata.Name, p);
        // Legacy StarTrucker catalog metadata
        map.TryAdd(StarTruckerCatalog.Instance.Name, new StandardFormatProvider(
            StarTruckerCatalog.Instance,
            new FormatMetadata(StarTruckerCatalog.Instance.Name, IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: true, UsesStreamedFileAccess: false)));
        return map;
    }

    public static bool TryGetByName(string? formatName, out ILocalizationProvider provider)
    {
        if (!string.IsNullOrEmpty(formatName) && ByName.TryGetValue(formatName, out var found))
        {
            provider = found;
            return true;
        }
        provider = null!;
        return false;
    }

    public static string? GetScanReason(string resourcePath, string formatName)
    {
        if (formatName == "po" && LocaleClassifier.IsGettextTemplate(resourcePath))
            return "قالب الترجمة الرئيسي (Gettext POT) — نصوص اللعبة الرسمية جاهزة للترجمة.";
        return TryGetByName(formatName, out var p) ? p.Metadata.DedicatedScanReason : null;
    }

    private static IReadOnlyList<ILocalizationProvider> BuildProviders()
    {
        var json = new JsonFormat();
        var csv = new DelimitedFormat(',');
        var tsv = new DelimitedFormat('\t');
        var xml = new XmlFormat();
        var ini = new IniFormat();
        var yaml = new YamlFormat();
        var po = new PoFormat();
        var xlsx = new SpreadsheetFormat();
        var renpy = new RenpyFormat();
        var bundle = new UnityBundleFormat();

        return
        [
            new StandardFormatProvider(json, new("json", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(csv, new("csv", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(tsv, new("tsv", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(xml, new("xml/xliff", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false),
                customProbe: (_, fullPath, _) =>
                {
                    if (fullPath != null && File.Exists(fullPath) && Containers.IsArchive(fullPath))
                        return new(false);
                    return new(true, xml);
                }),
            new StandardFormatProvider(ini, new("ini", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(yaml, new("yaml", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(TomlFormat.Instance, new("toml", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(StringsFormat.Instance, new("strings", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: false,
                DedicatedScanReason: "ملف ترجمة قياسي (.strings) — يحافظ على ترميز UTF-8 / UTF-16 والتعليقات الأصلية.")),
            new StandardFormatProvider(LuaFormat.Instance, new("lua", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(po, new("po", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(xlsx, new("xlsx", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: true, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(renpy, new("renpy-template", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: false)),
            new StandardFormatProvider(bundle, new("unity-bundle", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: false),
                customProbe: (rel, fullPath, _) =>
                {
                    if (fullPath == null) return new(true, bundle);
                    if (!UnityBundleFormat.IsCandidateBundlePath(rel)) return new(false);
                    return UnityBundleFormat.HasTranslatableContent(fullPath) ? new(true, bundle) : new(false);
                }),
            new StandardFormatProvider(UnityI2Format.Instance, new("unity-i2", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: true,
                DedicatedScanReason: "نصوص اللعبة الرئيسية (I2 Localization) — يُحقن الخط العربي وتشكيل الحروف تلقائياً عند البناء."),
                customProbe: (_, fullPath, _) =>
                {
                    if (fullPath == null) return new(true, UnityI2Format.Instance);
                    if (UnityI2Format.HasI2Languages(fullPath)) return new(true, UnityI2Format.Instance);
                    // Fallback seamlessly to Unity TextAsset extraction when data.unity3d / resources.assets has no I2 table
                    return UnityAssetsFormat.HasTranslatableTextAssets(fullPath)
                        ? new(true, UnityAssetsFormat.Instance)
                        : new(false);
                },
                diskExtractor: (fullPath, _, _, _) => UnityI2Format.ExtractFromFile(fullPath)),
            new StandardFormatProvider(UnityAssetsFormat.Instance, new("unity-assets", IsDedicatedLocalization: false, IsOpenDataFormat: true, IsConfigurable: false, UsesStreamedFileAccess: true,
                DedicatedScanReason: "نصوص مدمجة داخل موارد Unity (TextAsset) — مُفلترة تلقائياً عبر حارس الجودة مع حقن الخط العربي الاحتياطي (TextMeshPro) عند توفره."),
                customProbe: (_, fullPath, _) =>
                {
                    if (fullPath == null) return new(true, UnityAssetsFormat.Instance);
                    return UnityAssetsFormat.HasTranslatableTextAssets(fullPath)
                        ? new(true, UnityAssetsFormat.Instance)
                        : new(false);
                },
                diskExtractor: (fullPath, _, onDiag, _) => UnityAssetsFormat.ExtractFromFile(fullPath, onDiag)),
            new StandardFormatProvider(GameMakerDataWinFormat.Instance, new("gamemaker-datawin", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: true,
                DedicatedScanReason: "مقطع نصوص GameMaker الثنائي (STRG داخل data.win / game.unx / game.ios) — مُفلتر هيكلياً لحماية أسماء الأصول والـ Shaders مع إعادة بناء آمنة ونسخة احتياطية SHA-256."),
                customProbe: (_, fullPath, _) =>
                {
                    if (fullPath == null) return new(true, GameMakerDataWinFormat.Instance);
                    return GameMakerDataWinFormat.HasTranslatableStrings(fullPath)
                        ? new(true, GameMakerDataWinFormat.Instance)
                        : new(false);
                },
                diskExtractor: (fullPath, _, _, _) => GameMakerDataWinFormat.ExtractFromFile(fullPath)),
            new StandardFormatProvider(UnrealLocresFormat.Instance, new("unreal-locres", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: false,
                DedicatedScanReason: "ملف ترجمة Unreal Engine ثنائي (.locres) — يدعم العربية بترميز UTF-16LE أصلياً.")),
            new StandardFormatProvider(UnrealStringTableFormat.Instance, new("unreal-stringtable", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: true,
                DedicatedScanReason: "جدول نصوص Unreal Engine ثنائي (StringTable .uasset) — يُحدّث بترميز UTF-16LE للعربية مع مزامنة حجم الحزمة."),
                customProbe: (_, fullPath, _) =>
                {
                    if (fullPath == null) return new(true, UnrealStringTableFormat.Instance);
                    return UnrealStringTableFormat.HasStringTable(fullPath)
                        ? new(true, UnrealStringTableFormat.Instance)
                        : new(false);
                },
                diskExtractor: (fullPath, _, _, _) => UnrealStringTableFormat.ExtractFromFiles(fullPath)),
            new StandardFormatProvider(UnrealPakFormat.Instance, new("unreal-pak", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: true,
                DedicatedScanReason: "نصوص Unreal Engine (.locres / StringTable داخل .pak) — يُبنى الناتج كحزمة تعديل جاهزة دون تعديل ملفات اللعبة الأصلية."),
                customProbe: (_, fullPath, aesKey) =>
                {
                    if (fullPath == null) return new(false);
                    bool ok = UnrealPakFormat.HasTranslatableLocres(fullPath, out string? diag, aesKey);
                    return new(ok, ok ? UnrealPakFormat.Instance : null, diag);
                },
                diskExtractor: (fullPath, aesKey, onDiag, ct) => UnrealPakFormat.ExtractFromFile(fullPath, cancellation: ct, aesKey: aesKey, onDiagnostic: onDiag)),
            new StandardFormatProvider(GodotTranslationFormat.Instance, new("godot-translation", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: false,
                DedicatedScanReason: "ملف ترجمة Godot ثنائي (.translation / OptimizedTranslation) — يُعاد بناؤه بترميز UTF-8 مع الحفاظ على جدول التجزئة.")),
            new StandardFormatProvider(XUnityTranslationFormat.Instance, new("xunity-autotranslator", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: false,
                DedicatedScanReason: "ملف ترجمة XUnity.AutoTranslator (BepInEx) — مخصّص لألعاب Unity ذات النصوص المدمجة في الكود البرمجي.")),
            new StandardFormatProvider(LocalizationTextFormat.Instance, new("loc-txt", IsDedicatedLocalization: true, IsOpenDataFormat: false, IsConfigurable: false, UsesStreamedFileAccess: false,
                DedicatedScanReason: "ملف ترجمة نصي (.txt داخل مجلد اللغات) — يُحفظ بنفس البنية والتعليقات والترميز الأصلي."))
        ];
    }
}
