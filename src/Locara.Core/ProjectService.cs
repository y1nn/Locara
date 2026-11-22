using System.Text.Json;

namespace Locara.Core;

public sealed class ProjectService(ProjectStore store)
{
    private sealed record Source(string Root, string Package)
    {
        public string Path => Package.Length == 0 ? Root : System.IO.Path.Combine(Root, Package);
        public string Key(string path) => Package.Length == 0 ? path : Package + " | " + path;
        public bool IsArchive => Containers.IsArchive(Path) && File.Exists(Path);
        public IContainerAdapter Adapter => IsArchive ? Containers.For(Path) : new FolderContainer();
        public string OutputPath => Package.Length == 0 ? System.IO.Path.GetFileName(Path) : Package;
        private bool IsGameCatalog => StarTruckerCatalog.IsGameArchive(Path) && IsArchive;
        private bool? hasArabicTemplates;
        private Dictionary<string, bool>? bundleCheckCache;
        private Dictionary<string, bool>? i2CheckCache;
        private Dictionary<string, bool>? unityAssetsCheckCache;
        private Dictionary<string, bool>? unrealPakCheckCache;
        public List<Diagnostic> FormatDiagnostics { get; } = [];

        public IFormatHandler? FormatFor(string resource)
        {
            if (IsGameCatalog)
                return StarTruckerCatalog.Instance.Supports(resource) ? StarTruckerCatalog.Instance : null;
            if (Engines.IsEngineInternalMetadata(resource))
                return null;
            hasArabicTemplates ??= GameRuntime.HasArabicTemplates(Path);
            if (hasArabicTemplates.Value && !GameRuntime.IsArabicTemplate(resource)) return null;
            var format = Formats.For(resource);
            if (format is XmlFormat && Directory.Exists(Path)
                && Containers.IsArchive(System.IO.Path.Combine(Path, resource))) return null;
            if (format is UnityBundleFormat && Directory.Exists(Path))
            {
                if (!UnityBundleFormat.IsCandidateBundlePath(resource)) return null;
                bundleCheckCache ??= new(StringComparer.OrdinalIgnoreCase);
                if (!bundleCheckCache.TryGetValue(resource, out bool hasText))
                {
                    hasText = UnityBundleFormat.HasTranslatableContent(System.IO.Path.Combine(Path, resource));
                    bundleCheckCache[resource] = hasText;
                }
                if (!hasText) return null;
            }
            if (format is UnityI2Format && Directory.Exists(Path))
            {
                i2CheckCache ??= new(StringComparer.OrdinalIgnoreCase);
                if (!i2CheckCache.TryGetValue(resource, out bool hasI2))
                {
                    hasI2 = UnityI2Format.HasI2Languages(System.IO.Path.Combine(Path, resource));
                    i2CheckCache[resource] = hasI2;
                }
                if (hasI2) return format;
                // Fall through to universal Unity TextAsset extraction when data.unity3d / resources.assets has no I2Languages!
                format = UnityAssetsFormat.Instance;
            }
            if (format is UnityAssetsFormat && Directory.Exists(Path))
            {
                unityAssetsCheckCache ??= new(StringComparer.OrdinalIgnoreCase);
                if (!unityAssetsCheckCache.TryGetValue(resource, out bool hasTextAssets))
                {
                    hasTextAssets = UnityAssetsFormat.HasTranslatableTextAssets(System.IO.Path.Combine(Path, resource));
                    unityAssetsCheckCache[resource] = hasTextAssets;
                }
                if (!hasTextAssets) return null;
            }
            if (format is UnrealPakFormat)
            {
                string fullPak = Directory.Exists(Path) ? System.IO.Path.Combine(Path, resource) : Path;
                unrealPakCheckCache ??= new(StringComparer.OrdinalIgnoreCase);
                if (!unrealPakCheckCache.TryGetValue(resource, out bool hasLocres))
                {
                    hasLocres = UnrealPakFormat.HasTranslatableLocres(fullPak, out string? diag);
                    unrealPakCheckCache[resource] = hasLocres;
                    if (!hasLocres && diag != null)
                        FormatDiagnostics.Add(new(resource, diag));
                }
                if (!hasLocres) return null;
            }
            return format;
        }
    }

    private const string LooseFiles = "loose";
    private const string ExtractionRevision = "6";

    public static string InitialSearch(string source) => "";
    private string PackageSetting => "package:" + store.Get("source_scope");
    private string PackageSpellingSetting(string package) =>
        "package-spelling:" + store.Get("source_scope") + ":" + package.ToUpperInvariant();
    private string RulesSetting(string resource) => "extraction:" + store.Get("source_scope") + ":" + resource;
    private string FileSelectionSetting(string package) =>
        "file-selection:" + store.Get("source_scope") + ":" + package;
    private string FileSelectionModeSetting(string package) =>
        "file-selection-mode:" + store.Get("source_scope") + ":" + package;

    private Dictionary<string, bool> LoadFileSelection(string package)
    {
        string raw = store.Get(FileSelectionSetting(package));
        if (raw.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(raw) is { } parsed
                ? new(parsed, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private void SaveFileSelection(string package, Dictionary<string, bool> selection) =>
        store.Set(FileSelectionSetting(package), JsonSerializer.Serialize(selection));

    private Dictionary<string, string> LoadFileSelectionModes(string package)
    {
        string raw = store.Get(FileSelectionModeSetting(package));
        if (raw.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) is { } parsed
                ? new(parsed, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private void SaveFileSelectionModes(string package, Dictionary<string, string> modes) =>
        store.Set(FileSelectionModeSetting(package), JsonSerializer.Serialize(modes));

    public void EnsureCatalogSelected()
    {
        string root = store.Info.Source;
        if (root.Length == 0 || !Directory.Exists(root)) return;
        string catalog = Path.Combine(root, StarTruckerCatalog.LanguageArchive);
        if (!File.Exists(catalog) || !Containers.IsArchive(catalog)) return;
        if (store.Get(PackageSetting) != LooseFiles && store.Get("scanned_package").Length > 0) return;

        string catalogResourceKey = StarTruckerCatalog.LanguageArchive + " | strings.xml";
        bool hasScannedCatalog = store.Query("SELECT EXISTS(SELECT 1 FROM files WHERE path=$p)",
            r => r.GetBoolean(0), ("$p", catalogResourceKey)).Single();
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            store.Set(PackageSetting, StarTruckerCatalog.LanguageArchive);
            if (hasScannedCatalog)
            {
                store.Execute("UPDATE files SET active = CASE WHEN path=$p THEN 1 ELSE 0 END", ("$p", catalogResourceKey));
                store.Execute("UPDATE units SET active = CASE WHEN path=$p THEN 1 ELSE 0 END", ("$p", catalogResourceKey));
                store.Set("scanned_source", root);
                store.Set("scanned_package", StarTruckerCatalog.LanguageArchive);
                store.Set("scanned_engine", "generic");
                store.Set("scan_errors", "[]");
                store.Set("scan_version", Guid.NewGuid().ToString("N"));
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
    }

    private Source CurrentSource(CancellationToken cancellation)
    {
        string root = store.Info.Source;
        if (root.Length == 0) throw new InvalidOperationException("اختر مصدر المشروع أولاً.");
        if (!Directory.Exists(root))
        {
            if (!File.Exists(root)) throw new FileNotFoundException("مصدر المشروع غير موجود.", root);
            if (!Containers.IsArchive(root) && Formats.For(root) == null) throw new NotSupportedException("صيغة الملف غير مدعومة.");
            return new(root, "");
        }
        EnsureCatalogSelected();
        string choice = PackageChoice(root);
        if (choice == LooseFiles) return new(root, "");
        if (choice.Length != 0) return new(root, Identity.SafePath(choice));

        string catalog = Path.Combine(root, StarTruckerCatalog.LanguageArchive);
        if (File.Exists(catalog) && Containers.IsArchive(catalog))
            return new(root, StarTruckerCatalog.LanguageArchive);

        string? mainGodotPck = FindMainGodotPck(root, cancellation);
        if (mainGodotPck != null)
            return new(root, mainGodotPck);

        using var index = new FolderContainer().Index(root, null, cancellation);
        var packages = index.Entries.Where(entry => Containers.IsArchive(Path.Combine(root, entry.Path))).ToList();
        var archivePaths = packages.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        if (index.Entries.Any(entry => !archivePaths.Contains(entry.Path)
            && !Engines.IsEngineInternalMetadata(entry.Path)
            && Formats.For(entry.Path) is { } fmt
            && (fmt is not UnityBundleFormat || (UnityBundleFormat.IsCandidateBundlePath(entry.Path)
                && UnityBundleFormat.HasTranslatableContent(Path.Combine(root, entry.Path))))
            && (fmt is not UnityI2Format || UnityI2Format.HasI2Languages(Path.Combine(root, entry.Path))
                || UnityAssetsFormat.HasTranslatableTextAssets(Path.Combine(root, entry.Path)))
            && (fmt is not UnityAssetsFormat || UnityAssetsFormat.HasTranslatableTextAssets(Path.Combine(root, entry.Path)))
            && (fmt is not UnrealPakFormat || UnrealPakFormat.HasTranslatableLocres(Path.Combine(root, entry.Path), out _))))
            return new(root, "");
        return packages.Count switch
        {
            0 => new(root, ""),
            1 => new(root, packages[0].Path),
            _ => throw new InvalidOperationException("توجد عدة حزم داخل مجلد اللعبة. اختر الحزمة التي تريد فحصها.")
        };
    }

    public IReadOnlyList<SourcePackage> DiscoverPackages(string? password = null, CancellationToken cancellation = default)
    {
        string source = store.Info.Source;
        if (!Directory.Exists(source)) return [];
        using var folder = new FolderContainer().Index(source, null, cancellation);
        return folder.Entries.Where(entry => Containers.IsArchive(Path.Combine(source, entry.Path))).Select(entry =>
        {
            string path = Path.Combine(source, entry.Path);
            var adapter = Containers.For(path);
            try
            {
                using var index = adapter.Index(path, password, cancellation);
                return new SourcePackage(entry.Path, adapter.Name, index.Encrypted ? "مشفرة • كلمة مرور الجلسة مطلوبة للقراءة" : "يمكن قراءة الفهرس وإعادة البناء");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ICSharpCode.SharpZipLib.Zip.ZipException)
            {
                return new SourcePackage(entry.Path, adapter.Name, error.Message);
            }
        }).ToList();
    }

    public IReadOnlyList<LanguageSource> Languages(CancellationToken cancellation = default)
    {
        string root = store.Info.Source;
        if (root.Length == 0 || !Directory.Exists(root)) return [];
        string folder = Path.Combine(root, StarTruckerCatalog.LanguageDirectory);
        if (!Directory.Exists(folder)) return [];
        Identity.RequireNoLinks(folder);
        var result = new List<LanguageSource>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.xml").Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Containers.IsArchive(file)) continue;
            try
            {
                using var index = new ZipContainer().Index(file, null, cancellation);
                var resource = index.Entries.FirstOrDefault(entry => StarTruckerCatalog.Instance.Supports(entry.Path));
                if (resource == null) continue;
                string sample = StarTruckerCatalog.Instance.Extract(resource.Read(cancellation))
                    .FirstOrDefault(slot => slot.Locator.Equals("id:STR_NEWGAME", StringComparison.Ordinal))?.Source ?? "";
                string code = Path.GetFileNameWithoutExtension(file);
                string language;
                try { language = System.Globalization.CultureInfo.GetCultureInfo(code).NativeName; }
                catch (System.Globalization.CultureNotFoundException) { language = code; }
                result.Add(new(code, language, Identity.SafePath(Path.GetRelativePath(root, file)), sample));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Xml.XmlException
                or ICSharpCode.SharpZipLib.Zip.ZipException) { }
        }
        return result;
    }

    private List<Diagnostic> ReadScanErrors()
    {
        string raw = store.Get("scan_errors");
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]") return [];
        try { return JsonSerializer.Deserialize<List<Diagnostic>>(raw) ?? []; }
        catch (JsonException) { return [new Diagnostic("", raw)]; }
    }

    public bool HasBlockingScanErrors()
    {
        var errors = ReadScanErrors();
        if (errors.Count == 0) return false;
        string sourceRoot = store.Get("scanned_source");
        string scannedPackage = store.Get("scanned_package");
        if (scannedPackage.Length > 0 && scannedPackage != LooseFiles)
            return true;
        bool hasActiveUnits = store.Query("SELECT EXISTS(SELECT 1 FROM units WHERE active=1)", r => r.GetBoolean(0)).Single();
        if (!hasActiveUnits) return true;
        var activeFilePaths = store.Query("SELECT DISTINCT path FROM units WHERE active=1", r => r.GetString(0))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var err in errors)
        {
            if (string.IsNullOrWhiteSpace(err.Path)
                || string.Equals(err.Path, sourceRoot, StringComparison.OrdinalIgnoreCase)
                || activeFilePaths.Contains(err.Path))
                return true;
        }
        return false;
    }

    public SourceCapability Capabilities(string? password = null, CancellationToken cancellation = default)
    {
        var source = CurrentSource(cancellation);
        using var index = Open(source, password, cancellation);
        var paths = index.Entries.Select(entry => entry.Path).ToArray();
        var profile = Engines.Resolve(store.Info.Engine, paths, source.Path);
        bool hasText = paths.Any(path => source.FormatFor(path) != null);
        bool scanned = store.Get("scanned_source") == source.Root && store.Get("scanned_package") == source.Package
            && !HasBlockingScanErrors()
            && store.Query("SELECT EXISTS(SELECT 1 FROM units WHERE active=1)", row => row.GetBoolean(0)).Single();

        if (StarTruckerCatalog.IsGameArchive(source.Path))
        {
            string? reason = source.Package == StarTruckerCatalog.LanguageArchive
                ? StarTruckerDisplay.UnsupportedReason(source.Root, source.Path, cancellation) : "يتطلب التشغيل ملف اللغة الإنجليزي للتثبيت المطابق.";
            return new("Star Trucker", reason == null ? "1.75.2.0" : "", hasText, scanned,
                reason == null ? GameLaunch.Steam : GameLaunch.None,
                reason ?? "تجربة مؤقتة عبر Steam مع استعادة الملفات.",
                reason == null ? "عرض عربي كامل لجميع نصوص اللعبة المعتمدة." : "تصدير ملف اللغة.");
        }
        if (index.GodotVersion is { } version && profile.Name is "godot" or "generic")
        {
            bool playable = paths.Contains("project.godot", StringComparer.OrdinalIgnoreCase)
                || paths.Contains("project.binary", StringComparer.OrdinalIgnoreCase);
            bool isSteamGodot = playable && Directory.Exists(source.Root) && source.Package.Length > 0
                && SteamGameTrial.FindSteamGame(source.Root) != null;
            var launch = isSteamGodot ? GameLaunch.Steam
                : (playable ? GameLaunch.GodotPack : GameLaunch.None);
            return new("Godot", string.Join('.', version), hasText, scanned, launch,
                isSteamGodot
                    ? "لعبة Godot مثبّتة عبر Steam — تجربة مباشرة عبر Steam مع حفظ الأصل واستعادته تلقائياً، أو تثبيت دائم بضغطة زر."
                    : (playable ? "تشغيل الحزمة المستقلة بمحرك Godot المطابق." : "استورد الحزمة من مشروع Godot الذي يحمّلها."),
                "يدعم Godot 4 العرض العربي واتجاه RTL والخطوط أصلياً بترميز UTF-8 المنطقي.");
        }
        if (profile.Name == "renpy")
        {
            bool project = Directory.Exists(Path.Combine(source.Path, "game"));
            bool templates = paths.Any(GameRuntime.IsArabicTemplate);
            bool scripts = project && paths.Any(path => !path.StartsWith("game/tl/", StringComparison.OrdinalIgnoreCase)
                && path.StartsWith("game/", StringComparison.OrdinalIgnoreCase) && Formats.For(path) is RenpyFormat);
            bool compiled = paths.Any(path => Path.GetExtension(path).ToLowerInvariant() is ".rpyc" or ".rpa");
            return new("Ren’Py", "", hasText && (!project || templates), scanned, project && templates ? GameLaunch.RenpyCopy : GameLaunch.None,
                templates ? "تحرير قوالب العربية وتجربة النسخة عبر SDK المطابق."
                    : scripts ? "ولّد قوالب العربية من الواجهة عبر SDK المطابق."
                    : compiled ? "عُثر على موارد مجمّعة. اختر قوالب العربية أو مجلد المصادر."
                    : "افتح قالب العربية مباشرة أو اختر مجلد مشروع Ren’Py.",
                "اختُبر الحوار والخيارات في عينة Ren’Py 8.6.1.", scripts && !templates);
        }
        if (profile.Name == "rpgmaker")
        {
            string edition = GameRuntime.RpgMakerVersion(source.Path);
            return new("RPG Maker", edition, hasText, scanned,
                GameLaunch.None,
                "أعد ملفات البيانات إلى نسخة اللعبة أو افتحها ضمن مشروع MV أو MZ المطابق.",
                "تحقق بنيوي من رسائل الأحداث والخيارات. يعتمد العرض على خطوط اللعبة.");
        }
        bool isSteamGame = !source.IsArchive && Directory.Exists(source.Root)
            && SteamGameTrial.FindSteamGame(source.Root) != null;

        if (profile.Name == "unity")
        {
            bool hasI2 = paths.Any(path => source.FormatFor(path) is UnityI2Format);
            bool hasBundles = paths.Any(path => source.FormatFor(path) is UnityBundleFormat);
            bool hasUnityAssets = paths.Any(path => source.FormatFor(path) is UnityAssetsFormat);
            var launch = hasText && isSteamGame ? GameLaunch.Steam : GameLaunch.None;
            return new(profile.Name, "", hasText, scanned, launch,
                hasI2
                    ? "لعبة Unity بنظام I2 Localization (داخل data.unity3d / resources.assets) — يقرأ Locara جميع النصوص ويحقن الخط العربي الاحتياطي وتشكيل العربية تلقائياً مع تجربة Steam والتثبيت المباشر."
                    : hasBundles
                        ? "لعبة Unity Localization (.bundle) — يقرأ Locara جداول النصوص مباشرة ويعيد بناء الحزمة مع مزامنة فهرس Addressables (catalog.bin/catalog.json) وحقن الخط العربي الاحتياطي (TextMeshPro) عند توفره."
                        : hasUnityAssets
                            ? "لعبة Unity بموارد TextAsset مدمجة (داخل resources.assets / sharedassets*.assets / data.unity3d) — يستخرج Locara النصوص المهيّأة (JSON/CSV/XML/INI/YAML/PO/TXT) ويحقن الخط العربي الاحتياطي (TextMeshPro) تلقائياً."
                            : hasText
                                ? "لعبة Unity بملفات نصية مفتوحة قابلة للترجمة والتثبيت المباشر مع حقن الخط العربي الاحتياطي (TextMeshPro) عند توفره."
                                : "لعبة Unity مجمّعة — لم يُعثر على جداول I2 Localization أو Unity Localization (.bundle) أو TextAsset نصي أو ملفات نصية مفتوحة قابلة للقراءة.",
                hasText
                    ? "تجهيز عربي تلقائي (تشكيل ICU والتفاف ذكي للأسطر + حقن خطك المختار في TextMeshPro Fallback عند توفره، مع خيار إيقاف التشكيل للألعاب الداعمة لـ RTL أصلياً)."
                    : "");
        }
        if (profile.Name == "unreal")
        {
            bool hasUnrealPak = paths.Any(path => source.FormatFor(path) is UnrealPakFormat);
            bool hasLocres = paths.Any(path => source.FormatFor(path) is UnrealLocresFormat);
            bool hasIoStore = paths.Any(path => path.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
            var launch = hasText && isSteamGame ? GameLaunch.Steam : GameLaunch.None;
            return new(profile.Name, "", hasText, scanned, launch,
                hasUnrealPak
                    ? "لعبة Unreal Engine 4/5 — يقرأ Locara نصوص .locres مباشرة من حزم .pak ويولّد حزمة تعديل جاهزة (Locara_Arabic_P.pak) داخل Content/Paks/~mods/ مع التثبيت المباشر وتجربة Steam دون تعديل ملفات اللعبة الأصلية."
                    : hasLocres
                        ? "موارد Unreal Engine (.locres) — قراءة وإعادة بناء ثنائية مباشرة مع دعم UTF-16LE للعربية."
                        : hasText
                            ? "لعبة Unreal بملفات نصية مفتوحة قابلة للترجمة والتثبيت المباشر."
                            : hasIoStore
                                ? "لعبة Unreal Engine 5 (IoStore .utoc/.ucas) — لم يُعثر على ملفات .locres غير مشفّرة داخل حزم .pak."
                                : "لعبة Unreal Engine مجمّعة — لم يُعثر على ملفات .locres غير مشفّرة داخل حزم .pak (قد تكون الحزمة مشفّرة بمفتاح AES).",
                hasUnrealPak || hasLocres
                    ? "يدعم Unreal Engine العرض العربي واتجاه RTL أصلياً عبر HarfBuzz/ICU (تُكتب النصوص بترميز UTF-16LE المنطقي دون حاجة لتشكيل مسبق)."
                    : "");
        }
        return new(profile.Name, "", hasText, scanned, hasText && isSteamGame ? GameLaunch.Steam : GameLaunch.None,
            "صدّر الموارد المعاد بناؤها أو ثبّتها مباشرة داخل اللعبة. افتح إعداد الاستخراج لاختيار الحقول أو الأعمدة.", "");
    }

    public void SelectPackage(string? package)
    {
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            string root = store.Info.Source;
            if (!Directory.Exists(root)) throw new InvalidOperationException("اختر مجلد اللعبة أولاً.");
            string previous = PackageChoice(root);
            string selection = LooseFiles;
            if (package != null)
            {
                string canonical = CanonicalPackagePath(root, package);
                if (string.Equals(previous, canonical, StringComparison.OrdinalIgnoreCase))
                    selection = previous;
                else
                {
                    string saved = store.Get(PackageSpellingSetting(canonical));
                    selection = saved.Length == 0 ? canonical : saved;
                }
            }
            // Retain the namespace of packages selected by older versions with different casing.
            if (previous.Length > 0 && previous != LooseFiles)
                store.Set(PackageSpellingSetting(previous), previous);
            if (store.Get(PackageSetting) != selection) store.Set(PackageSetting, selection);
            if (previous != selection)
            {
                store.Set("scanned_source", "");
                store.Set("scan_version", Guid.NewGuid().ToString("N"));
                store.Execute("UPDATE files SET active=0 WHERE active=1; UPDATE units SET active=0,revision=revision+1 WHERE active IN (1,2)");
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
    }

    public string SelectedPackage => PackageChoice(store.Get("source"));

    private string PackageChoice(string root)
    {
        string choice = store.Get(PackageSetting);
        if (choice.Length > 0) return choice;
        // Recover automatic choices made before the selection was stored separately.
        if (root.Length > 0 && store.Get("scanned_source") == root && store.Get("scan_errors") == "[]")
        {
            string previous = store.Get("scanned_package");
            return previous.Length > 0 ? previous : LooseFiles;
        }
        return "";
    }

    private static string CanonicalPackagePath(string root, string package)
    {
        string relative = Identity.SafePath(package);
        string candidate = Path.Combine(root, relative);
        if (!File.Exists(candidate) || !Containers.IsArchive(candidate))
            throw new FileNotFoundException("الحزمة المختارة غير موجودة.", relative);
        Identity.RequireNoLinks(candidate);

        // Recover the spelling stored on disk, including each parent directory.
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 };
        string current = root;
        foreach (string segment in relative.Split('/'))
        {
            var matches = Directory.EnumerateFileSystemEntries(current, segment, options).ToList();
            current = matches.FirstOrDefault(path => Path.GetFileName(path) == segment)
                ?? (matches.Count == 1 ? matches[0] : throw new IOException("مسار الحزمة غير واضح — " + relative));
        }
        return Identity.SafePath(Path.GetRelativePath(root, current));
    }

    private static ResourceIndex Open(Source source, string? password, CancellationToken cancellation)
    {
        if (source.Package.Length != 0 && !File.Exists(source.Path))
            throw new FileNotFoundException("لم تعد الحزمة المختارة في مجلد اللعبة.", source.Package);
        if (source.Package.Length != 0 && !source.IsArchive)
            throw new InvalidDataException("لم يعد الملف المحدد حاوية مدعومة.");
        return source.Adapter.Index(source.Path, password, cancellation);
    }

    public IReadOnlyList<string> ListConfigurableResources(string? password = null, CancellationToken cancellation = default)
    {
        var source = CurrentSource(cancellation);
        using var resources = Open(source, password, cancellation);
        return resources.Entries.Where(entry => source.FormatFor(entry.Path) != null && Formats.Configurable(entry.Path))
            .Select(entry => source.Key(entry.Path)).ToList();
    }

    public ExtractionPreview InspectResource(string resource, string? password = null, CancellationToken cancellation = default)
    {
        var source = CurrentSource(cancellation);
        using var resources = Open(source, password, cancellation);
        var entry = resources.Entries.FirstOrDefault(item => source.Key(item.Path) == resource)
            ?? throw new FileNotFoundException("المورد المختار غير متاح.", resource);
        var format = source.FormatFor(entry.Path)
            ?? throw new NotSupportedException("المورد المختار لم يعد مدعوماً.");
        return Formats.Inspect(entry.Read(cancellation), format, Rules(resource));
    }

    public void ConfigureResource(string resource, ExtractionRule? rule)
    {
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            string value = rule == null ? "" : JsonSerializer.Serialize(rule);
            if (store.Get(RulesSetting(resource)) != value)
            {
                store.Set(RulesSetting(resource), value);
                store.Set("scanned_source", "");
                store.Set("scan_version", Guid.NewGuid().ToString("N"));
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
    }

    private ExtractionRule? Rules(string resource)
    {
        string value = store.Get(RulesSetting(resource));
        return value.Length == 0 ? null : JsonSerializer.Deserialize<ExtractionRule>(value);
    }

    public ScanResult Scan(string? password = null, CancellationToken cancellation = default)
    {
        string source = store.Get("source");
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Choose a source before scanning.");
        EnsureCatalogSelected();
        string engine = store.Get("engine");
        string scope = store.Get("source_scope");
        string scanVersion = store.Get("scan_version");
        var input = CurrentSource(cancellation);
        using var resources = Open(input, password, cancellation);
        var profile = Engines.Resolve(engine, resources.Entries.Select(entry => entry.Path).ToList(), input.Path);
        bool rpgDataAtRoot = profile.Name == "rpgmaker" && Engines.RpgDataAtRoot(input.Path);
        var diagnostics = new List<Diagnostic>();
        var advisories = new List<Diagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previous = store.Query("SELECT path,hash,format FROM files WHERE active=1",
            reader => (Path: reader.GetString(0), Hash: reader.GetString(1), Format: reader.GetString(2)))
            .ToDictionary(row => row.Path, StringComparer.Ordinal);
        bool sameSource = store.Get("scanned_source") == source && store.Get("scanned_engine") == profile.Name
            && store.Get("scanned_package") == input.Package && store.Get("extraction_revision") == ExtractionRevision;
        string cachedAdvisories = sameSource ? store.Get("scan_advisories") : "";
        var priorAdvisories = (cachedAdvisories.Length == 0 ? [] : JsonSerializer.Deserialize<Diagnostic[]>(cachedAdvisories)!)
            .ToLookup(item => item.Path, StringComparer.Ordinal);
        var changes = new List<(string Path, string Hash, IFormatHandler Format, IReadOnlyList<TextSlot> Slots,
            Dictionary<string, string>? LegacyLocators, string? BundleLocale)>();
        foreach (var resource in resources.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var format = input.FormatFor(resource.Path);
            if (format == null) continue;
            string path = input.Key(resource.Path);
            try
            {
                string hash;
                byte[]? bytes = null;
                if ((format is UnityI2Format or UnityAssetsFormat or UnrealPakFormat) && !input.IsArchive)
                {
                    hash = resource.Hash(cancellation);
                }
                else
                {
                    bytes = resource.Read(cancellation);
                    hash = Identity.Hash(bytes);
                }
                var oldFile = previous.GetValueOrDefault(path);
                if (!sameSource || oldFile.Hash != hash || oldFile.Format != format.Name)
                {
                    string diskPath = Directory.Exists(input.Path) ? Path.Combine(input.Path, resource.Path) : input.Path;
                    IReadOnlyList<TextSlot> slots = format is UnityI2Format && !input.IsArchive
                        ? UnityI2Format.ExtractFromFile(diskPath)
                        : format is UnityAssetsFormat && !input.IsArchive
                            ? UnityAssetsFormat.ExtractFromFile(diskPath)
                            : format is UnrealPakFormat && !input.IsArchive
                                ? UnrealPakFormat.ExtractFromFile(diskPath, cancellation)
                                : Formats.Extract(profile, resource.Path, bytes!, format, Rules(path), rpgDataAtRoot,
                                    message => advisories.Add(new(path, message)));
                    if (slots.Select(slot => slot.Locator).Distinct().Count() != slots.Count)
                        throw new InvalidDataException("Ambiguous duplicate text locators.");
                    var legacyLocators = format is StarTruckerCatalog && oldFile.Hash == hash
                        && oldFile.Format == "xml/xliff" ? StarTruckerCatalog.LegacyLocators(bytes!) : null;
                    string? bundleLocale = format is UnityBundleFormat && bytes != null
                        ? UnityBundleFormat.DetectStringTableLocale(bytes) : null;
                    changes.Add((path, hash, format, slots, legacyLocators, bundleLocale));
                }
                else advisories.AddRange(priorAdvisories[path]);
                seen.Add(path);
            }
            catch (Exception error) when (error is IOException or FormatException or JsonException
                or InvalidDataException or System.Xml.XmlException or YamlDotNet.Core.YamlException or ArgumentException
                or ICSharpCode.SharpZipLib.Zip.ZipException or NotSupportedException)
            {
                diagnostics.Add(new(path, error.Message));
            }
        }
        if (seen.Count == 0 && diagnostics.Count == 0)
        {
            if (input.FormatDiagnostics.Count > 0)
            {
                diagnostics.AddRange(input.FormatDiagnostics);
            }
            else
            {
                string message = profile.Name switch
                {
                    "unity" => "هذه لعبة Unity مجمّعة ولا تحتوي على ملفات نصوص مفتوحة؛ نصوصها محزومة داخل حزم ثنائية (.bundle / .assets مثل localization-string-tables-*.bundle) وتحتاج أداة فك حزم Unity أولاً. (تجاهل Locara ملفات محرك Unity الداخلية مثل RuntimeInitializeOnLoads.json تلقائياً لحماية اللعبة وتوفير رصيدك).",
                    "unreal" => "هذه لعبة Unreal Engine مجمّعة ولم يُعثر فيها على ملفات .locres غير مشفّرة داخل حزم .pak (قد تكون الحزم مشفّرة بمفتاح AES أو تستخدم نصوصاً مدمجة في أصول UE5 IoStore).",
                    _ => "لم يُعثر على موارد نصية مدعومة في المصدر المختار."
                };
                diagnostics.Add(new(input.Path, message));
            }
        }

        cancellation.ThrowIfCancellationRequested();
        var fileSelection = LoadFileSelection(input.Package);
        var fileSelectionModes = LoadFileSelectionModes(input.Package);
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            if (store.Get("source") != source || store.Get("engine") != engine || store.Get("source_scope") != scope || store.Get("scan_version") != scanVersion)
                throw new InvalidOperationException("Project settings changed. Scan again.");

            foreach (string path in previous.Keys.Where(path => !seen.Contains(path)))
            {
                cancellation.ThrowIfCancellationRequested();
                store.Execute("UPDATE files SET active=0 WHERE path=$p; UPDATE units SET active=0 WHERE path=$p", ("$p", path));
            }

            bool hasEnglishBundle = changes.Any(c => IsEnglishLocaleCode(c.BundleLocale))
                || seen.Any(p => p.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                    && (p.Contains("(en)", StringComparison.OrdinalIgnoreCase)
                        || p.Contains("english", StringComparison.OrdinalIgnoreCase)));
            bool hasPrimaryPathLocale = seen.Any(p => IsPrimaryLocaleSource(p));

            var classifications = new Dictionary<string, FileClassification>(changes.Count, StringComparer.Ordinal);
            foreach (var change in changes)
                classifications[change.Path] = TextQualityClassifier.ClassifyFile(
                    change.Path,
                    change.Slots.Select(s => (s.Locator, s.Source)).ToArray(),
                    change.Format.Name);

            int primaryDedicatedHumanUnits = 0;
            var changedPaths = new HashSet<string>(changes.Select(c => c.Path), StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (!IsDedicatedLocalizationFormat(change.Path, change.Format.Name)) continue;
                bool isSecondary = (change.Format is UnityBundleFormat && hasEnglishBundle && change.BundleLocale != null && !IsEnglishLocaleCode(change.BundleLocale))
                    || (change.Format is not UnityBundleFormat && hasPrimaryPathLocale && ExtractPathLocale(change.Path) is { } loc && !IsEnglishLocaleCode(loc));
                if (isSecondary) continue;
                var q = classifications[change.Path];
                if (q.Verdict == FileQualityVerdict.Translatable || change.Format is UnityI2Format or StarTruckerCatalog)
                    primaryDedicatedHumanUnits += q.HumanCount;
            }
            if (primaryDedicatedHumanUnits < DedicatedLocalizationMinHumanUnits)
            {
                foreach (var (prevPath, prevInfo) in previous)
                {
                    if (!seen.Contains(prevPath) || changedPaths.Contains(prevPath)) continue;
                    if (!IsDedicatedLocalizationFormat(prevPath, prevInfo.Format)) continue;
                    bool isSecondary = prevInfo.Format != "unity-bundle"
                        && hasPrimaryPathLocale
                        && ExtractPathLocale(prevPath) is { } loc
                        && !IsEnglishLocaleCode(loc);
                    if (isSecondary) continue;
                    primaryDedicatedHumanUnits += store.Query(
                        "SELECT COUNT(*) FROM units WHERE path=$p AND active IN (1, 2)",
                        r => r.GetInt32(0), ("$p", prevPath))[0];
                    if (primaryDedicatedHumanUnits >= DedicatedLocalizationMinHumanUnits) break;
                }
            }
            bool hasPrimaryDedicatedLoc = primaryDedicatedHumanUnits >= DedicatedLocalizationMinHumanUnits;

            foreach (var change in changes)
            {
                cancellation.ThrowIfCancellationRequested();
                store.Execute("UPDATE units SET active=0 WHERE path=$p", ("$p", change.Path));
                store.Execute("""
                    INSERT INTO files(path,hash,format,snapshot) VALUES($p,$h,$f,X'')
                    ON CONFLICT(path) DO UPDATE SET hash=$h,format=$f,snapshot=X'',active=1
                    """, ("$p", change.Path), ("$h", change.Hash), ("$f", change.Format.Name));
                var previousUnits = store.Query("""
                    SELECT id,path,locator,source,target,context,status,warning,revision,imported
                    FROM units WHERE path=$p
                    """, ProjectStore.ReadUnit, ("$p", change.Path)).ToDictionary(unit => unit.Id);

                var quality = classifications[change.Path];
                bool included;
                string? pathLocale = ExtractPathLocale(change.Path);
                if (fileSelection.TryGetValue(change.Path, out bool explicitIncluded))
                {
                    included = explicitIncluded;
                }
                else if (previousUnits.Values.Any(u => u.Target.Length > 0 || u.Status != "pending"))
                {
                    included = true;
                }
                else if (change.Format is StarTruckerCatalog)
                {
                    included = true;
                }
                else if (change.Format is UnityBundleFormat && hasEnglishBundle
                    && change.BundleLocale != null && !IsEnglishLocaleCode(change.BundleLocale))
                {
                    included = false;
                    advisories.Add(new(change.Path,
                        $"حزمة لغة أخرى ({change.BundleLocale}) — استُبعدت تلقائياً للاكتفاء باللغة الإنجليزية وتوفير رصيدك؛ يمكنك تفعيلها من تبويب «الملفات»."));
                }
                else if (change.Format is not UnityBundleFormat && hasPrimaryPathLocale
                    && pathLocale != null && !IsEnglishLocaleCode(pathLocale))
                {
                    included = false;
                    advisories.Add(new(change.Path,
                        $"ملف لغة أخرى ({pathLocale}) — استُبعد تلقائياً للاكتفاء باللغة الإنجليزية وتوفير رصيدك؛ يمكنك تفعيله من تبويب «الملفات»."));
                }
                else if (quality.Verdict == FileQualityVerdict.Technical)
                {
                    included = false;
                    if (change.Slots.Count > 0)
                    {
                        advisories.Add(new(change.Path,
                            $"استُبعد تلقائياً كملف تقني/برمجي ({quality.Reason}) — يمكنك تفعيله من تبويب «الملفات» إذا أردت ترجمته."));
                    }
                }
                else if (hasPrimaryDedicatedLoc
                    && !IsDedicatedLocalizationFormat(change.Path, change.Format.Name)
                    && !(IsExplicitLocalizationDirectory(change.Path) && quality.Verdict == FileQualityVerdict.Translatable))
                {
                    included = false;
                    if (change.Slots.Count > 0)
                    {
                        advisories.Add(new(change.Path,
                            "ملف بيانات/إعدادات جانبي — توجد ملفات ترجمة رسمية مخصّصة في المشروع، لذا استُبعد تلقائياً لحماية اللعبة وتوفير رصيدك؛ يمكنك تفعيله من تبويب «الملفات»."));
                    }
                }
                else if (quality.Verdict == FileQualityVerdict.Mixed)
                {
                    included = false;
                    if (change.Slots.Count > 0)
                    {
                        advisories.Add(new(change.Path,
                            $"استُبعد تلقائياً كملف مختلط ({quality.Reason}) — يمكنك معاينته وتفعيل نصوصه البشرية فقط من تبويب «الملفات»."));
                    }
                }
                else
                {
                    included = true;
                }

                bool humanOnly = included
                    && fileSelectionModes.TryGetValue(change.Path, out string? mode)
                    && string.Equals(mode, "human", StringComparison.OrdinalIgnoreCase);
                string unitScope = scope.Length == 0 ? change.Path : scope + "\n" + change.Path;
                var matched = new HashSet<string>(StringComparer.Ordinal);
                foreach (var slot in change.Slots)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string id = Identity.UnitId(unitScope, slot.Locator);
                    var oldUnit = previousUnits.GetValueOrDefault(id);
                    if (oldUnit == null && change.LegacyLocators?.TryGetValue(slot.Locator, out string? legacy) == true)
                    {
                        oldUnit = previousUnits.GetValueOrDefault(Identity.UnitId(unitScope, legacy));
                        if (oldUnit?.Source == slot.Source) oldUnit.Context = slot.Context;
                        else oldUnit = null;
                    }
                    if (oldUnit != null) matched.Add(oldUnit.Id);
                    bool ambiguous = change.Format is StarTruckerCatalog
                        && slot.Locator.StartsWith("duplicate:", StringComparison.Ordinal)
                        && previous.GetValueOrDefault(change.Path).Hash != change.Hash;
                    int slotActive = !included
                        ? 2
                        : (!humanOnly
                            || (oldUnit != null && (oldUnit.Target.Length > 0 || oldUnit.Status != "pending"))
                            || TextQualityClassifier.ClassifyEntry(slot.Locator, slot.Source, change.Format.Name) != StringQuality.Technical)
                            ? 1
                            : 2;
                    StoreSlot(id, change.Path, slot, oldUnit, profile.Name, ambiguous, slotActive);
                }
                if (change.Format is StarTruckerCatalog && previous.ContainsKey(change.Path))
                {
                    int unmatched = previousUnits.Values.Count(unit => unit.Target.Length > 0 && !matched.Contains(unit.Id)
                        && unit.Id == Identity.UnitId(unitScope, unit.Locator));
                    if (unmatched > 0)
                        advisories.Add(new(change.Path, $"احتُفظ بـ {unmatched} ترجمة في قاعدة المشروع لتعذّر مطابقة موضعها. استُبعدت من البناء."));
                }
            }

            if (Directory.Exists(source) && diagnostics.Count == 0 && store.Get(PackageSetting).Length == 0)
                store.Set(PackageSetting, input.Package.Length == 0 ? LooseFiles : input.Package);
            store.Set("scan_version", Guid.NewGuid().ToString("N"));
            store.Set("scanned_source", source);
            store.Set("scanned_engine", profile.Name);
            store.Set("scanned_package", input.Package);
            store.Set("scan_errors", JsonSerializer.Serialize(diagnostics));
            store.Set("scan_advisories", JsonSerializer.Serialize(advisories));
            store.Set("extraction_revision", ExtractionRevision);
            cancellation.ThrowIfCancellationRequested();
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }

        int count = store.Query("SELECT COUNT(*) FROM units WHERE active=1", reader => reader.GetInt32(0))[0];
        if (seen.Count > 0 && count == 0 && advisories.Count == 0)
        {
            int excludedUnits = store.Query("SELECT COUNT(*) FROM units WHERE active=2", reader => reader.GetInt32(0))[0];
            if (excludedUnits > 0)
                advisories.Add(new(input.Path,
                    $"جميع الملفات المكتشفة ({seen.Count}) مستبعدة حالياً ({excludedUnits:N0} نص). افتح تبويب «الملفات» لمعاينة العينات أو تحديد ما تريد ترجمته."));
        }
        if (profile.Name == "renpy" && advisories.Count == 0 && seen.Count > 0
            && resources.Entries.Any(entry => Formats.For(entry.Path) is RenpyFormat)
            && !store.Query("""
                SELECT EXISTS(SELECT 1 FROM units JOIN files USING(path)
                    WHERE units.active=1 AND files.active=1 AND files.format='renpy-template')
                """, row => row.GetBoolean(0)).Single())
        {
            diagnostics.Add(new(input.Path,
                "لم تُستخرج حوارات من قوالب Ren’Py. إذا كان المدخل ملفات السيناريو الأصلية، ولّد قوالب العربية عبر Generate Translations في SDK المطابق، ثم افحص game/tl/arabic."));
        }
        diagnostics.AddRange(advisories);
        return new(seen.Count, count, diagnostics, input.Package);
    }

    public IReadOnlyList<FileQualitySummary> ScannedFiles()
    {
        var files = store.Query("SELECT path, format FROM files WHERE active=1 ORDER BY path",
            r => (Path: r.GetString(0), Format: r.GetString(1)));
        if (files.Count == 0) return [];

        string package = store.Get("scanned_package");
        var explicitSelection = LoadFileSelection(package);
        var selectionModes = LoadFileSelectionModes(package);

        var unitsByFile = new Dictionary<string, List<(string Locator, string Source, string Context, string Status, int Active)>>(StringComparer.Ordinal);
        foreach (var row in store.Query("""
            SELECT path, locator, source, context, status, active FROM units
            WHERE active IN (1, 2)
            ORDER BY path, locator
            """, r => (Path: r.GetString(0), Locator: r.GetString(1), Source: r.GetString(2), Context: r.GetString(3), Status: r.GetString(4), Active: r.GetInt32(5))))
        {
            if (!unitsByFile.TryGetValue(row.Path, out var list))
            {
                list = [];
                unitsByFile[row.Path] = list;
            }
            list.Add((row.Locator, row.Source, row.Context, row.Status, row.Active));
        }

        static string? ExtractBundleLocale(string filePath, List<(string Locator, string Source, string Context, string Status, int Active)> rows)
        {
            foreach (var r in rows)
            {
                int open = r.Context.LastIndexOf('(');
                int close = r.Context.LastIndexOf(')');
                if (open >= 0 && close > open + 1)
                {
                    string code = r.Context[(open + 1)..close].Trim();
                    if (code.Length is >= 2 and <= 12) return code;
                }
            }
            int po = filePath.LastIndexOf('(');
            int pc = filePath.LastIndexOf(')');
            if (po >= 0 && pc > po + 1)
            {
                string code = filePath[(po + 1)..pc].Trim();
                if (code.Length is >= 2 and <= 12) return code;
            }
            return null;
        }

        bool hasEnglishBundle = files.Any(f => f.Format == "unity-bundle"
            && IsEnglishLocaleCode(ExtractBundleLocale(f.Path, unitsByFile.GetValueOrDefault(f.Path) ?? [])));
        bool hasPrimaryPathLocale = files.Any(f => f.Format != "unity-bundle" && IsPrimaryLocaleSource(f.Path));

        var classifications = new Dictionary<string, FileClassification>(files.Count, StringComparer.Ordinal);
        int primaryDedicatedHumanUnits = 0;
        foreach (var (filePath, format) in files)
        {
            var rows = unitsByFile.GetValueOrDefault(filePath) ?? [];
            var entries = rows.Select(r => (r.Locator, r.Source)).ToArray();
            var c = TextQualityClassifier.ClassifyFile(filePath, entries, format);
            classifications[filePath] = c;

            if (!IsDedicatedLocalizationFormat(filePath, format)) continue;
            bool isSecondary = (format == "unity-bundle" && hasEnglishBundle
                    && ExtractBundleLocale(filePath, rows) is { } bLoc && !IsEnglishLocaleCode(bLoc))
                || (format != "unity-bundle" && hasPrimaryPathLocale
                    && ExtractPathLocale(filePath) is { } pLoc && !IsEnglishLocaleCode(pLoc));
            if (isSecondary) continue;
            if (c.Verdict == FileQualityVerdict.Translatable || format is "unity-i2" or "star-trucker-catalog")
                primaryDedicatedHumanUnits += c.HumanCount;
        }
        bool hasPrimaryDedicatedLoc = primaryDedicatedHumanUnits >= DedicatedLocalizationMinHumanUnits;

        var result = new List<FileQualitySummary>(files.Count);
        foreach (var (filePath, format) in files)
        {
            var rows = unitsByFile.GetValueOrDefault(filePath) ?? [];
            int total = rows.Count;
            int pending = 0, translated = 0, approved = 0, activeOne = 0;
            foreach (var (_, _, _, status, active) in rows)
            {
                if (active == 1) activeOne++;
                if (status == "pending") pending++;
                else if (status == "approved") approved++;
                else if (status is "translated" or "imported") translated++;
            }

            var classification = classifications[filePath];
            var verdict = classification.Verdict;
            string reason = classification.Reason;
            if (format == "unity-bundle" && hasEnglishBundle
                && ExtractBundleLocale(filePath, rows) is { } bundleLoc && !IsEnglishLocaleCode(bundleLoc))
            {
                verdict = FileQualityVerdict.SecondaryLanguage;
                reason = $"حزمة لغة إضافية ({bundleLoc}) — مستبعدة افتراضياً للاكتفاء بالإنجليزية وتوفير رصيدك.";
            }
            else if (format != "unity-bundle" && hasPrimaryPathLocale
                && ExtractPathLocale(filePath) is { } pathLocale && !IsEnglishLocaleCode(pathLocale))
            {
                verdict = FileQualityVerdict.SecondaryLanguage;
                reason = $"ملف لغة إضافية ({pathLocale}) — مستبعد افتراضياً للاكتفاء بالإنجليزية وتوفير رصيدك.";
            }
            else if (verdict != FileQualityVerdict.Technical
                && hasPrimaryDedicatedLoc
                && !IsDedicatedLocalizationFormat(filePath, format)
                && !(IsExplicitLocalizationDirectory(filePath) && verdict == FileQualityVerdict.Translatable))
            {
                verdict = FileQualityVerdict.AuxiliaryData;
                reason = "ملف بيانات/إعدادات جانبي — توجد ملفات ترجمة رسمية مخصّصة في المشروع، لذا استُبعد افتراضياً لحماية اللعبة وتوفير رصيدك؛ يمكنك تفعيله عند الحاجة.";
            }
            else if (format == "unity-i2")
            {
                reason = "نصوص اللعبة الرئيسية (I2 Localization) — يُحقن الخط العربي وتشكيل الحروف تلقائياً عند البناء.";
            }
            else if (format == "unity-assets")
            {
                reason = "نصوص مدمجة داخل موارد Unity (TextAsset) — مُفلترة تلقائياً عبر حارس الجودة مع حقن الخط العربي الاحتياطي (TextMeshPro) عند توفره.";
            }
            else if (format == "unreal-pak")
            {
                reason = "نصوص Unreal Engine (.locres داخل .pak) — يُبنى الناتج كحزمة تعديل جاهزة Locara_Arabic_P.pak دون تعديل ملفات اللعبة الأصلية.";
            }
            else if (format == "unreal-locres")
            {
                reason = "ملف ترجمة Unreal Engine ثنائي (.locres) — يدعم العربية بترميز UTF-16LE أصلياً.";
            }
            else if (format == "godot-translation")
            {
                reason = "ملف ترجمة Godot ثنائي (.translation / OptimizedTranslation) — يُعاد بناؤه بترميز UTF-8 مع الحفاظ على جدول التجزئة.";
            }
            else if (format == "xunity-autotranslator")
            {
                reason = "ملف ترجمة XUnity.AutoTranslator (BepInEx) — مخصّص لألعاب Unity ذات النصوص المدمجة في الكود البرمجي.";
            }
            else if (format == "po" && IsGettextTemplate(filePath))
            {
                reason = "قالب الترجمة الرئيسي (Gettext POT) — نصوص اللعبة الرسمية جاهزة للترجمة.";
            }

            bool included = total > 0
                ? activeOne > 0
                : (explicitSelection.TryGetValue(filePath, out bool saved) ? saved : verdict == FileQualityVerdict.Translatable);
            bool humanOnly = included
                && selectionModes.TryGetValue(filePath, out string? mode)
                && string.Equals(mode, "human", StringComparison.OrdinalIgnoreCase);

            result.Add(new FileQualitySummary(
                filePath, format, total, pending, translated, approved,
                classification.HumanCount, classification.TechnicalCount, classification.TechnicalRatio,
                verdict, reason, classification.Samples, included, activeOne, humanOnly));
        }

        return result
            .OrderByDescending(f => f.Included)
            .ThenBy(f => f.Verdict switch
            {
                FileQualityVerdict.Translatable => 0,
                FileQualityVerdict.Mixed => 1,
                FileQualityVerdict.AuxiliaryData => 2,
                FileQualityVerdict.SecondaryLanguage => 3,
                _ => 4
            })
            .ThenByDescending(f => IsDedicatedLocalizationFormat(f.Path, f.Format))
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetFileIncluded(string resourcePath, bool included, bool humanOnly = false)
    {
        if (!included || !humanOnly)
        {
            SetFilesIncluded(new Dictionary<string, bool>(StringComparer.Ordinal) { [resourcePath] = included });
            return;
        }
        string package = store.Get("scanned_package");
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            var saved = LoadFileSelection(package);
            var modes = LoadFileSelectionModes(package);
            saved[resourcePath] = true;
            modes[resourcePath] = "human";

            string format = store.Query("SELECT format FROM files WHERE path=$p AND active=1",
                r => r.GetString(0), ("$p", resourcePath)).FirstOrDefault() ?? "";
            var rows = store.Query(
                "SELECT id, locator, source, target, status, active FROM units WHERE path=$p AND active IN (1, 2)",
                r => (Id: r.GetString(0), Locator: r.GetString(1), Source: r.GetString(2), Target: r.GetString(3), Status: r.GetString(4), Active: r.GetInt32(5)),
                ("$p", resourcePath));
            foreach (var row in rows)
            {
                bool keepActive = row.Target.Length > 0
                    || row.Status != "pending"
                    || TextQualityClassifier.ClassifyEntry(row.Locator, row.Source, format) != StringQuality.Technical;
                int desired = keepActive ? 1 : 2;
                if (row.Active != desired)
                    store.Execute("UPDATE units SET active=$a WHERE id=$id", ("$a", desired), ("$id", row.Id));
            }

            SaveFileSelection(package, saved);
            SaveFileSelectionModes(package, modes);
            store.Set("scan_version", Guid.NewGuid().ToString("N"));
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
    }

    public void SetFilesIncluded(IReadOnlyDictionary<string, bool> selections)
    {
        if (selections.Count == 0) return;
        string package = store.Get("scanned_package");
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            var saved = LoadFileSelection(package);
            var modes = LoadFileSelectionModes(package);
            bool anyChanged = false;
            foreach (var (path, included) in selections)
            {
                saved[path] = included;
                modes.Remove(path);
                int fromActive = included ? 2 : 1;
                int toActive = included ? 1 : 2;
                store.Execute("UPDATE units SET active=$to WHERE path=$p AND active=$from",
                    ("$to", toActive), ("$p", path), ("$from", fromActive));
                anyChanged = true;
            }
            if (anyChanged)
            {
                SaveFileSelection(package, saved);
                SaveFileSelectionModes(package, modes);
                store.Set("scan_version", Guid.NewGuid().ToString("N"));
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
    }

    public IReadOnlyList<FileSchemaCluster> BuildSchemaClusters()
    {
        var files = ScannedFiles();
        if (files.Count == 0) return [];
        var entriesByFile = new Dictionary<string, List<(string Locator, string Source)>>(StringComparer.Ordinal);
        foreach (var row in store.Query("""
            SELECT path, locator, source FROM units
            WHERE active IN (1, 2)
            ORDER BY path, locator
            """, r => (Path: r.GetString(0), Locator: r.GetString(1), Source: r.GetString(2))))
        {
            if (!entriesByFile.TryGetValue(row.Path, out var list))
            {
                list = [];
                entriesByFile[row.Path] = list;
            }
            list.Add((row.Locator, row.Source));
        }
        var readonlyMap = entriesByFile.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<(string Locator, string Source)>)kv.Value,
            StringComparer.Ordinal);
        return SchemaClusterer.Build(files, readonlyMap);
    }

    public ScoutApplyResult ApplyScoutPlan(
        ScoutPlan plan,
        IReadOnlySet<string>? selectedClusterIds = null,
        string? password = null,
        CancellationToken cancellation = default)
    {
        var clustersById = plan.AnalyzedClusters.ToDictionary(c => c.ClusterId, StringComparer.Ordinal);
        string package = store.Get("scanned_package");
        int appliedClusters = 0, includedFiles = 0, excludedFiles = 0, ruleFiles = 0;
        bool needsRescan = false;

        store.Execute("BEGIN IMMEDIATE");
        try
        {
            var saved = LoadFileSelection(package);
            var modes = LoadFileSelectionModes(package);

            foreach (var rec in plan.Recommendations)
            {
                if (selectedClusterIds != null && !selectedClusterIds.Contains(rec.ClusterId))
                    continue;
                if (!clustersById.TryGetValue(rec.ClusterId, out var cluster))
                    continue;
                appliedClusters++;

                if (rec.Action == "exclude")
                {
                    foreach (string path in cluster.FilePaths)
                    {
                        saved[path] = false;
                        modes.Remove(path);
                        store.Execute("UPDATE units SET active=2 WHERE path=$p AND active=1", ("$p", path));
                        excludedFiles++;
                    }
                }
                else if (rec.Action == "include_all")
                {
                    foreach (string path in cluster.FilePaths)
                    {
                        saved[path] = true;
                        modes.Remove(path);
                        var existingRule = Rules(path);
                        if (existingRule?.FieldPatterns is { Length: > 0 })
                        {
                            var cleared = existingRule with { FieldPatterns = null };
                            bool empty = cleared.Key == null && cleared.Source == null && cleared.Target == null
                                && cleared.Sheet == null && (cleared.Fields == null || cleared.Fields.Length == 0);
                            store.Set(RulesSetting(path), empty ? "" : JsonSerializer.Serialize(cleared));
                            store.Execute("UPDATE files SET hash='' WHERE path=$p", ("$p", path));
                            needsRescan = true;
                        }
                        store.Execute("UPDATE units SET active=1 WHERE path=$p AND active=2", ("$p", path));
                        includedFiles++;
                    }
                }
                else if (rec.Action == "include_fields" && rec.FieldPatterns.Count > 0)
                {
                    string[] patterns = rec.FieldPatterns.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (string path in cluster.FilePaths)
                    {
                        saved[path] = true;
                        modes.Remove(path);
                        var existing = Rules(path);
                        var updated = (existing ?? new ExtractionRule()) with { FieldPatterns = patterns };
                        string serialized = JsonSerializer.Serialize(updated);
                        if (store.Get(RulesSetting(path)) != serialized)
                        {
                            store.Set(RulesSetting(path), serialized);
                            store.Execute("UPDATE files SET hash='' WHERE path=$p", ("$p", path));
                            needsRescan = true;
                        }
                        ruleFiles++;
                        includedFiles++;
                    }
                }
            }

            if (appliedClusters > 0)
            {
                SaveFileSelection(package, saved);
                SaveFileSelectionModes(package, modes);
                store.Set("scan_version", Guid.NewGuid().ToString("N"));
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }

        int activeUnits;
        if (needsRescan)
        {
            var scan = Scan(password, cancellation);
            activeUnits = scan.Units;
        }
        else
        {
            activeUnits = store.Query("SELECT COUNT(*) FROM units WHERE active=1", r => r.GetInt32(0))[0];
        }

        return new ScoutApplyResult(appliedClusters, includedFiles, excludedFiles, ruleFiles, activeUnits);
    }

    public DeploymentAdvice AnalyzeDeployment(string? password = null, CancellationToken cancellation = default) =>
        DeploymentAdvisor.Analyze(Capabilities(password, cancellation), ScannedFiles());

    private void StoreSlot(string id, string path, TextSlot slot, Unit? old, string engine, bool ambiguous = false, int active = 1)
    {
        string imported = string.IsNullOrWhiteSpace(slot.ExistingTarget) ? "" : slot.ExistingTarget;
        string target = old?.Target ?? "";
        string status = old?.Status ?? "pending";
        string warning = old?.Warning ?? "";
        bool importChanged = imported != (old?.ImportedTarget ?? "");
        bool legacyRowContext = slot.Locator.StartsWith("key:", StringComparison.Ordinal)
            && old?.Context.StartsWith("row ", StringComparison.Ordinal) == true && slot.Context == slot.Locator;
        bool meaningChanged = old != null && (old.Source != slot.Source || (old.Context != slot.Context && !legacyRowContext));
        if (target.Length > 0 && (ambiguous || meaningChanged || (importChanged && target != imported && status != "imported")))
        {
            status = "conflict";
            warning = ambiguous ? "تغيّر الكتالوج وهذا المعرّف مكرر. تحقّق من الموضع قبل اعتماد الترجمة."
                : "تغيّر النص أو السياق أو ترجمة الملف. راجع الترجمتين قبل الاعتماد.";
        }
        else if (importChanged && (status == "pending" || status == "imported"))
        {
            target = imported;
            status = imported.Length == 0 ? "pending" : "imported";
        }
        if (status != "conflict")
        {
            warning = target.Length == 0 ? "" : Tokens.Validate(slot.Source, target, engine);
            if (warning.Length > 0 && status == "approved") status = "conflict";
        }
        store.Execute("""
            INSERT INTO units(id,path,locator,source,context,target,status,warning,imported,active)
            VALUES($id,$path,$loc,$src,$ctx,$target,$state,$warning,$import,$active)
            ON CONFLICT(id) DO UPDATE SET source=$src,context=$ctx,target=$target,status=$state,
                warning=$warning,imported=$import,active=$active,revision=units.revision+
                    ($ambiguous OR units.source<>$src OR units.context<>$ctx OR units.target<>$target OR units.status<>$state
                     OR units.warning<>$warning OR units.imported<>$import)
            """, ("$id", id), ("$path", path), ("$loc", slot.Locator), ("$src", slot.Source),
            ("$ctx", slot.Context), ("$target", target), ("$state", status), ("$warning", warning), ("$import", imported),
            ("$active", active), ("$ambiguous", ambiguous));
    }

    public BuildResult Build(string destination, bool final = false, string? password = null, CancellationToken cancellation = default,
        PackagePreview? reviewed = null)
    {
        // All source settings, fingerprints and translations must come from the same project snapshot.
        store.Execute("BEGIN");
        try { return BuildSnapshot(destination, final, password, cancellation, reviewed); }
        finally { store.Execute("ROLLBACK"); }
    }

    public PackagePreview Preview(bool final = false, string? password = null, CancellationToken cancellation = default)
    {
        store.Execute("BEGIN");
        try
        {
            var source = CurrentSource(cancellation);
            using var index = Open(source, password, cancellation);
            return Prepare(source, index, final, cancellation).Preview;
        }
        finally { store.Execute("ROLLBACK"); }
    }

    private sealed record BuildPlan(PackagePreview Preview, IEngineProfile Profile,
        Dictionary<string, string> ExpectedHashes, List<PackageChange> Changes, bool ArabicDisplay);

    private static string DefaultCustomFontsDir => Path.Combine(LocaraPaths.UserDataDirectory, "fonts");

    private ArabicFontChoice ResolveDisplayFont() =>
        ArabicFontCatalog.Resolve(store.Get("display_font"), DefaultCustomFontsDir);

    private UnityArabicDisplayOptions ResolveUnityDisplayOptions() =>
        UnityArabicDisplayOptions.Parse(
            store.Get("unity_arabic_wrap"),
            store.Get("unity_arabic_font_scale"),
            store.Get("unity_arabic_shaping"));

    public const string GettextPoTargetSetting = "gettext_po_target";

    private static readonly string[] CjkFontNameTokens =
        ["cjk", "jp", "ja", "ko", "kr", "zh", "sc", "tc", "cn", "hans", "hant", "notosanssc", "notosansjp", "notosanskr"];

    private static List<ResourceEntry> FindPatchableLooseUnityFonts(Source source, ResourceIndex index)
    {
        if (source.IsArchive || !Directory.Exists(source.Path)) return [];
        var all = new List<ResourceEntry>();
        foreach (var entry in index.Entries)
        {
            string norm = entry.Path.Replace('\\', '/');
            if (!norm.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
            if (!norm.Contains("_Data/", StringComparison.OrdinalIgnoreCase)) continue;
            if (norm.Contains("/MonoBleedingEdge/", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("/Engine/", StringComparison.OrdinalIgnoreCase))
                continue;
            all.Add(entry);
        }
        if (all.Count == 0) return all;
        var nonCjk = all.Where(e =>
        {
            string stem = Path.GetFileNameWithoutExtension(e.Path).ToLowerInvariant();
            return !CjkFontNameTokens.Any(tok => stem.Contains(tok, StringComparison.Ordinal));
        }).ToList();
        return nonCjk.Count > 0 ? nonCjk : all;
    }

    private static ResourceEntry? FindPatchableUnityTmpHost(Source source, ResourceIndex index)
    {
        if (source.IsArchive || !Directory.Exists(source.Path)) return null;
        foreach (string targetName in new[] { "resources.assets", "data.unity3d" })
        {
            foreach (var entry in index.Entries)
            {
                if (!Path.GetFileName(entry.Path).Equals(targetName, StringComparison.OrdinalIgnoreCase)) continue;
                string full = Path.Combine(source.Path, entry.Path);
                if (UnityTmpArabicPatcher.CanPatchFile(full)) return entry;
            }
        }
        return null;
    }

    private BuildPlan Prepare(Source source, ResourceIndex index, bool final, CancellationToken cancellation,
        Action<ResourceEntry, byte[]>? stageChange = null,
        Func<ResourceEntry, string>? reserveStreamedRewrite = null,
        Action<ResourceEntry, string>? commitStreamedRewrite = null)
    {
        if (store.Get("scanned_source") != source.Root || store.Get("scanned_package") != source.Package)
            throw new InvalidOperationException("افحص المصدر المحدد قبل البناء.");
        if (HasBlockingScanErrors())
            throw new InvalidOperationException("راجع تشخيصات الفحص قبل البناء.");
        var skippedScanErrorPaths = ReadScanErrors()
            .Select(e => e.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.Ordinal);
        var profile = Engines.Resolve(store.Get("engine"), index.Entries.Select(entry => entry.Path).ToList(), source.Path);
        if (profile.Name != store.Get("scanned_engine"))
            throw new InvalidOperationException("تغير ملف تعريف المحرك. أعد الفحص.");
        if (store.Get("extraction_revision") != ExtractionRevision
            && (profile.Name == "rpgmaker" || index.Entries.Any(entry => Formats.For(entry.Path) is RenpyFormat)))
            throw new InvalidOperationException("تحديث قواعد الاستخراج يتطلب إعادة فحص المصدر قبل البناء.");
        var scanned = store.Query("SELECT path,hash FROM files WHERE active=1", row => (Path: row.GetString(0), Hash: row.GetString(1)))
            .ToDictionary(row => row.Path, row => row.Hash, StringComparer.Ordinal);
        if (index.Entries.Any(entry => source.FormatFor(entry.Path) is StarTruckerCatalog
            && store.Query("SELECT format FROM files WHERE path=$p AND active=1",
                row => row.GetString(0), ("$p", source.Key(entry.Path))).SingleOrDefault() != StarTruckerCatalog.Instance.Name))
            throw new InvalidOperationException("أعد فحص كتالوج Star Trucker قبل البناء.");
        var units = store.Query("""
            SELECT id,path,locator,source,target,context,status,warning,revision,imported FROM units
            WHERE active=1 AND target<>'' AND warning=''
            AND (status='approved' OR ($preview=1 AND status='translated'))
            ORDER BY path,locator
            """, ProjectStore.ReadUnit, ("$preview", !final)).ToLookup(unit => unit.Path, StringComparer.Ordinal);
        int blocked = store.Query("SELECT COUNT(*) FROM units WHERE active=1 AND (status='conflict' OR warning<>'')",
            row => row.GetInt32(0)).Single();
        var displayFont = ResolveDisplayFont();
        var unityDisplay = ResolveUnityDisplayOptions();
        Dictionary<string, string> expected = new(StringComparer.Ordinal);
        List<PackageChange> changes = [];
        List<PlannedResource> resources = [];
        List<UnityAddressablesCatalog.BundleUpdate> bundleUpdates = [];
        var unrealPatchGroups = new Dictionary<string, List<(string SourcePakPath, Dictionary<string, string> Replacements)>>(StringComparer.OrdinalIgnoreCase);
        bool arabicDisplay = false;
        bool i2ArabicUpdated = false;
        bool unityAssetsArabicUpdated = false;
        bool unityTmpPatchedInResource = false;
        bool anyNonI2UnityArabic = false;
        bool unrealPatchGenerated = false;
        bool unrealIoStorePatched = false;
        string notice = "";
        bool hasI2InIndex = profile.Name == "unity" && !source.IsArchive
            && index.Entries.Any(e => source.FormatFor(e.Path) is UnityI2Format);
        ResourceEntry? standaloneUnityTmpHost = profile.Name == "unity" && !source.IsArchive
            && !StarTruckerCatalog.IsGameArchive(source.Path)
            ? FindPatchableUnityTmpHost(source, index)
            : null;
        var looseUnityFonts = profile.Name == "unity" && !source.IsArchive
            && !StarTruckerCatalog.IsGameArchive(source.Path)
            && standaloneUnityTmpHost == null
            ? FindPatchableLooseUnityFonts(source, index)
            : [];
        string gettextPoTarget = store.Get(GettextPoTargetSetting).Trim();
        var gettextPotDeliveries = new List<(string PotPath, Dictionary<string, string> EffectiveReplacements, byte[] RewrittenPotBytes)>();
        foreach (var entry in index.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var format = source.FormatFor(entry.Path);
            if (format == null) continue;
            string key = source.Key(entry.Path);
            if (!scanned.ContainsKey(key) && skippedScanErrorPaths.Contains(key))
                continue;
            var selected = units[key];
            bool displaySupported = false;
            if (format is StarTruckerCatalog && selected.Any(unit => StarTruckerDisplay.HasArabic(unit.Target)))
            {
                string? unsupported = source.Package.Length == 0
                    ? "اختر مجلد اللعبة لبناء موارد الخط." : StarTruckerDisplay.UnsupportedReason(source.Root, source.Path, cancellation);
                displaySupported = unsupported == null;
                notice = displaySupported
                    ? "تجهيز العرض العربي يغطي جميع نصوص اللعبة المعتمدة (القوائم والحوارات والواجهات)."
                    : "تصدير ملف اللغة فقط. " + unsupported;
            }
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            bool shapedLabels = false;
            foreach (var unit in selected)
            {
                if (Tokens.Validate(unit.Source, unit.Target, profile.Name).Length != 0)
                {
                    blocked++;
                    continue;
                }
                if (!displaySupported || !StarTruckerDisplay.HasArabic(unit.Target))
                    replacements.Add(unit.Locator, unit.Target);
                else if (StarTruckerDisplay.Supports(unit))
                {
                    replacements.Add(unit.Locator, StarTruckerDisplay.Shape(unit.Target));
                    shapedLabels = true;
                }
                else blocked++;
            }
            if (replacements.Count > 0) resources.Add(new(key, replacements.Count));

            if (format is UnityI2Format && !source.IsArchive)
            {
                string beforeI2 = entry.Hash(cancellation);
                if (scanned.GetValueOrDefault(key) != beforeI2)
                    throw new InvalidOperationException("تغير المورد منذ الفحص — " + key);
                expected[entry.Path] = beforeI2;
                if (replacements.Count == 0) continue;

                bool hasArabicI2 = replacements.Values.Any(StarTruckerDisplay.HasArabic);
                if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
                {
                    string stagedPath = reserveStreamedRewrite(entry);
                    UnityI2Format.ApplyToFile(Path.Combine(source.Path, entry.Path), stagedPath, replacements, displayFont, cancellation, out bool patchedI2Tmp, unityDisplay);
                    if (patchedI2Tmp) unityTmpPatchedInResource = true;
                    string afterI2 = PackageInstaller.FileHash(stagedPath, cancellation);
                    if (beforeI2 == afterI2) continue;
                    expected[entry.Path] = afterI2;
                    changes.Add(new(entry.Path, beforeI2, afterI2));
                    commitStreamedRewrite(entry, stagedPath);
                }
                else
                {
                    changes.Add(new(entry.Path, beforeI2, beforeI2 + ":pending"));
                    if (hasArabicI2) unityTmpPatchedInResource = true;
                }
                if (hasArabicI2) i2ArabicUpdated = true;
                continue;
            }

            if (format is UnityAssetsFormat && !source.IsArchive)
            {
                string beforeAssets = entry.Hash(cancellation);
                if (scanned.GetValueOrDefault(key) != beforeAssets)
                    throw new InvalidOperationException("تغير المورد منذ الفحص — " + key);
                expected[entry.Path] = beforeAssets;
                if (replacements.Count == 0) continue;

                bool hasArabicAsset = replacements.Values.Any(StarTruckerDisplay.HasArabic);
                if (hasArabicAsset) anyNonI2UnityArabic = true;
                if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
                {
                    string stagedPath = reserveStreamedRewrite(entry);
                    UnityAssetsFormat.ApplyToFile(
                        Path.Combine(source.Path, entry.Path), stagedPath, replacements, displayFont, unityDisplay,
                        applyShaping: standaloneUnityTmpHost != null, cancellation, out bool patchedAssetTmp);
                    if (patchedAssetTmp) unityTmpPatchedInResource = true;
                    string afterAssets = PackageInstaller.FileHash(stagedPath, cancellation);
                    if (beforeAssets == afterAssets) continue;
                    expected[entry.Path] = afterAssets;
                    changes.Add(new(entry.Path, beforeAssets, afterAssets));
                    commitStreamedRewrite(entry, stagedPath);
                }
                else
                {
                    changes.Add(new(entry.Path, beforeAssets, beforeAssets + ":pending"));
                    if (hasArabicAsset && standaloneUnityTmpHost != null
                        && entry.Path.Equals(standaloneUnityTmpHost.Path, StringComparison.OrdinalIgnoreCase))
                        unityTmpPatchedInResource = true;
                }
                if (hasArabicAsset) unityAssetsArabicUpdated = true;
                continue;
            }

            if (format is UnrealPakFormat && !source.IsArchive)
            {
                string beforePak = entry.Hash(cancellation);
                if (scanned.GetValueOrDefault(key) != beforePak)
                    throw new InvalidOperationException("تغير المورد منذ الفحص — " + key);
                expected[entry.Path] = beforePak;
                if (replacements.Count == 0) continue;

                string fullPak = Directory.Exists(source.Path) ? Path.Combine(source.Path, entry.Path) : source.Path;
                if (UnrealPakFormat.HasCompanionUtoc(fullPak))
                {
                    // UE5 IoStore game: stream-patch the base .pak directly (preserving all untouched entries & .utoc pairing)
                    // so UE5 never rejects standalone _P.pak with "utoc not found".
                    if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
                    {
                        var (_, _, _, patched, _, _) = UnrealPakFormat.BuildPatchedLocresEntries(fullPak, replacements, displayFont, cancellation);
                        if (patched.Count == 0) continue;
                        string stagedPath = reserveStreamedRewrite(entry);
                        UnrealPakCodec.PatchExistingPak(fullPak, stagedPath, patched, cancellation);
                        string afterPak = PackageInstaller.FileHash(stagedPath, cancellation);
                        if (beforePak == afterPak) continue;
                        expected[entry.Path] = afterPak;
                        changes.Add(new(entry.Path, beforePak, afterPak));
                        commitStreamedRewrite(entry, stagedPath);
                    }
                    else
                    {
                        changes.Add(new(entry.Path, beforePak, beforePak + ":pending"));
                    }
                    unrealIoStorePatched = true;
                    continue;
                }

                string patchRelPath = UnrealPakFormat.PatchPakRelativePathFor(entry.Path);
                if (!unrealPatchGroups.TryGetValue(patchRelPath, out var group))
                {
                    group = [];
                    unrealPatchGroups[patchRelPath] = group;
                }
                group.Add((fullPak, replacements));
                continue;
            }

            byte[]? original = replacements.Count == 0 ? null : entry.Read(cancellation);
            string before = original == null ? entry.Hash(cancellation) : Identity.Hash(original);
            if (scanned.GetValueOrDefault(key) != before)
                throw new InvalidOperationException("تغير المورد منذ الفحص — " + key);
            expected[entry.Path] = before;
            if (original == null) continue;
            bool hasUnityFontTarget = standaloneUnityTmpHost != null || looseUnityFonts.Count > 0;
            bool shapeBundleOrLooseUnity = profile.Name == "unity" && !source.IsArchive
                && hasUnityFontTarget && !hasI2InIndex && unityDisplay.ShapeArabic
                && replacements.Values.Any(StarTruckerDisplay.HasArabic);
            if (profile.Name == "unity" && !source.IsArchive && replacements.Values.Any(StarTruckerDisplay.HasArabic))
                anyNonI2UnityArabic = true;

            byte[] rewritten;
            Dictionary<string, string> effectiveReplacements = replacements;
            if (format is UnityBundleFormat bundleFmt && shapeBundleOrLooseUnity)
            {
                rewritten = bundleFmt.ApplyWithDisplay(original, replacements, unityDisplay);
            }
            else if (shapeBundleOrLooseUnity && format is not StarTruckerCatalog)
            {
                var shapedMap = new Dictionary<string, string>(replacements.Count, StringComparer.Ordinal);
                foreach (var (loc, val) in replacements)
                    shapedMap[loc] = unityDisplay.TransformArabic(val);
                effectiveReplacements = shapedMap;
                rewritten = Formats.Apply(original, format, shapedMap, Rules(key));
            }
            else
            {
                rewritten = Formats.Apply(original, format, replacements, Rules(key));
            }
            bool redirectingPotToSiblingPo = format is PoFormat && !source.IsArchive && IsGettextTemplate(key)
                && gettextPoTarget.Length > 0
                && !gettextPoTarget.Equals("pot-only", StringComparison.OrdinalIgnoreCase)
                && !gettextPoTarget.Equals("export-ar", StringComparison.OrdinalIgnoreCase);
            if (format is PoFormat && !source.IsArchive && IsGettextTemplate(key)
                && gettextPoTarget.Length > 0
                && !gettextPoTarget.Equals("pot-only", StringComparison.OrdinalIgnoreCase))
            {
                gettextPotDeliveries.Add((entry.Path, effectiveReplacements, rewritten));
                if (redirectingPotToSiblingPo) continue;
            }
            string after = Identity.Hash(rewritten);
            if (before == after) continue;
            arabicDisplay |= shapedLabels;
            expected[entry.Path] = after;
            changes.Add(new(entry.Path, before, after));
            stageChange?.Invoke(entry, rewritten);
            if (format is UnityBundleFormat)
            {
                bundleUpdates.Add(new UnityAddressablesCatalog.BundleUpdate(
                    entry.Path,
                    UnityBundleFormat.ComputeUncompressedCrc32(original),
                    checked((uint)original.Length),
                    UnityBundleFormat.ComputeUncompressedCrc32(rewritten),
                    checked((uint)rewritten.Length)));
            }
        }
        if (expected.Count != scanned.Count || expected.Count == 0)
            throw new InvalidOperationException("تغيرت قائمة الموارد أو لم يُعثر على موارد مدعومة. أعد الفحص.");

        // Gettext .pot -> .po delivery (standalone Arabic .po or replacing a user-chosen sibling .po language slot)
        bool gettextPoDelivered = false;
        if (!source.IsArchive && gettextPotDeliveries.Count > 0)
        {
            var poFormat = new PoFormat();
            foreach (var (potPath, effReplacements, rewrittenPotBytes) in gettextPotDeliveries)
            {
                if (gettextPoTarget.Equals("export-ar", StringComparison.OrdinalIgnoreCase))
                {
                    string dir = Path.GetDirectoryName(potPath)?.Replace('\\', '/') ?? "";
                    string stem = Path.GetFileNameWithoutExtension(potPath);
                    string dropInPath = string.IsNullOrEmpty(dir) ? $"{stem}_ar.po" : $"{dir}/{stem}_ar.po";
                    string afterHash = Identity.Hash(rewrittenPotBytes);
                    expected[dropInPath] = afterHash;
                    changes.Add(new(dropInPath, "", afterHash));
                    stageChange?.Invoke(ResourceEntry.FromBytes(dropInPath, rewrittenPotBytes), rewrittenPotBytes);
                    gettextPoDelivered = true;
                    string poNote = $"تم تصدير ملف Gettext عربي جاهز ({Path.GetFileName(dropInPath)}) بجانب القالب.";
                    notice = notice.Length == 0 ? poNote : notice + "\n" + poNote;
                }
                else
                {
                    var siblingEntry = index.Entries.FirstOrDefault(e =>
                        e.Path.Equals(gettextPoTarget, StringComparison.OrdinalIgnoreCase)
                        || source.Key(e.Path).Equals(gettextPoTarget, StringComparison.OrdinalIgnoreCase));
                    if (siblingEntry != null && !changes.Any(c => c.Path.Equals(siblingEntry.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        byte[] siblingOrig = siblingEntry.Read(cancellation);
                        string siblingBefore = Identity.Hash(siblingOrig);
                        byte[] siblingRewritten = poFormat.Apply(siblingOrig, effReplacements, preserveLanguage: true);
                        string siblingAfter = Identity.Hash(siblingRewritten);
                        if (siblingBefore != siblingAfter)
                        {
                            expected[siblingEntry.Path] = siblingAfter;
                            changes.Add(new(siblingEntry.Path, siblingBefore, siblingAfter));
                            stageChange?.Invoke(siblingEntry, siblingRewritten);
                            gettextPoDelivered = true;
                            string poNote = $"تم تطبيق الترجمة العربية على ملف اللغة ({Path.GetFileName(siblingEntry.Path)}) مع الحفاظ على ترويسته الأصلية — اختر تلك اللغة من إعدادات اللعبة لتظهر العربية.";
                            notice = notice.Length == 0 ? poNote : notice + "\n" + poNote;
                        }
                    }
                }
            }
        }

        if (!source.IsArchive && unrealPatchGroups.Count > 0)
        {
            var unrealFont = ArabicFontCatalog.ResolveForUnreal(displayFont).Choice;
            var patchFileNames = new List<string>();
            foreach (var (patchRelPath, group) in unrealPatchGroups)
            {
                cancellation.ThrowIfCancellationRequested();
                patchFileNames.Add(Path.GetFileName(patchRelPath));
                if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
                {
                    string mountPoint = "../../../";
                    int targetVersion = 11;
                    ulong pathHashSeed = 0x9B56FB58ul;
                    var allPatchedByPath = new Dictionary<string, UnrealPakPatchFile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (sourcePakPath, pakReplacements) in group)
                    {
                        var (mp, ver, seed, patched, _, _) = UnrealPakFormat.BuildPatchedLocresEntries(sourcePakPath, pakReplacements, displayFont, cancellation);
                        if (patched.Count > 0)
                        {
                            mountPoint = mp;
                            targetVersion = ver;
                            if (seed != 0) pathHashSeed = seed;
                            foreach (var pf in patched)
                                allPatchedByPath[pf.RelativePath] = pf;
                        }
                    }
                    if (allPatchedByPath.Count == 0) continue;

                    var dummyEntry = new ResourceEntry(patchRelPath, 0, (_, _) => { });
                    string stagedPath = reserveStreamedRewrite(dummyEntry);
                    UnrealPakCodec.WritePatchPak(stagedPath, mountPoint, [.. allPatchedByPath.Values], targetVersion, cancellation, pathHashSeed);
                    string afterPatchHash = PackageInstaller.FileHash(stagedPath, cancellation);
                    expected[patchRelPath] = afterPatchHash;
                    changes.Add(new(patchRelPath, "", afterPatchHash));
                    commitStreamedRewrite(dummyEntry, stagedPath);
                }
                else
                {
                    changes.Add(new(patchRelPath, "", "pending"));
                }
                unrealPatchGenerated = true;
            }
            if (unrealPatchGenerated)
            {
                string names = string.Join("، ", patchFileNames.Distinct(StringComparer.OrdinalIgnoreCase));
                string ueNote = $"تم توليد حزمة تعديل Unreal جاهزة ({names}) مع حقن الخط العربي الثنائي ({unrealFont.DisplayName}) داخل Content/Paks/ دون تعديل ملفات اللعبة الأصلية.";
                notice = notice.Length == 0 ? ueNote : notice + "\n" + ueNote;
            }
        }
        if (unrealIoStorePatched)
        {
            var unrealFont = ArabicFontCatalog.ResolveForUnreal(displayFont).Choice;
            string ioNote = $"تم تحديث حزمة Unreal Engine 5 (IoStore .pak) بالتدفّق المباشر مع حقن الخط العربي الثنائي ({unrealFont.DisplayName}) وحفظ نسخة احتياطية كاملة للاستعادة بضغطة زر.";
            notice = notice.Length == 0 ? ioNote : notice + "\n" + ioNote;
        }

        if (!source.IsArchive && bundleUpdates.Count > 0)
        {
            var catalogPatches = UnityAddressablesCatalog.PreparePatches(source.Root, bundleUpdates, cancellation);
            foreach (var patch in catalogPatches)
            {
                string afterHash = Identity.Hash(patch.RewrittenBytes);
                expected[patch.RelativePath] = afterHash;
                changes.Add(new(patch.RelativePath, patch.OriginalHash, afterHash));
                var catalogEntry = index.Entries.FirstOrDefault(e => e.Path == patch.RelativePath)
                    ?? ResourceEntry.FromBytes(patch.RelativePath, patch.RewrittenBytes);
                stageChange?.Invoke(catalogEntry, patch.RewrittenBytes);
            }
            if (catalogPatches.Count > 0 && notice.Length == 0)
            {
                string catalogNames = string.Join(", ", catalogPatches.Select(p => Path.GetFileName(p.RelativePath)));
                notice = $"تم تحديث حزم Unity Localization ومزامنة فهرس Addressables ({catalogNames}) تلقائياً.";
            }
        }
        bool unityTmpStandalonePatched = false;
        if (!source.IsArchive && anyNonI2UnityArabic && !unityTmpPatchedInResource && standaloneUnityTmpHost != null
            && !changes.Any(c => c.Path.Equals(standaloneUnityTmpHost.Path, StringComparison.OrdinalIgnoreCase)))
        {
            string hostFullPath = Path.Combine(source.Path, standaloneUnityTmpHost.Path);
            string beforeHost = standaloneUnityTmpHost.Hash(cancellation);
            if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
            {
                string stagedPath = reserveStreamedRewrite(standaloneUnityTmpHost);
                if (UnityTmpArabicPatcher.PatchFile(hostFullPath, stagedPath, displayFont, unityDisplay, cancellation))
                {
                    string afterHost = PackageInstaller.FileHash(stagedPath, cancellation);
                    if (beforeHost != afterHost)
                    {
                        expected[standaloneUnityTmpHost.Path] = afterHost;
                        changes.Add(new(standaloneUnityTmpHost.Path, beforeHost, afterHost));
                        commitStreamedRewrite(standaloneUnityTmpHost, stagedPath);
                        unityTmpStandalonePatched = true;
                    }
                }
            }
            else
            {
                changes.Add(new(standaloneUnityTmpHost.Path, beforeHost, beforeHost + ":pending"));
                unityTmpStandalonePatched = true;
            }
        }
        bool unityLooseFontsPatched = false;
        var patchedLooseFontNames = new List<string>();
        ArabicFontChoice? looseFontChoice = null;
        if (!source.IsArchive && anyNonI2UnityArabic && !unityTmpPatchedInResource && !unityTmpStandalonePatched && looseUnityFonts.Count > 0)
        {
            var (bilingualChoice, patchedFontBytes) = ArabicFontCatalog.ResolveBilingual(displayFont);
            string afterFontHash = Identity.Hash(patchedFontBytes);
            looseFontChoice = bilingualChoice;
            foreach (var fontEntry in looseUnityFonts)
            {
                if (changes.Any(c => c.Path.Equals(fontEntry.Path, StringComparison.OrdinalIgnoreCase))) continue;
                string beforeFontHash = fontEntry.Hash(cancellation);
                if (beforeFontHash == afterFontHash) continue;
                expected[fontEntry.Path] = afterFontHash;
                changes.Add(new(fontEntry.Path, beforeFontHash, afterFontHash));
                stageChange?.Invoke(fontEntry, patchedFontBytes);
                patchedLooseFontNames.Add(Path.GetFileName(fontEntry.Path));
                unityLooseFontsPatched = true;
            }
        }
        if (i2ArabicUpdated)
        {
            string shapingDesc = unityDisplay.ShapeArabic ? $"تشكيل العربية ({unityDisplay.WrapLabel})" : "عربي منطقي (RTL أصلي)";
            string i2Note = $"تم تحديث I2 Localization وتجهيز العرض ({shapingDesc}) وحقن الخط الاحتياطي (TextMeshPro) · الخط: {displayFont.DisplayName} · الحجم: {unityDisplay.ScaleLabel}";
            notice = notice.Length == 0 ? i2Note : notice + "\n" + i2Note;
        }
        else if (unityAssetsArabicUpdated || unityTmpStandalonePatched)
        {
            string shapingDesc = unityDisplay.ShapeArabic ? $"تشكيل العربية ({unityDisplay.WrapLabel})" : "عربي منطقي (RTL أصلي)";
            string tmpNote = (unityTmpPatchedInResource || unityTmpStandalonePatched)
                ? $"تم تجهيز العرض العربي ({shapingDesc}) وحقن الخط الاحتياطي (TextMeshPro) تلقائياً · الخط: {displayFont.DisplayName} · الحجم: {unityDisplay.ScaleLabel}"
                : $"تم تحديث موارد Unity (TextAsset) بنجاح.";
            notice = notice.Length == 0 ? tmpNote : notice + "\n" + tmpNote;
        }
        else if (unityLooseFontsPatched && looseFontChoice != null)
        {
            string shapingDesc = unityDisplay.ShapeArabic ? $"تشكيل العربية ({unityDisplay.WrapLabel})" : "عربي منطقي (RTL أصلي)";
            string fontNote = $"تم حقن الخط العربي+اللاتيني الثنائي ({looseFontChoice.DisplayName}) في خطوط اللعبة ({string.Join("، ", patchedLooseFontNames)}) وتجهيز العرض ({shapingDesc}).";
            notice = notice.Length == 0 ? fontNote : notice + "\n" + fontNote;
        }

        string package = source.IsArchive ? source.OutputPath : "";
        IReadOnlyList<string> changedFiles = changes.Select(change => change.Path).Order(StringComparer.Ordinal).ToList();
        if (source.IsArchive && changes.Count > 0)
            changedFiles = arabicDisplay ? [package, ..StarTruckerDisplay.AssetPaths] : [package];
        var plan = new PackagePreview(package, final, resources, blocked, changedFiles)
        {
            Notice = arabicDisplay ? $"{notice} · الخط: {displayFont.DisplayName}" : notice,
            Fingerprint = Identity.Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                source.Root,
                source.Package,
                scan = store.Get("scan_version"),
                final,
                blocked,
                arabicDisplay,
                i2ArabicUpdated,
                unityAssetsArabicUpdated,
                unityTmpStandalonePatched,
                unityLooseFontsPatched,
                gettextPoTarget,
                gettextPoDelivered,
                displayFont = displayFont.Id,
                unityWrap = unityDisplay.WrapMode,
                unityFontScale = unityDisplay.FontScale,
                unityShapeArabic = unityDisplay.ShapeArabic,
                notice,
                inputs = scanned.OrderBy(entry => entry.Key, StringComparer.Ordinal),
                changedFiles,
                revisions = units.SelectMany(group => group.Select(unit => new { unit.Id, unit.Revision }))
            }))
        };
        return new(plan, profile, expected, changes, arabicDisplay);
    }

    private BuildResult BuildSnapshot(string destination, bool final, string? password, CancellationToken cancellation, PackagePreview? reviewed)
    {
        destination = Path.GetFullPath(destination);
        var source = CurrentSource(cancellation);
        Identity.RequireNoLinks(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Choose a new output directory for each build.");
        if (Identity.Within(destination, source.Root))
            throw new IOException("Output must be outside the source.");

        string? archiveHash = source.IsArchive ? PackageInstaller.FileHash(source.Path, cancellation) : null;
        using var index = Open(source, password, cancellation);
        string staging = destination + ".building-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            Dictionary<string, ResourceEntry> rewrites = new(StringComparer.Ordinal);
            string rewriteDirectory = Path.Combine(staging, "rewrites");
            int streamedCounter = 0;
            var plan = Prepare(source, index, final, cancellation,
                stageChange: (resource, bytes) =>
                {
                    Directory.CreateDirectory(rewriteDirectory);
                    string path = Path.Combine(rewriteDirectory, "mem-" + rewrites.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.WriteAllBytes(path, bytes);
                    long length = bytes.LongLength;
                    rewrites.Add(resource.Path, new(resource.Path, length, (target, token) =>
                    {
                        using var input = File.OpenRead(path);
                        Containers.Copy(input, target, length, token);
                    }));
                },
                reserveStreamedRewrite: _ =>
                {
                    Directory.CreateDirectory(rewriteDirectory);
                    return Path.Combine(rewriteDirectory, "stream-" + (streamedCounter++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                },
                commitStreamedRewrite: (resource, stagedPath) =>
                {
                    long length = new FileInfo(stagedPath).Length;
                    rewrites.Add(resource.Path, new(resource.Path, length, (target, token) =>
                    {
                        using var input = File.OpenRead(stagedPath);
                        Containers.Copy(input, target, length, token);
                    }));
                });
            if (reviewed != null && plan.Preview.Fingerprint != reviewed.Fingerprint)
                throw new InvalidOperationException("تغيّرت خطة البناء. راجع القائمة وأعد المحاولة.");

            var expected = plan.ExpectedHashes;
            bool overlayOnly = !source.IsArchive && Directory.Exists(source.Root) && plan.Profile.Name is "unity" or "unreal";
            var output = overlayOnly
                ? rewrites.Values.ToList()
                : index.Entries.Select(resource =>
                {
                    if (rewrites.TryGetValue(resource.Path, out var rewrite)) return rewrite;
                    if (expected.ContainsKey(resource.Path)) return resource;
                    return resource with
                    {
                        CopyTo = (target, token) => expected[resource.Path] = resource.CopyAndHash(target, token)
                    };
                }).ToList();
            string payload = source.IsArchive ? "archive" : "resources";
            // Preserve the original standalone package layout for existing CLI consumers.
            if (source.IsArchive && source.Package.Length == 0) payload = ".";
            string target = Path.Combine(staging, payload, source.IsArchive ? source.OutputPath : "");
            Directory.CreateDirectory(source.IsArchive ? Path.GetDirectoryName(target)! : target);
            source.Adapter.WriteIndexed(target, output, password, cancellation, index);
            List<object> manifestFiles = [];
            using (var readBack = source.Adapter.Index(target, password, cancellation))
            {
                if (readBack.Entries.Count != output.Count) throw new IOException("Rebuilt container verification failed.");
                foreach (var entry in readBack.Entries)
                {
                    string hash = entry.Hash(cancellation);
                    if (!expected.TryGetValue(entry.Path, out var hashBeforeWrite) || hash != hashBeforeWrite)
                        throw new IOException("Rebuilt container verification failed: " + entry.Path);
                    manifestFiles.Add(new { path = entry.Path, hash });
                }
            }
            var changes = plan.Changes;
            if (source.IsArchive)
            {
                if (archiveHash != PackageInstaller.FileHash(source.Path, cancellation))
                    throw new IOException("Source archive changed during the build.");
                if (changes.Count > 0) changes = [new(source.OutputPath, archiveHash!, PackageInstaller.FileHash(target, cancellation))];
            }
            var displayFont = ResolveDisplayFont();
            var unityDisplay = ResolveUnityDisplayOptions();
            if (plan.ArabicDisplay)
            {
                var assets = StarTruckerDisplay.WriteAssets(source.Root, Path.Combine(staging, payload), cancellation, displayFont);
                changes.AddRange(assets);
                manifestFiles.AddRange(assets.Select(asset => new { path = asset.Path, hash = asset.LocalizedHash }));
                StarTruckerDisplay.WriteLicense(staging, displayFont);
            }
            if (Directory.Exists(rewriteDirectory)) Directory.Delete(rewriteDirectory, recursive: true);
            PackageInstaller.WriteManifest(staging, new(payload, changes));
            int translated = plan.Preview.Resources.Sum(resource => resource.Translations);
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new
            {
                project = store.Get("name"),
                engine = plan.Profile.Name,
                mode = final ? "final" : "preview",
                container = plan.Preview.Package,
                translations = translated,
                files = manifestFiles,
                display = new
                {
                    arabic = plan.ArabicDisplay,
                    font = displayFont.DisplayName,
                    unityWrap = unityDisplay.WrapMode,
                    unityFontScale = unityDisplay.FontScale,
                    unityShapeArabic = unityDisplay.ShapeArabic,
                    notice = plan.Preview.Notice
                },
                deployment = plan.Profile.Deployment
            }, new JsonSerializerOptions { WriteIndented = true }));
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return new(destination, plan.ArabicDisplay ? changes.Count : output.Count, translated);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private const int DedicatedLocalizationMinHumanUnits = 20;

    private static readonly HashSet<string> AmbiguousShortLocaleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "no", "it", "he", "hi"
    };

    private static readonly HashSet<string> ExplicitLocalizationDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "localization", "localisation", "locales", "locale", "lang", "languages",
        "i18n", "l10n", "dialogues", "dialogs", "subtitles", "captions", "tl"
    };

    private static readonly HashSet<string> LocalizationContextFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "localization", "localisation", "locales", "locale", "lang", "languages",
        "i18n", "l10n", "strings", "dialogues", "dialogs", "subtitles", "captions", "tl", "text"
    };

    private static readonly string[] LocalizationFileStems =
    [
        "strings_", "strings-", "locale_", "locale-", "lang_", "lang-",
        "loc_", "loc-", "i18n_", "i18n-", "l10n_", "l10n-", "subtitles_", "dialogue_", "dialog_"
    ];

    private static readonly HashSet<string> DedicatedLocalizationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".po", ".pot", ".locres", ".translation", ".xlf", ".xliff"
    };

    private static readonly HashSet<string> KnownLocaleCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "eng", "english", "en-us", "en-gb", "en_us", "en_gb",
        "de", "deu", "ger", "german", "de-de", "de_de",
        "fr", "fra", "fre", "french", "fr-fr", "fr_fr",
        "es", "esp", "spa", "spanish", "es-es", "es-419", "es-mx", "es_es", "es_419", "es_mx",
        "it", "ita", "italian", "it-it", "it_it",
        "ja", "jpn", "jp", "japanese", "ja-jp", "ja_jp",
        "ko", "kor", "kr", "korean", "ko-kr", "ko_kr",
        "pt", "por", "ptb", "pt-br", "pt-pt", "pt_br", "pt_pt", "portuguese",
        "ru", "rus", "russian", "ru-ru", "ru_ru",
        "zh", "zho", "zhs", "zht", "chs", "cht", "zh-cn", "zh-tw", "zh_cn", "zh_tw", "zh-hans", "zh-hant", "zh_hans", "zh_hant", "chinese",
        "pl", "pol", "polish", "tr", "tur", "turkish", "th", "tha", "thai",
        "nl", "nld", "dutch", "sv", "swe", "swedish", "da", "dan", "danish",
        "fi", "fin", "finnish", "no", "nor", "nb", "norwegian",
        "cs", "ces", "cze", "czech", "hu", "hun", "hungarian", "ro", "ron", "romanian",
        "uk", "ukr", "ukrainian", "vi", "vie", "vietnamese", "id", "ind", "indonesian",
        "ar", "ara", "arabic", "he", "heb", "hebrew", "el", "ell", "greek", "hi", "hin", "bg", "bul"
    };

    private static string StripContainerPrefix(string resourcePath)
    {
        int bar = resourcePath.LastIndexOf(" | ", StringComparison.Ordinal);
        return (bar >= 0 ? resourcePath[(bar + 3)..] : resourcePath).Replace('\\', '/');
    }

    internal static bool IsDedicatedLocalizationFormat(string resourcePath, string formatName)
    {
        if (formatName is "po" or "unreal-locres" or "unreal-pak" or "unity-i2" or "unity-bundle"
            or "godot-translation" or "renpy-template" or "xunity-autotranslator" or "star-trucker-catalog")
            return true;
        if (formatName == "xml/xliff")
        {
            string ext = Path.GetExtension(StripContainerPrefix(resourcePath));
            return ext.Equals(".xlf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".xliff", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    internal static bool IsExplicitLocalizationDirectory(string resourcePath)
    {
        string[] parts = StripContainerPrefix(resourcePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (ExplicitLocalizationDirectories.Contains(parts[i]))
                return true;
        }
        return false;
    }

    private static bool IsLocalizationContextPath(string normalizedInnerPath)
    {
        string[] parts = normalizedInnerPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (LocalizationContextFolders.Contains(parts[i]))
                return true;
        }
        string fileName = parts.Length > 0 ? parts[^1] : "";
        foreach (string stem in LocalizationFileStems)
        {
            if (fileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool IsGettextTemplate(string resourcePath)
    {
        string p = StripContainerPrefix(resourcePath);
        if (!Path.GetExtension(p).Equals(".pot", StringComparison.OrdinalIgnoreCase))
            return false;
        string? locale = ExtractPathLocale(resourcePath);
        return locale == null || IsEnglishLocaleCode(locale);
    }

    internal static bool IsPrimaryLocaleSource(string resourcePath) =>
        IsEnglishLocaleCode(ExtractPathLocale(resourcePath)) || IsGettextTemplate(resourcePath);

    private static string? FindMainGodotPck(string root, CancellationToken cancellation)
    {
        if (!Directory.Exists(root)) return null;
        string? best = null;
        long bestSize = -1;
        var pck = new PckContainer();
        foreach (string file in Directory.EnumerateFiles(root, "*.pck", SearchOption.TopDirectoryOnly))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var idx = pck.Index(file, null, cancellation);
                if (!idx.Entries.Any(e => e.Path is "project.godot" or "project.binary"))
                    continue;
                long len = new FileInfo(file).Length;
                if (len > bestSize)
                {
                    bestSize = len;
                    best = Identity.SafePath(Path.GetRelativePath(root, file));
                }
            }
            catch
            {
                // Skip unreadable/encrypted/non-Godot .pck files
            }
        }
        return best;
    }

    internal static bool IsEnglishLocaleCode(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && (code.Equals("en", StringComparison.OrdinalIgnoreCase)
            || code.Equals("eng", StringComparison.OrdinalIgnoreCase)
            || code.Equals("english", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en_", StringComparison.OrdinalIgnoreCase));

    internal static string? ExtractPathLocale(string resourcePath)
    {
        string p = StripContainerPrefix(resourcePath);
        string[] parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string seg = parts[i];
            if (KnownLocaleCodes.Contains(seg))
                return seg;
        }
        string fileNoExt = Path.GetFileNameWithoutExtension(p);
        if (KnownLocaleCodes.Contains(fileNoExt))
            return fileNoExt;

        int dot = fileNoExt.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < fileNoExt.Length)
        {
            string suffix = fileNoExt[(dot + 1)..];
            if (KnownLocaleCodes.Contains(suffix))
                return suffix;
        }

        string[] tokens = fileNoExt.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2)
        {
            // Compound BCP-47 locale pairs (e.g. zh_cn, pt_br, en_us, zh-hans) are unambiguous anywhere in the filename.
            for (int i = tokens.Length - 2; i >= 0; i--)
            {
                string pair = tokens[i] + "_" + tokens[i + 1];
                if (KnownLocaleCodes.Contains(pair))
                    return pair;
            }

            string ext = Path.GetExtension(p);
            bool dedicatedExt = DedicatedLocalizationExtensions.Contains(ext);
            bool inLocContext = dedicatedExt || IsLocalizationContextPath(p);
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string tok = tokens[i];
                if (!KnownLocaleCodes.Contains(tok)) continue;
                // Full language names (e.g. english, french, korean, russian) are unambiguous anywhere.
                if (tok.Length >= 5)
                    return tok;
                if (!inLocContext) continue;
                if (tok.Length >= 3)
                    return tok;
                if (!AmbiguousShortLocaleTokens.Contains(tok))
                    return tok;
                if (dedicatedExt && i == tokens.Length - 1)
                    return tok;
            }
        }
        return null;
    }

    public XUnityExportResult ExportXUnityAutoTranslator(string destination, bool final = false)
    {
        var allUnits = store.Query("""
            SELECT id,path,locator,source,target,context,status,warning,revision,imported FROM units
            WHERE active=1 AND target<>'' AND warning=''
            ORDER BY path,locator
            """, ProjectStore.ReadUnit);
        var displayFont = ResolveDisplayFont();
        var unityDisplay = ResolveUnityDisplayOptions();
        string engineName = store.Get("scanned_engine");
        if (string.IsNullOrWhiteSpace(engineName)) engineName = "unity";
        return XUnityAutoTranslatorExporter.Export(destination, allUnits, displayFont, unityDisplay, final, engineName);
    }
}
