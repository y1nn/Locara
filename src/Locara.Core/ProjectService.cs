namespace Locara.Core;

public sealed partial class ProjectService(ProjectStore store)
{
    private readonly ProjectSettings settings = new(store);

    private sealed record Source(string Root, string Package, byte[]? UnrealAesKey = null)
    {
        public string Path => Package.Length == 0 ? Root : System.IO.Path.Combine(Root, Package);
        public string Key(string path) => Package.Length == 0 ? path : Package + " | " + path;
        public bool IsArchive => Containers.IsArchive(Path) && File.Exists(Path);
        public IContainerAdapter Adapter => IsArchive ? Containers.For(Path) : new FolderContainer();
        public string OutputPath => Package.Length == 0 ? System.IO.Path.GetFileName(Path) : Package;
        private bool IsGameCatalog => StarTruckerCatalog.IsGameArchive(Path) && IsArchive;
        private bool? hasArabicTemplates;
        private Dictionary<string, IFormatHandler?>? probeCache;
        public List<Diagnostic> FormatDiagnostics { get; } = [];

        public IFormatHandler? FormatFor(string resource)
        {
            if (IsGameCatalog)
                return StarTruckerCatalog.Instance.Supports(resource) ? StarTruckerCatalog.Instance : null;
            if (Engines.IsEngineInternalMetadata(resource))
                return null;
            hasArabicTemplates ??= GameRuntime.HasArabicTemplates(Path);
            if (hasArabicTemplates.Value && !GameRuntime.IsArabicTemplate(resource))
                return null;

            probeCache ??= new(StringComparer.OrdinalIgnoreCase);
            if (probeCache.TryGetValue(resource, out var cached))
                return cached;

            bool isFolder = Directory.Exists(Path);
            string? fullDiskPath = isFolder
                ? System.IO.Path.Combine(Path, resource)
                : (!IsArchive && File.Exists(Path) ? Path : null);
            string? singleFileMatchPath = !isFolder && !IsArchive && fullDiskPath != null
                ? fullDiskPath.Replace('\\', '/')
                : null;

            foreach (var provider in LocalizationProviderRegistry.All)
            {
                string? matchPath = provider.MatchesPath(resource)
                    ? resource
                    : (singleFileMatchPath != null && provider.MatchesPath(singleFileMatchPath) ? singleFileMatchPath : null);
                if (matchPath == null) continue;
                var result = provider.Probe(matchPath, fullDiskPath, UnrealAesKey);
                if (!result.Supported && result.Diagnostic != null)
                    FormatDiagnostics.Add(new(resource, result.Diagnostic));
                var resolved = result.Supported ? (result.ResolvedHandler ?? provider.Handler) : null;
                probeCache[resource] = resolved;
                return resolved;
            }

            probeCache[resource] = null;
            return null;
        }
    }

    private const string LooseFiles = "loose";
    private const string ExtractionRevision = "8";
    private const int DedicatedLocalizationMinHumanUnits = 20;

    public const string GettextPoTargetSetting = ProjectSettings.GettextPoTargetKey;
    public const string UnrealAesKeySetting = ProjectSettings.UnrealAesKeySetting;

    public static string InitialSearch(string source) => "";

    // Backward-compatible static delegators to LocaleClassifier
    public static bool IsGettextTemplate(string resourcePath) => LocaleClassifier.IsGettextTemplate(resourcePath);
    internal static bool IsExplicitLocalizationDirectory(string resourcePath) => LocaleClassifier.IsExplicitLocalizationDirectory(resourcePath);
    internal static bool IsPrimaryLocaleSource(string resourcePath) => LocaleClassifier.IsPrimaryLocaleSource(resourcePath);
    internal static bool IsEnglishLocaleCode(string? code) => LocaleClassifier.IsEnglishLocaleCode(code);
    internal static string? ExtractPathLocale(string resourcePath) => LocaleClassifier.ExtractPathLocale(resourcePath);
    internal static bool IsDedicatedLocalizationFormat(string resourcePath, string formatName) =>
        LocaleClassifier.IsDedicatedLocalizationResource(resourcePath, formatName);

    public void EnsureCatalogSelected()
    {
        string root = store.Info.Source;
        if (root.Length == 0 || !Directory.Exists(root)) return;
        string catalog = Path.Combine(root, StarTruckerCatalog.LanguageArchive);
        if (!File.Exists(catalog) || !Containers.IsArchive(catalog)) return;
        if (settings.SelectedPackageRaw != LooseFiles && settings.ScannedPackage.Length > 0) return;

        string catalogResourceKey = StarTruckerCatalog.LanguageArchive + " | strings.xml";
        bool hasScannedCatalog = store.Query("SELECT EXISTS(SELECT 1 FROM files WHERE path=$p)",
            r => r.GetBoolean(0), ("$p", catalogResourceKey)).Single();
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            settings.SelectedPackageRaw = StarTruckerCatalog.LanguageArchive;
            if (hasScannedCatalog)
            {
                store.Execute("UPDATE files SET active = CASE WHEN path=$p THEN 1 ELSE 0 END", ("$p", catalogResourceKey));
                store.Execute("UPDATE units SET active = CASE WHEN path=$p THEN 1 ELSE 0 END", ("$p", catalogResourceKey));
                settings.ScannedSource = root;
                settings.ScannedPackage = StarTruckerCatalog.LanguageArchive;
                settings.ScannedEngine = "generic";
                settings.WriteScanErrors([]);
                settings.TouchScanVersion();
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
    }

    private Source CurrentSource(CancellationToken cancellation)
    {
        string root = store.Info.Source;
        if (root.Length == 0) throw new InvalidOperationException("اختر مصدر المشروع أولاً.");
        byte[]? aesKey = settings.ResolveUnrealAesKeyBytes();
        if (!Directory.Exists(root))
        {
            if (!File.Exists(root)) throw new FileNotFoundException("مصدر المشروع غير موجود.", root);
            if (!Containers.IsArchive(root) && Formats.For(root) == null) throw new NotSupportedException("صيغة الملف غير مدعومة.");
            return new(root, "", aesKey);
        }
        EnsureCatalogSelected();
        string choice = PackageChoice(root);
        if (choice == LooseFiles) return new(root, "", aesKey);
        if (choice.Length != 0) return new(root, Identity.SafePath(choice), aesKey);

        string catalog = Path.Combine(root, StarTruckerCatalog.LanguageArchive);
        if (File.Exists(catalog) && Containers.IsArchive(catalog))
            return new(root, StarTruckerCatalog.LanguageArchive, aesKey);

        string? mainGodotPck = FindMainGodotPck(root, cancellation);
        if (mainGodotPck != null)
            return new(root, mainGodotPck, aesKey);

        using var index = new FolderContainer().Index(root, null, cancellation);
        var packages = index.Entries.Where(entry => Containers.IsArchive(Path.Combine(root, entry.Path))).ToList();
        var archivePaths = packages.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var probe = new Source(root, "", aesKey);
        if (index.Entries.Any(entry => !archivePaths.Contains(entry.Path) && probe.FormatFor(entry.Path) != null))
            return new(root, "", aesKey);

        return packages.Count switch
        {
            0 => new(root, "", aesKey),
            1 => new(root, packages[0].Path, aesKey),
            _ => throw new InvalidOperationException("توجد عدة حزم داخل مجلد اللعبة. اختر الحزمة التي تريد فحصها.")
        };
    }

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

    public bool HasBlockingScanErrors()
    {
        var errors = settings.ReadScanErrors();
        if (errors.Count == 0) return false;
        string sourceRoot = settings.ScannedSource;
        string scannedPackage = settings.ScannedPackage;
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
        bool scanned = settings.ScannedSource == source.Root && settings.ScannedPackage == source.Package
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
            bool hasStringTable = paths.Any(path => source.FormatFor(path) is UnrealStringTableFormat);
            bool hasIoStore = paths.Any(path => path.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
            var launch = hasText && isSteamGame ? GameLaunch.Steam : GameLaunch.None;
            return new(profile.Name, "", hasText, scanned, launch,
                hasUnrealPak
                    ? "لعبة Unreal Engine 4/5 — يقرأ Locara نصوص .locres وجداول StringTable (.uasset) مباشرة من حزم .pak ويولّد حزمة تعديل جاهزة (*_Locara_P.pak) أو يحدّث حزم UE5 IoStore مع التثبيت المباشر وتجربة Steam."
                    : hasLocres || hasStringTable
                        ? "موارد Unreal Engine (.locres / StringTable .uasset) — قراءة وإعادة بناء ثنائية مباشرة مع دعم UTF-16LE للعربية."
                        : hasText
                            ? "لعبة Unreal بملفات نصية مفتوحة قابلة للترجمة والتثبيت المباشر."
                            : hasIoStore
                                ? "لعبة Unreal Engine 5 (IoStore .utoc/.ucas) — لم يُعثر على ملفات .locres أو جداول StringTable غير مشفّرة داخل حزم .pak."
                                : "لعبة Unreal Engine مجمّعة — لم يُعثر على ملفات .locres أو StringTable غير مشفّرة داخل حزم .pak (إذا كانت الحزمة مشفّرة بمفتاح AES يمكنك إدخال المفتاح في الخيارات المتقدمة ثم إعادة الفحص).",
                hasUnrealPak || hasLocres || hasStringTable
                    ? "يدعم Unreal Engine العرض العربي واتجاه RTL أصلياً عبر HarfBuzz/ICU (تُكتب النصوص بترميز UTF-16LE المنطقي دون حاجة لتشكيل مسبق)."
                    : "");
        }
        if (profile.Name == "gamemaker")
        {
            var launch = hasText && isSteamGame ? GameLaunch.Steam : GameLaunch.None;
            return new("gamemaker", "", hasText, scanned, launch,
                hasText
                    ? "لعبة GameMaker بملفات ترجمة خارجية — يدعم Locara الترجمة والتثبيت الموثّق وتجربة Steam المباشرة."
                    : "لعبة GameMaker مجمّعة (data.win) — لم يُعثر على ملفات ترجمة خارجية مفتوحة بجانب حزمة data.win.",
                hasText
                    ? "تُحفظ ملفات الترجمة بترميز UTF-8؛ يعتمد عرض الحروف العربية المتصلة على دعم الخط المستخدم داخل اللعبة."
                    : "");
        }
        if (!hasText && profile.Name == "generic")
        {
            var customArchives = FindProprietaryArchives(index.Entries);
            if (customArchives.Count > 0)
            {
                return new(profile.Name, "", false, scanned, GameLaunch.None,
                    $"يحتوي المجلد على حزم ثنائية مخصّصة ({string.Join("، ", customArchives)}) بلا ملفات ترجمة مفتوحة — يُرجّح أن اللعبة تستخدم محركاً خاصاً.", "");
            }
            return new(profile.Name, "", false, scanned, GameLaunch.None,
                "لم يُعثر على ملفات نصية مدعومة في هذا المصدر.", "");
        }

        return new(profile.Name, "", hasText, scanned, hasText && isSteamGame ? GameLaunch.Steam : GameLaunch.None,
            "صدّر الموارد المعاد بناؤها أو ثبّتها مباشرة داخل اللعبة. افتح إعداد الاستخراج لاختيار الحقول أو الأعمدة.", "");
    }

    private static readonly HashSet<string> ExplicitProprietaryArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pack", ".vpk", ".bsa", ".ba2", ".arc", ".wad", ".forge", ".rpf", ".cas", ".big", ".rpa"
    };

    private static readonly HashSet<string> GenericBinaryArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pak", ".dat", ".bin", ".blob", ".res"
    };

    internal static IReadOnlyList<string> FindProprietaryArchives(IReadOnlyList<ResourceEntry> entries)
    {
        var candidates = new List<(string Name, long Size)>();
        foreach (var entry in entries)
        {
            string norm = entry.Path.Replace('\\', '/');
            if (norm.Contains("/Mono/", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("/MonoBleedingEdge/", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase))
                continue;
            string ext = Path.GetExtension(norm);
            if (ext.Length == 0) continue;
            bool isExplicit = ExplicitProprietaryArchiveExtensions.Contains(ext) && entry.Size >= 64 * 1024;
            bool isLargeBinary = GenericBinaryArchiveExtensions.Contains(ext) && entry.Size >= 1024 * 1024;
            if (!isExplicit && !isLargeBinary) continue;
            candidates.Add((Path.GetFileName(norm), entry.Size));
        }
        return candidates
            .OrderByDescending(c => c.Size)
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
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
                    string saved = settings.GetPackageSpelling(canonical);
                    selection = saved.Length == 0 ? canonical : saved;
                }
            }
            if (previous.Length > 0 && previous != LooseFiles)
                settings.SetPackageSpelling(previous, previous);
            if (settings.SelectedPackageRaw != selection) settings.SelectedPackageRaw = selection;
            if (previous != selection)
            {
                settings.ScannedSource = "";
                settings.TouchScanVersion();
                store.Execute("UPDATE files SET active=0 WHERE active=1; UPDATE units SET active=0,revision=revision+1 WHERE active IN (1,2)");
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
    }

    public string SelectedPackage => PackageChoice(settings.Source);

    private string PackageChoice(string root)
    {
        string choice = settings.SelectedPackageRaw;
        if (choice.Length > 0) return choice;
        if (root.Length > 0 && settings.ScannedSource == root && settings.ReadScanErrors().Count == 0)
        {
            string previous = settings.ScannedPackage;
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
        return Formats.Inspect(entry.Read(cancellation), format, settings.GetExtractionRule(resource));
    }

    public void ConfigureResource(string resource, ExtractionRule? rule)
    {
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            string value = rule == null ? "" : System.Text.Json.JsonSerializer.Serialize(rule);
            if (settings.GetExtractionRuleRaw(resource) != value)
            {
                settings.SetExtractionRuleRaw(resource, value);
                settings.ScannedSource = "";
                settings.TouchScanVersion();
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
    }
}
