using System.Text.Json;

namespace Locara.Core;

public sealed partial class ProjectService
{
    public ScanResult Scan(string? password = null, CancellationToken cancellation = default)
    {
        string source = settings.Source;
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Choose a source before scanning.");
        EnsureCatalogSelected();
        string engine = settings.Engine;
        string scope = settings.SourceScope;
        string scanVersion = settings.ScanVersion;
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
        bool sameSource = settings.ScannedSource == source && settings.ScannedEngine == profile.Name
            && settings.ScannedPackage == input.Package && settings.ExtractionRevision == ExtractionRevision;
        var priorAdvisories = (sameSource ? settings.ReadScanAdvisories() : [])
            .ToLookup(item => item.Path, StringComparer.Ordinal);
        var fileSelection = settings.LoadFileSelection(input.Package);
        var userEditedPaths = store.Query(
            "SELECT DISTINCT path FROM units WHERE target<>'' OR status<>'pending'",
            r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
        bool earlyHasPrimaryPathLocale = resources.Entries.Any(e =>
            !Engines.IsEngineInternalMetadata(e.Path)
            && Formats.For(e.Path) is { } f
            && f is not UnityBundleFormat
            && LocaleClassifier.IsPrimaryLocaleSource(input.Key(e.Path)));

        var lazySkippedSecondary = new List<(string Path, string Hash, string FormatName, string Locale)>();
        var lazySkippedAuxiliary = new List<(string Path, string Hash, string FormatName)>();
        var changes = new List<(string Path, string Hash, IFormatHandler Format, IReadOnlyList<TextSlot> Slots,
            Dictionary<string, string>? LegacyLocators, string? BundleLocale)>();

        var primaryCandidates = new List<(ResourceEntry Resource, IFormatHandler Format, string Path)>();
        var auxiliaryCandidates = new List<(ResourceEntry Resource, IFormatHandler Format, string Path)>();
        foreach (var resource in resources.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var format = input.FormatFor(resource.Path);
            if (format == null) continue;
            string path = input.Key(resource.Path);
            bool isPrimaryTrack = LocaleClassifier.IsDedicatedLocalizationResource(path, format.Name)
                || LocaleClassifier.IsExplicitLocalizationDirectory(path)
                || (fileSelection.TryGetValue(path, out bool explicitInc) && explicitInc)
                || userEditedPaths.Contains(path);
            if (isPrimaryTrack) primaryCandidates.Add((resource, format, path));
            else auxiliaryCandidates.Add((resource, format, path));
        }

        void ProcessCandidate(ResourceEntry resource, IFormatHandler format, string path, bool allowLazyAuxiliary)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (earlyHasPrimaryPathLocale
                    && format is not UnityBundleFormat and not StarTruckerCatalog
                    && LocaleClassifier.ExtractPathLocale(path) is { } earlyLocale
                    && !LocaleClassifier.IsEnglishLocaleCode(earlyLocale)
                    && !(fileSelection.TryGetValue(path, out bool explicitInc) && explicitInc)
                    && !userEditedPaths.Contains(path))
                {
                    string lazyHash = resource.Hash(cancellation);
                    lazySkippedSecondary.Add((path, lazyHash, format.Name, earlyLocale));
                    seen.Add(path);
                    return;
                }

                if (allowLazyAuxiliary)
                {
                    string lazyHash = resource.Hash(cancellation);
                    lazySkippedAuxiliary.Add((path, lazyHash, format.Name));
                    seen.Add(path);
                    return;
                }

                LocalizationProviderRegistry.TryGetByName(format.Name, out var provider);
                bool streamedAccess = provider?.Metadata.UsesStreamedFileAccess == true && !input.IsArchive;
                string hash;
                byte[]? bytes = null;
                if (streamedAccess)
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
                    IReadOnlyList<TextSlot> slots = streamedAccess && provider != null
                        ? provider.ExtractFromDisk(diskPath, input.UnrealAesKey, d => advisories.Add(d), cancellation)
                        : Formats.Extract(profile, resource.Path, bytes!, format, settings.GetExtractionRule(path), rpgDataAtRoot,
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
                or InvalidDataException or InvalidOperationException or System.Text.DecoderFallbackException
                or System.Xml.XmlException or YamlDotNet.Core.YamlException or ArgumentException
                or ICSharpCode.SharpZipLib.Zip.ZipException or NotSupportedException)
            {
                diagnostics.Add(new(path, error.Message));
            }
        }

        // Pass 1: Extract dedicated & explicit-localization files first
        foreach (var item in primaryCandidates)
            ProcessCandidate(item.Resource, item.Format, item.Path, allowLazyAuxiliary: false);

        bool earlyHasDedicatedLoc = false;
        if (auxiliaryCandidates.Count > 0)
        {
            int pass1DedicatedHuman = 0;
            bool pass1HasEnglishBundle = changes.Any(c => LocaleClassifier.IsEnglishLocaleCode(c.BundleLocale))
                || seen.Any(p => p.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                    && (p.Contains("(en)", StringComparison.OrdinalIgnoreCase)
                        || p.Contains("english", StringComparison.OrdinalIgnoreCase)));
            var pass1ChangedPaths = new HashSet<string>(changes.Select(c => c.Path), StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (!LocaleClassifier.IsDedicatedLocalizationResource(change.Path, change.Format.Name)) continue;
                bool isSec = (change.Format is UnityBundleFormat && pass1HasEnglishBundle && change.BundleLocale != null && !LocaleClassifier.IsEnglishLocaleCode(change.BundleLocale))
                    || (change.Format is not UnityBundleFormat && earlyHasPrimaryPathLocale && LocaleClassifier.ExtractPathLocale(change.Path) is { } loc && !LocaleClassifier.IsEnglishLocaleCode(loc));
                if (isSec) continue;
                var q = TextQualityClassifier.ClassifyFile(change.Path, change.Slots.Select(s => (s.Locator, s.Source)).ToArray(), change.Format.Name);
                if (q.Verdict == FileQualityVerdict.Translatable || change.Format is UnityI2Format or StarTruckerCatalog)
                    pass1DedicatedHuman += q.HumanCount;
                if (pass1DedicatedHuman >= DedicatedLocalizationMinHumanUnits) break;
            }
            if (pass1DedicatedHuman < DedicatedLocalizationMinHumanUnits)
            {
                foreach (var (prevPath, prevInfo) in previous)
                {
                    if (!seen.Contains(prevPath) || pass1ChangedPaths.Contains(prevPath)) continue;
                    if (!LocaleClassifier.IsDedicatedLocalizationResource(prevPath, prevInfo.Format)) continue;
                    bool isSec = prevInfo.Format != "unity-bundle"
                        && earlyHasPrimaryPathLocale
                        && LocaleClassifier.ExtractPathLocale(prevPath) is { } loc
                        && !LocaleClassifier.IsEnglishLocaleCode(loc);
                    if (isSec) continue;
                    pass1DedicatedHuman += store.Query(
                        "SELECT COUNT(*) FROM units WHERE path=$p AND active IN (1, 2)",
                        r => r.GetInt32(0), ("$p", prevPath))[0];
                    if (pass1DedicatedHuman >= DedicatedLocalizationMinHumanUnits) break;
                }
            }
            earlyHasDedicatedLoc = pass1DedicatedHuman >= DedicatedLocalizationMinHumanUnits;
        }

        // Pass 2: Process remaining non-dedicated sidecar/script files
        foreach (var item in auxiliaryCandidates)
            ProcessCandidate(item.Resource, item.Format, item.Path, allowLazyAuxiliary: earlyHasDedicatedLoc);

        if (input.FormatDiagnostics.Count > 0 && !seen.Any(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.AddRange(input.FormatDiagnostics);
        }
        if (seen.Count == 0 && diagnostics.Count == 0)
        {
            string message = profile.Name switch
            {
                "unity" => "هذه لعبة Unity مجمّعة ولا تحتوي على ملفات نصوص مفتوحة؛ نصوصها محزومة داخل حزم ثنائية (.bundle / .assets مثل localization-string-tables-*.bundle) وتحتاج أداة فك حزم Unity أولاً. (تجاهل Locara ملفات محرك Unity الداخلية مثل RuntimeInitializeOnLoads.json تلقائياً لحماية اللعبة وتوفير رصيدك).",
                "unreal" => "هذه لعبة Unreal Engine مجمّعة ولم يُعثر فيها على ملفات .locres أو جداول StringTable غير مشفّرة داخل حزم .pak (إذا كانت الحزم مشفّرة بمفتاح AES يمكنك إدخال المفتاح في الخيارات المتقدمة ثم إعادة الفحص).",
                "gamemaker" => "هذه لعبة GameMaker مجمّعة وتخزّن نصوصها داخل حزمة data.win (مقطع STRG) دون ملفات ترجمة خارجية مفتوحة (JSON/CSV/INI/PO/TOML/Lua).",
                _ => "لم يُعثر على موارد نصية مدعومة في المصدر المختار."
            };
            diagnostics.Add(new(input.Path, message));
        }

        cancellation.ThrowIfCancellationRequested();
        fileSelection = settings.LoadFileSelection(input.Package);
        var fileSelectionModes = settings.LoadFileSelectionModes(input.Package);
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            if (settings.Source != source || settings.Engine != engine || settings.SourceScope != scope || settings.ScanVersion != scanVersion)
                throw new InvalidOperationException("Project settings changed. Scan again.");

            foreach (string path in previous.Keys.Where(path => !seen.Contains(path)))
            {
                cancellation.ThrowIfCancellationRequested();
                store.Execute("UPDATE files SET active=0 WHERE path=$p; UPDATE units SET active=0 WHERE path=$p", ("$p", path));
            }

            bool hasEnglishBundle = changes.Any(c => LocaleClassifier.IsEnglishLocaleCode(c.BundleLocale))
                || seen.Any(p => p.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                    && (p.Contains("(en)", StringComparison.OrdinalIgnoreCase)
                        || p.Contains("english", StringComparison.OrdinalIgnoreCase)));
            bool hasPrimaryPathLocale = seen.Any(p => LocaleClassifier.IsPrimaryLocaleSource(p));

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
                if (!LocaleClassifier.IsDedicatedLocalizationResource(change.Path, change.Format.Name)) continue;
                bool isSecondary = (change.Format is UnityBundleFormat && hasEnglishBundle && change.BundleLocale != null && !LocaleClassifier.IsEnglishLocaleCode(change.BundleLocale))
                    || (change.Format is not UnityBundleFormat && hasPrimaryPathLocale && LocaleClassifier.ExtractPathLocale(change.Path) is { } loc && !LocaleClassifier.IsEnglishLocaleCode(loc));
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
                    if (!LocaleClassifier.IsDedicatedLocalizationResource(prevPath, prevInfo.Format)) continue;
                    bool isSecondary = prevInfo.Format != "unity-bundle"
                        && hasPrimaryPathLocale
                        && LocaleClassifier.ExtractPathLocale(prevPath) is { } loc
                        && !LocaleClassifier.IsEnglishLocaleCode(loc);
                    if (isSecondary) continue;
                    primaryDedicatedHumanUnits += store.Query(
                        "SELECT COUNT(*) FROM units WHERE path=$p AND active IN (1, 2)",
                        r => r.GetInt32(0), ("$p", prevPath))[0];
                    if (primaryDedicatedHumanUnits >= DedicatedLocalizationMinHumanUnits) break;
                }
            }
            bool hasPrimaryDedicatedLoc = primaryDedicatedHumanUnits >= DedicatedLocalizationMinHumanUnits;

            foreach (var lazy in lazySkippedSecondary)
            {
                cancellation.ThrowIfCancellationRequested();
                store.Execute("UPDATE units SET active=0 WHERE path=$p", ("$p", lazy.Path));
                store.Execute("""
                    INSERT INTO files(path,hash,format,snapshot) VALUES($p,$h,$f,X'')
                    ON CONFLICT(path) DO UPDATE SET hash=$h,format=$f,snapshot=X'',active=1
                    """, ("$p", lazy.Path), ("$h", lazy.Hash), ("$f", lazy.FormatName));
                advisories.Add(new(lazy.Path,
                    $"ملف لغة أخرى ({lazy.Locale}) — استُبعد تلقائياً للاكتفاء باللغة الإنجليزية وتوفير رصيدك؛ يمكنك تفعيله من تبويب «الملفات»."));
            }

            foreach (var lazyAux in lazySkippedAuxiliary)
            {
                cancellation.ThrowIfCancellationRequested();
                store.Execute("UPDATE units SET active=0 WHERE path=$p", ("$p", lazyAux.Path));
                store.Execute("""
                    INSERT INTO files(path,hash,format,snapshot) VALUES($p,$h,$f,X'')
                    ON CONFLICT(path) DO UPDATE SET hash=$h,format=$f,snapshot=X'',active=1
                    """, ("$p", lazyAux.Path), ("$h", lazyAux.Hash), ("$f", lazyAux.FormatName));
            }
            if (lazySkippedAuxiliary.Count > 0)
            {
                advisories.Add(new(input.Path,
                    $"تم تخطي تحليل {lazySkippedAuxiliary.Count:N0} ملف بيانات/برمجة جانبي لتسريع الفحص وحماية رصيدك لوجود ملفات ترجمة رسمية؛ يمكنك تفعيل أي ملف منها من تبويب «الملفات»."));
            }

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
                string? pathLocale = LocaleClassifier.ExtractPathLocale(change.Path);
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
                    && change.BundleLocale != null && !LocaleClassifier.IsEnglishLocaleCode(change.BundleLocale))
                {
                    included = false;
                    advisories.Add(new(change.Path,
                        $"حزمة لغة أخرى ({change.BundleLocale}) — استُبعدت تلقائياً للاكتفاء باللغة الإنجليزية وتوفير رصيدك؛ يمكنك تفعيلها من تبويب «الملفات»."));
                }
                else if (change.Format is not UnityBundleFormat && hasPrimaryPathLocale
                    && pathLocale != null && !LocaleClassifier.IsEnglishLocaleCode(pathLocale))
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
                    && !LocaleClassifier.IsDedicatedLocalizationResource(change.Path, change.Format.Name)
                    && !(LocaleClassifier.IsExplicitLocalizationDirectory(change.Path) && quality.Verdict == FileQualityVerdict.Translatable))
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

            if (Directory.Exists(source) && diagnostics.Count == 0 && settings.SelectedPackageRaw.Length == 0)
                settings.SelectedPackageRaw = input.Package.Length == 0 ? LooseFiles : input.Package;
            settings.TouchScanVersion();
            settings.ScannedSource = source;
            settings.ScannedEngine = profile.Name;
            settings.ScannedPackage = input.Package;
            settings.WriteScanErrors(diagnostics);
            settings.WriteScanAdvisories(advisories);
            settings.ExtractionRevision = ExtractionRevision;
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

    public (bool HasActivePot, List<string> SiblingPoPaths) GetGettextPoStatus()
    {
        var poFiles = store.Query(
            "SELECT path FROM files WHERE active=1 AND format='po' ORDER BY path",
            r => r.GetString(0));
        if (poFiles.Count == 0) return (false, []);

        bool hasActivePot = false;
        foreach (string filePath in poFiles)
        {
            if (!LocaleClassifier.IsGettextTemplate(filePath)) continue;
            bool hasActiveUnits = store.Query(
                "SELECT EXISTS(SELECT 1 FROM units WHERE path=$p AND active=1)",
                r => r.GetBoolean(0),
                ("$p", filePath)).Single();
            if (hasActiveUnits) { hasActivePot = true; break; }
        }
        if (!hasActivePot) return (false, []);
        var siblings = poFiles
            .Where(p => !LocaleClassifier.IsGettextTemplate(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (true, siblings);
    }

    public IReadOnlyList<FileQualitySummary> ScannedFiles()
    {
        var files = store.Query("SELECT path, format FROM files WHERE active=1 ORDER BY path",
            r => (Path: r.GetString(0), Format: r.GetString(1)));
        if (files.Count == 0) return [];

        string package = settings.ScannedPackage;
        var explicitSelection = settings.LoadFileSelection(package);
        var selectionModes = settings.LoadFileSelectionModes(package);

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
            && LocaleClassifier.IsEnglishLocaleCode(ExtractBundleLocale(f.Path, unitsByFile.GetValueOrDefault(f.Path) ?? [])));
        bool hasPrimaryPathLocale = files.Any(f => f.Format != "unity-bundle" && LocaleClassifier.IsPrimaryLocaleSource(f.Path));

        var classifications = new Dictionary<string, FileClassification>(files.Count, StringComparer.Ordinal);
        int primaryDedicatedHumanUnits = 0;
        foreach (var (filePath, format) in files)
        {
            var rows = unitsByFile.GetValueOrDefault(filePath) ?? [];
            var entries = rows.Select(r => (r.Locator, r.Source)).ToArray();
            var c = TextQualityClassifier.ClassifyFile(filePath, entries, format);
            classifications[filePath] = c;

            if (!LocaleClassifier.IsDedicatedLocalizationResource(filePath, format)) continue;
            bool isSecondary = (format == "unity-bundle" && hasEnglishBundle
                    && ExtractBundleLocale(filePath, rows) is { } bLoc && !LocaleClassifier.IsEnglishLocaleCode(bLoc))
                || (format != "unity-bundle" && hasPrimaryPathLocale
                    && LocaleClassifier.ExtractPathLocale(filePath) is { } pLoc && !LocaleClassifier.IsEnglishLocaleCode(pLoc));
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
                && ExtractBundleLocale(filePath, rows) is { } bundleLoc && !LocaleClassifier.IsEnglishLocaleCode(bundleLoc))
            {
                verdict = FileQualityVerdict.SecondaryLanguage;
                reason = $"حزمة لغة إضافية ({bundleLoc}) — مستبعدة افتراضياً للاكتفاء بالإنجليزية وتوفير رصيدك.";
            }
            else if (format != "unity-bundle" && hasPrimaryPathLocale
                && LocaleClassifier.ExtractPathLocale(filePath) is { } pathLocale && !LocaleClassifier.IsEnglishLocaleCode(pathLocale))
            {
                verdict = FileQualityVerdict.SecondaryLanguage;
                reason = $"ملف لغة إضافية ({pathLocale}) — مستبعد افتراضياً للاكتفاء بالإنجليزية وتوفير رصيدك.";
            }
            else if ((verdict != FileQualityVerdict.Technical || total == 0)
                && hasPrimaryDedicatedLoc
                && !LocaleClassifier.IsDedicatedLocalizationResource(filePath, format)
                && !(LocaleClassifier.IsExplicitLocalizationDirectory(filePath) && verdict == FileQualityVerdict.Translatable))
            {
                verdict = FileQualityVerdict.AuxiliaryData;
                reason = total == 0
                    ? "ملف بيانات/إعدادات جانبي — تم تخطي تحليله لتسريع الفحص لوجود ملفات ترجمة رسمية في المشروع؛ فعّل الملف من مربع التضمين لاستخراج نصوصه فوراً."
                    : "ملف بيانات/إعدادات جانبي — توجد ملفات ترجمة رسمية مخصّصة في المشروع، لذا استُبعد تلقائياً لحماية اللعبة وتوفير رصيدك؛ يمكنك تفعيله عند الحاجة.";
            }
            else if (LocalizationProviderRegistry.GetScanReason(filePath, format) is { } providerReason)
            {
                reason = providerReason;
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
            .ThenByDescending(f => LocaleClassifier.IsDedicatedLocalizationResource(f.Path, f.Format))
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetFileIncluded(string resourcePath, bool included, bool humanOnly = false, string? password = null)
    {
        if (!included || !humanOnly)
        {
            SetFilesIncluded(new Dictionary<string, bool>(StringComparer.Ordinal) { [resourcePath] = included }, password);
            return;
        }
        string package = settings.ScannedPackage;
        bool needsOnDemandExtract = false;
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            var saved = settings.LoadFileSelection(package);
            var modes = settings.LoadFileSelectionModes(package);
            saved[resourcePath] = true;
            modes[resourcePath] = "human";

            string format = store.Query("SELECT format FROM files WHERE path=$p AND active=1",
                r => r.GetString(0), ("$p", resourcePath)).FirstOrDefault() ?? "";
            var rows = store.Query(
                "SELECT id, locator, source, target, status, active FROM units WHERE path=$p AND active IN (1, 2)",
                r => (Id: r.GetString(0), Locator: r.GetString(1), Source: r.GetString(2), Target: r.GetString(3), Status: r.GetString(4), Active: r.GetInt32(5)),
                ("$p", resourcePath));
            if (rows.Count == 0 && format.Length > 0)
            {
                store.Execute("UPDATE files SET hash='' WHERE path=$p", ("$p", resourcePath));
                needsOnDemandExtract = true;
            }
            foreach (var row in rows)
            {
                bool keepActive = row.Target.Length > 0
                    || row.Status != "pending"
                    || TextQualityClassifier.ClassifyEntry(row.Locator, row.Source, format) != StringQuality.Technical;
                int desired = keepActive ? 1 : 2;
                if (row.Active != desired)
                    store.Execute("UPDATE units SET active=$a WHERE id=$id", ("$a", desired), ("$id", row.Id));
            }

            settings.SaveFileSelection(package, saved);
            settings.SaveFileSelectionModes(package, modes);
            settings.TouchScanVersion();
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }

        if (needsOnDemandExtract)
            Scan(password);
    }

    public void SetFilesIncluded(IReadOnlyDictionary<string, bool> selections, string? password = null)
    {
        if (selections.Count == 0) return;
        string package = settings.ScannedPackage;
        bool needsOnDemandExtract = false;
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            var saved = settings.LoadFileSelection(package);
            var modes = settings.LoadFileSelectionModes(package);
            bool anyChanged = false;
            foreach (var (path, included) in selections)
            {
                saved[path] = included;
                modes.Remove(path);
                if (included)
                {
                    bool wasLazySkipped = store.Query(
                        "SELECT EXISTS(SELECT 1 FROM files WHERE path=$p AND active=1) AND NOT EXISTS(SELECT 1 FROM units WHERE path=$p AND active IN (1, 2))",
                        r => r.GetBoolean(0), ("$p", path)).Single();
                    if (wasLazySkipped)
                    {
                        store.Execute("UPDATE files SET hash='' WHERE path=$p", ("$p", path));
                        needsOnDemandExtract = true;
                    }
                }
                int fromActive = included ? 2 : 1;
                int toActive = included ? 1 : 2;
                store.Execute("UPDATE units SET active=$to WHERE path=$p AND active=$from",
                    ("$to", toActive), ("$p", path), ("$from", fromActive));
                anyChanged = true;
            }
            if (anyChanged)
            {
                settings.SaveFileSelection(package, saved);
                settings.SaveFileSelectionModes(package, modes);
                settings.TouchScanVersion();
            }
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }

        if (needsOnDemandExtract)
            Scan(password);
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
        string package = settings.ScannedPackage;
        int appliedClusters = 0, includedFiles = 0, excludedFiles = 0, ruleFiles = 0;
        bool needsRescan = false;

        store.Execute("BEGIN IMMEDIATE");
        try
        {
            var saved = settings.LoadFileSelection(package);
            var modes = settings.LoadFileSelectionModes(package);

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
                        var existingRule = settings.GetExtractionRule(path);
                        if (existingRule?.FieldPatterns is { Length: > 0 })
                        {
                            var cleared = existingRule with { FieldPatterns = null };
                            bool empty = cleared.Key == null && cleared.Source == null && cleared.Target == null
                                && cleared.Sheet == null && (cleared.Fields == null || cleared.Fields.Length == 0);
                            settings.SetExtractionRuleRaw(path, empty ? "" : JsonSerializer.Serialize(cleared));
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
                        var existing = settings.GetExtractionRule(path);
                        var updated = (existing ?? new ExtractionRule()) with { FieldPatterns = patterns };
                        string serialized = JsonSerializer.Serialize(updated);
                        if (settings.GetExtractionRuleRaw(path) != serialized)
                        {
                            settings.SetExtractionRuleRaw(path, serialized);
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
                settings.SaveFileSelection(package, saved);
                settings.SaveFileSelectionModes(package, modes);
                settings.TouchScanVersion();
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
            warning = target.Length == 0 ? "" : ArabicTextEngine.ValidateTokens(slot.Source, target, engine);
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
}
