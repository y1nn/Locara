using System.Text.Json;

namespace Locara.Core;

/// <summary>
/// Strongly-typed settings and metadata accessor over <see cref="ProjectStore"/>,
/// eliminating scattered string-literal setting keys across services and UI handlers.
/// </summary>
public sealed class ProjectSettings(ProjectStore store)
{
    public const string GettextPoTargetKey = "gettext_po_target";
    public const string UnrealAesKeySetting = "unreal_aes_key";

    public string Name
    {
        get => store.Get("name");
        set => store.Set("name", value);
    }

    public string Source
    {
        get => store.Get("source");
        set => store.Set("source", value);
    }

    public string Engine
    {
        get => store.Get("engine");
        set => store.Set("engine", value);
    }

    public string SourceScope => store.Get("source_scope");

    public string ScanVersion
    {
        get => store.Get("scan_version");
        set => store.Set("scan_version", value);
    }

    public void TouchScanVersion() => ScanVersion = Guid.NewGuid().ToString("N");

    public string ScannedSource
    {
        get => store.Get("scanned_source");
        set => store.Set("scanned_source", value);
    }

    public string ScannedEngine
    {
        get => store.Get("scanned_engine");
        set => store.Set("scanned_engine", value);
    }

    public string ScannedPackage
    {
        get => store.Get("scanned_package");
        set => store.Set("scanned_package", value);
    }

    public string ExtractionRevision
    {
        get => store.Get("extraction_revision");
        set => store.Set("extraction_revision", value);
    }

    public string DisplayFont
    {
        get => store.Get("display_font");
        set => store.Set("display_font", value);
    }

    public string GettextPoTarget
    {
        get => store.Get(GettextPoTargetKey).Trim();
        set => store.Set(GettextPoTargetKey, (value ?? "").Trim());
    }

    public string UnrealAesKey
    {
        get => store.Get(UnrealAesKeySetting).Trim();
        set => store.Set(UnrealAesKeySetting, (value ?? "").Trim());
    }

    public static bool IsValidUnrealAesKey(string? raw) =>
        UnrealPakCodec.TryParseAesKey(raw, out _);

    public byte[]? ResolveUnrealAesKeyBytes() =>
        UnrealPakCodec.TryParseAesKey(UnrealAesKey, out byte[]? keyBytes) ? keyBytes : null;

    public bool AutomaticOutput
    {
        get => store.Get("automatic_output") == "true";
        set => store.Set("automatic_output", value ? "true" : "false");
    }

    public bool UseSharedMemoryPrefill
    {
        get
        {
            string raw = store.Get("use_shared_memory_prefill");
            return raw.Length == 0 || raw == "true" || raw == "1";
        }
        set => store.Set("use_shared_memory_prefill", value ? "true" : "false");
    }

    public bool IncludeClassicUnityRtlPlugin
    {
        get
        {
            string raw = store.Get("unity_classic_rtl_plugin");
            return raw == "true" || raw == "1";
        }
        set => store.Set("unity_classic_rtl_plugin", value ? "true" : "false");
    }

    public UnityArabicDisplayOptions ResolveUnityDisplayOptions() =>
        UnityArabicDisplayOptions.Parse(
            store.Get("unity_arabic_wrap"),
            store.Get("unity_arabic_font_scale"),
            store.Get("unity_arabic_shaping"),
            store.Get("unity_arabic_contrast"));

    public void SaveUnityDisplayOptions(UnityArabicDisplayOptions options)
    {
        store.Set("unity_arabic_wrap", options.WrapMode);
        store.Set("unity_arabic_font_scale", options.FontScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        store.Set("unity_arabic_shaping", options.ShapeArabic ? "1" : "0");
        store.Set("unity_arabic_contrast", options.ContrastBoost ? "1" : "0");
    }

    public string RenpySdk
    {
        get => store.Get("renpy_sdk");
        set => store.Set("renpy_sdk", value ?? "");
    }

    public string GetRenpyOriginFor(string source) => store.Get("renpy-origin-for:" + source);

    public string EditorViewKey => "editor-view:" + SourceScope + ":" + ScannedPackage;

    public string GetEditorViewRaw() => store.Get(EditorViewKey);

    public void SetEditorViewRaw(string serialized) => store.Set(EditorViewKey, serialized);

    public ArabicFontChoice ResolveDisplayFont(string? customFontsDir = null) =>
        ArabicFontCatalog.Resolve(DisplayFont, customFontsDir ?? Path.Combine(LocaraPaths.UserDataDirectory, "fonts"));

    public List<Diagnostic> ReadScanErrors()
    {
        string raw = store.Get("scan_errors");
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]") return [];
        try { return JsonSerializer.Deserialize<List<Diagnostic>>(raw) ?? []; }
        catch (JsonException) { return [new Diagnostic("", raw)]; }
    }

    public void WriteScanErrors(IReadOnlyList<Diagnostic> errors) =>
        store.Set("scan_errors", JsonSerializer.Serialize(errors));

    public List<Diagnostic> ReadScanAdvisories()
    {
        string raw = store.Get("scan_advisories");
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]") return [];
        try { return JsonSerializer.Deserialize<List<Diagnostic>>(raw) ?? []; }
        catch (JsonException) { return []; }
    }

    public void WriteScanAdvisories(IReadOnlyList<Diagnostic> advisories) =>
        store.Set("scan_advisories", JsonSerializer.Serialize(advisories));

    private string PackageSettingKey => "package:" + SourceScope;
    private string PackageSpellingKey(string package) => "package-spelling:" + SourceScope + ":" + package.ToUpperInvariant();
    private string RulesKey(string resource) => "extraction:" + SourceScope + ":" + resource;
    private string FileSelectionKey(string package) => "file-selection:" + SourceScope + ":" + package;
    private string FileSelectionModeKey(string package) => "file-selection-mode:" + SourceScope + ":" + package;

    public string SelectedPackageRaw
    {
        get => store.Get(PackageSettingKey);
        set => store.Set(PackageSettingKey, value);
    }

    public string GetPackageSpelling(string package) => store.Get(PackageSpellingKey(package));
    public void SetPackageSpelling(string package, string spelling) => store.Set(PackageSpellingKey(package), spelling);

    public ExtractionRule? GetExtractionRule(string resource)
    {
        string raw = store.Get(RulesKey(resource));
        return raw.Length == 0 ? null : JsonSerializer.Deserialize<ExtractionRule>(raw);
    }

    public string GetExtractionRuleRaw(string resource) => store.Get(RulesKey(resource));

    public void SetExtractionRuleRaw(string resource, string serialized) => store.Set(RulesKey(resource), serialized);

    public Dictionary<string, bool> LoadFileSelection(string package)
    {
        string raw = store.Get(FileSelectionKey(package));
        if (raw.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(raw) is { } parsed
                ? new(parsed, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    public void SaveFileSelection(string package, Dictionary<string, bool> selection) =>
        store.Set(FileSelectionKey(package), JsonSerializer.Serialize(selection));

    public Dictionary<string, string> LoadFileSelectionModes(string package)
    {
        string raw = store.Get(FileSelectionModeKey(package));
        if (raw.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) is { } parsed
                ? new(parsed, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    public void SaveFileSelectionModes(string package, Dictionary<string, string> modes) =>
        store.Set(FileSelectionModeKey(package), JsonSerializer.Serialize(modes));
}
