using System.Text.Json;

namespace Locara.Core;

public sealed partial class ProjectService
{
    public BuildResult Build(string destination, bool final = false, string? password = null, CancellationToken cancellation = default,
        PackagePreview? reviewed = null, bool? overlayOnly = null)
    {
        store.Execute("BEGIN");
        try { return BuildSnapshot(destination, final, password, cancellation, reviewed, overlayOnly); }
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
        if (settings.ScannedSource != source.Root || settings.ScannedPackage != source.Package)
            throw new InvalidOperationException("افحص المصدر المحدد قبل البناء.");
        if (HasBlockingScanErrors())
            throw new InvalidOperationException("راجع تشخيصات الفحص قبل البناء.");
        var skippedScanErrorPaths = settings.ReadScanErrors()
            .Select(e => e.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.Ordinal);
        var profile = Engines.Resolve(settings.Engine, index.Entries.Select(entry => entry.Path).ToList(), source.Path);
        if (profile.Name != settings.ScannedEngine)
            throw new InvalidOperationException("تغير ملف تعريف المحرك. أعد الفحص.");
        if (settings.ExtractionRevision != ExtractionRevision
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
        var displayFont = settings.ResolveDisplayFont();
        var unityDisplay = settings.ResolveUnityDisplayOptions();
        byte[]? unrealAesKey = source.UnrealAesKey;
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
        string gettextPoTarget = settings.GettextPoTarget;
        var gettextPotDeliveries = new List<(string PotPath, Dictionary<string, string> EffectiveReplacements, byte[] RewrittenPotBytes)>();
        var companionUexpDeliveries = new List<(string UexpRelPath, string BeforeHash, byte[] NewBytes)>();
        int seenScanned = 0;
        var matchedTranslatedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in index.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var format = source.FormatFor(entry.Path);
            if (format == null) continue;
            string key = source.Key(entry.Path);
            if (!scanned.ContainsKey(key) && skippedScanErrorPaths.Contains(key))
                continue;
            if (scanned.ContainsKey(key)) seenScanned++;
            var selected = units[key];
            bool hasSelectedUnits = selected.Any();
            if (hasSelectedUnits) matchedTranslatedPaths.Add(key);
            bool displaySupported = false;
            if (format is StarTruckerCatalog && selected.Any(unit => ArabicTextEngine.HasArabic(unit.Target)))
            {
                string? unsupported = source.Package.Length == 0
                    ? "اختر مجلد اللعبة لبناء موارد الخط." : StarTruckerDisplay.UnsupportedReason(source.Root, source.Path, cancellation);
                displaySupported = unsupported == null;
                notice = displaySupported
                    ? "تجهيز العرض العربي يغطي جميع نصوص اللعبة المعتمدة (القوائم والحوارات والواجهات)."
                    : "تصدير ملف اللغة فقط. " + unsupported;
            }
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            var sourceByLocator = new Dictionary<string, string>(StringComparer.Ordinal);
            bool shapedLabels = false;
            foreach (var unit in selected)
            {
                if (ArabicTextEngine.ValidateTokens(unit.Source, unit.Target, profile.Name).Length != 0)
                {
                    blocked++;
                    continue;
                }
                sourceByLocator[unit.Locator] = unit.Source;
                if (!displaySupported || !ArabicTextEngine.HasArabic(unit.Target))
                    replacements.Add(unit.Locator, unit.Target);
                else if (StarTruckerDisplay.Supports(unit))
                {
                    replacements.Add(unit.Locator, ArabicTextEngine.Shape(unit.Target));
                    shapedLabels = true;
                }
                else blocked++;
            }
            if (replacements.Count > 0) resources.Add(new(key, replacements.Count));

            if (!source.IsArchive && replacements.Count == 0 && !hasSelectedUnits)
                continue;

            if (format is UnityI2Format && !source.IsArchive)
            {
                string beforeI2 = entry.Hash(cancellation);
                if (scanned.GetValueOrDefault(key) != beforeI2)
                    throw new InvalidOperationException("تغير المورد منذ الفحص — " + key);
                expected[entry.Path] = beforeI2;
                if (replacements.Count == 0) continue;

                bool hasArabicI2 = replacements.Values.Any(ArabicTextEngine.HasArabic);
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
                    if (hasArabicI2 && standaloneUnityTmpHost != null
                        && entry.Path.Equals(standaloneUnityTmpHost.Path, StringComparison.OrdinalIgnoreCase))
                        unityTmpPatchedInResource = true;
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

                bool hasArabicAsset = replacements.Values.Any(ArabicTextEngine.HasArabic);
                if (hasArabicAsset) anyNonI2UnityArabic = true;
                if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
                {
                    string stagedPath = reserveStreamedRewrite(entry);
                    UnityAssetsFormat.ApplyToFile(
                        Path.Combine(source.Path, entry.Path), stagedPath, replacements, displayFont, unityDisplay,
                        applyShaping: unityDisplay.ShapeArabic, cancellation, out bool patchedAssetTmp);
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

            if (format is GameMakerDataWinFormat && !source.IsArchive)
            {
                string beforeGm = entry.Hash(cancellation);
                if (scanned.GetValueOrDefault(key) != beforeGm)
                    throw new InvalidOperationException("تغير المورد منذ الفحص — " + key);
                expected[entry.Path] = beforeGm;
                if (replacements.Count == 0) continue;

                if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
                {
                    string stagedPath = reserveStreamedRewrite(entry);
                    GameMakerDataWinFormat.ApplyToFile(
                        Path.Combine(source.Path, entry.Path), stagedPath, replacements,
                        shapeArabic: settings.GameMakerShapeArabic, cancellation: cancellation);
                    string afterGm = PackageInstaller.FileHash(stagedPath, cancellation);
                    if (beforeGm == afterGm) continue;
                    expected[entry.Path] = afterGm;
                    changes.Add(new(entry.Path, beforeGm, afterGm));
                    commitStreamedRewrite(entry, stagedPath);
                }
                else
                {
                    changes.Add(new(entry.Path, beforeGm, beforeGm + ":pending"));
                }
                continue;
            }

            if (format is UnrealStringTableFormat && !source.IsArchive)
            {
                string beforeUa = entry.Hash(cancellation);
                if (scanned.GetValueOrDefault(key) != beforeUa)
                    throw new InvalidOperationException("تغير المورد منذ الفحص — " + key);
                expected[entry.Path] = beforeUa;
                if (replacements.Count == 0) continue;

                string fullUa = Directory.Exists(source.Path) ? Path.Combine(source.Path, entry.Path) : source.Path;
                string fullUexp = Path.ChangeExtension(fullUa, ".uexp");
                byte[] origUa = File.ReadAllBytes(fullUa);
                byte[]? origUexp = File.Exists(fullUexp) ? File.ReadAllBytes(fullUexp) : null;
                var (newUa, newUexp, stChanged) = UnrealStringTableFormat.ApplyPair(origUa, origUexp, replacements);
                if (!stChanged) continue;

                string afterUa = Identity.Hash(newUa);
                expected[entry.Path] = afterUa;
                changes.Add(new(entry.Path, beforeUa, afterUa));
                stageChange?.Invoke(entry, newUa);

                if (newUexp != null && origUexp != null && Directory.Exists(source.Path))
                {
                    string uexpRel = Identity.SafePath(Path.ChangeExtension(entry.Path, ".uexp"));
                    string beforeUexp = Identity.Hash(origUexp);
                    companionUexpDeliveries.Add((uexpRel, beforeUexp, newUexp));
                }
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
                    if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
                    {
                        var (_, _, _, patched, _, _) = UnrealPakFormat.BuildPatchedLocresEntries(fullPak, replacements, displayFont, cancellation, unrealAesKey);
                        if (patched.Count == 0) continue;
                        string stagedPath = reserveStreamedRewrite(entry);
                        UnrealPakCodec.PatchExistingPak(fullPak, stagedPath, patched, cancellation, unrealAesKey);
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
                && !hasI2InIndex && unityDisplay.ShapeArabic
                && replacements.Values.Any(ArabicTextEngine.HasArabic);
            if (profile.Name == "unity" && !source.IsArchive && replacements.Values.Any(ArabicTextEngine.HasArabic))
                anyNonI2UnityArabic = true;

            byte[] rewritten;
            Dictionary<string, string> effectiveReplacements = replacements;
            if (format is UnityBundleFormat bundleFmt && shapeBundleOrLooseUnity)
            {
                rewritten = bundleFmt.ApplyWithDisplay(original, replacements, unityDisplay);
            }
            else if (shapeBundleOrLooseUnity && format is not StarTruckerCatalog)
            {
                bool conservativeSingleLineWrap = !hasUnityFontTarget
                    && unityDisplay.WrapMode.Equals("auto", StringComparison.OrdinalIgnoreCase);
                HashSet<string>? locTxtQuoted = format is LocalizationTextFormat
                    ? LocalizationTextFormat.GetQuotedLocators(original)
                    : null;
                var shapedMap = new Dictionary<string, string>(replacements.Count, StringComparer.Ordinal);
                foreach (var (loc, val) in replacements)
                {
                    bool singleLineFormatOnly = format is IniFormat
                        || (locTxtQuoted != null && !locTxtQuoted.Contains(loc));
                    if (!ArabicTextEngine.HasArabic(val))
                    {
                        shapedMap[loc] = val;
                    }
                    else if (singleLineFormatOnly)
                    {
                        shapedMap[loc] = ArabicTextEngine.Shape(val);
                    }
                    else if (conservativeSingleLineWrap)
                    {
                        string srcText = sourceByLocator.GetValueOrDefault(loc, "");
                        int visibleLen = Tokens.ReplaceTokens(val, _ => "").Length;
                        bool multiLineOrLong = srcText.Contains('\n') || val.Contains('\n') || visibleLen > 65;
                        shapedMap[loc] = multiLineOrLong
                            ? ArabicTextEngine.WrapAndShape(val, 48)
                            : ArabicTextEngine.Shape(val);
                    }
                    else
                    {
                        shapedMap[loc] = unityDisplay.TransformArabic(val);
                    }
                }
                effectiveReplacements = shapedMap;
                rewritten = Formats.Apply(original, format, shapedMap, settings.GetExtractionRule(key));
            }
            else
            {
                rewritten = Formats.Apply(original, format, replacements, settings.GetExtractionRule(key));
            }
            bool redirectingPotToSiblingPo = format is PoFormat && !source.IsArchive && LocaleClassifier.IsGettextTemplate(key)
                && gettextPoTarget.Length > 0
                && !gettextPoTarget.Equals("pot-only", StringComparison.OrdinalIgnoreCase)
                && !gettextPoTarget.Equals("export-ar", StringComparison.OrdinalIgnoreCase);
            if (format is PoFormat && !source.IsArchive && LocaleClassifier.IsGettextTemplate(key)
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
        bool archiveCountMismatch = source.IsArchive && expected.Count != scanned.Count;
        bool missingTranslatedResource = units.Any(g => !matchedTranslatedPaths.Contains(g.Key));
        if (scanned.Count == 0 || seenScanned == 0 || archiveCountMismatch || missingTranslatedResource)
            throw new InvalidOperationException("تغيرت قائمة الموارد أو لم يُعثر على موارد مدعومة. أعد الفحص.");

        foreach (var (uexpRel, beforeUexp, newUexp) in companionUexpDeliveries)
        {
            string afterUexp = Identity.Hash(newUexp);
            expected[uexpRel] = afterUexp;
            changes.Add(new(uexpRel, beforeUexp, afterUexp));
            stageChange?.Invoke(ResourceEntry.FromBytes(uexpRel, newUexp), newUexp);
        }

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
                        || source.Key(e.Path).Equals(gettextPoTarget, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("تغيرت قائمة الموارد أو لم يُعثر على موارد مدعومة. أعد الفحص.");
                    if (!changes.Any(c => c.Path.Equals(siblingEntry.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        byte[] siblingOrig = siblingEntry.Read(cancellation);
                        string siblingBefore = Identity.Hash(siblingOrig);
                        string siblingKey = source.Key(siblingEntry.Path);
                        if (scanned.TryGetValue(siblingKey, out string? scannedSibling) && scannedSibling != siblingBefore)
                            throw new InvalidOperationException("تغير المورد منذ الفحص — " + siblingKey);
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
            bool anySigBesideSource = false;
            foreach (var (patchRelPath, group) in unrealPatchGroups)
            {
                cancellation.ThrowIfCancellationRequested();
                patchFileNames.Add(Path.GetFileName(patchRelPath));
                if (group.Any(g => File.Exists(Path.ChangeExtension(g.SourcePakPath, ".sig"))))
                    anySigBesideSource = true;
                if (reserveStreamedRewrite != null && commitStreamedRewrite != null)
                {
                    string mountPoint = "../../../";
                    int targetVersion = 11;
                    ulong pathHashSeed = 0x9B56FB58ul;
                    var allPatchedByPath = new Dictionary<string, UnrealPakPatchFile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (sourcePakPath, pakReplacements) in group)
                    {
                        var (mp, ver, seed, patched, _, _) = UnrealPakFormat.BuildPatchedLocresEntries(sourcePakPath, pakReplacements, displayFont, cancellation, unrealAesKey);
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
                string sigHint = anySigBesideSource
                    ? " (ملاحظة: إذا كانت اللعبة تتحقق من توقيع .sig فقد تحتاج نسخ ملف .sig المجاور باسم حزمة التعديل)."
                    : "";
                string ueNote = $"تم توليد حزمة تعديل Unreal جاهزة ({names}) مع حقن الخط العربي الثنائي ({unrealFont.DisplayName}) داخل Content/Paks/ دون تعديل ملفات اللعبة الأصلية.{sigHint}";
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
            string i2Note = (unityTmpPatchedInResource || unityTmpStandalonePatched)
                ? $"تم تحديث I2 Localization وتجهيز العرض ({shapingDesc}) وحقن الخط الاحتياطي (TextMeshPro) · الخط: {displayFont.DisplayName} · الحجم: {unityDisplay.ScaleLabel}"
                : $"تم تحديث I2 Localization وتجهيز العرض ({shapingDesc}).";
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
        else if (anyNonI2UnityArabic)
        {
            string shapingDesc = unityDisplay.ShapeArabic ? $"تشكيل العربية ({unityDisplay.WrapLabel})" : "عربي منطقي (RTL أصلي)";
            string dynNote = $"تم تجهيز العرض العربي ({shapingDesc}) لخطوط Unity.";
            notice = notice.Length == 0 ? dynNote : notice + "\n" + dynNote;
        }

        bool unityClassicRtlPluginBundled = false;
        bool wantClassicRtlPlugin = settings.IncludeClassicUnityRtlPlugin;
        if (!source.IsArchive && profile.Name == "unity" && wantClassicRtlPlugin && changes.Count > 0
            && Directory.Exists(source.Path)
            && UnityClassicRtlPlugin.DetectBackend(source.Path) != UnityScriptingBackend.Il2Cpp)
        {
            byte[] pluginDll = UnityClassicRtlPlugin.LoadPluginBytes();
            byte[] pluginCfg = UnityClassicRtlPlugin.BuildDefaultConfigBytes(
                fixPreShaped: true,
                fixLogical: true);

            foreach (var (relPath, payload) in new[]
            {
                (UnityClassicRtlPlugin.PluginRelativePath, pluginDll),
                (UnityClassicRtlPlugin.ConfigRelativePath, pluginCfg)
            })
            {
                if (changes.Any(c => c.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                string diskPath = Path.Combine(source.Path, relPath.Replace('/', Path.DirectorySeparatorChar));
                string origHash = File.Exists(diskPath) ? PackageInstaller.FileHash(diskPath, cancellation) : "";
                string afterHash = Identity.Hash(payload);
                if (origHash == afterHash) continue;
                expected[relPath] = afterHash;
                changes.Add(new(relPath, origHash, afterHash));
                stageChange?.Invoke(ResourceEntry.FromBytes(relPath, payload), payload);
                unityClassicRtlPluginBundled = true;
            }

            if (unityClassicRtlPluginBundled)
            {
                string rtlNote = "تم إرفاق ملحق إصلاح التفاف الأسطر (Locara.UnityRtlFix) تلقائياً داخل مجلد BepInEx.";
                notice = notice.Length == 0 ? rtlNote : notice + "\n" + rtlNote;
            }
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
                scan = settings.ScanVersion,
                final,
                blocked,
                arabicDisplay,
                i2ArabicUpdated,
                unityAssetsArabicUpdated,
                unityTmpStandalonePatched,
                unityLooseFontsPatched,
                unityClassicRtlPluginBundled,
                wantClassicRtlPlugin,
                gettextPoTarget,
                gettextPoDelivered,
                displayFont = displayFont.Id,
                unityWrap = unityDisplay.WrapMode,
                unityFontScale = unityDisplay.FontScale,
                unityShapeArabic = unityDisplay.ShapeArabic,
                unityContrastBoost = unityDisplay.ContrastBoost,
                unrealAesKeyHash = source.UnrealAesKey is { Length: > 0 } k ? Identity.Hash(k) : "",
                notice,
                inputs = scanned.OrderBy(entry => entry.Key, StringComparer.Ordinal),
                changedFiles,
                revisions = units.SelectMany(group => group.Select(unit => new { unit.Id, unit.Revision }))
            }))
        };
        return new(plan, profile, expected, changes, arabicDisplay);
    }

    private BuildResult BuildSnapshot(string destination, bool final, string? password, CancellationToken cancellation,
        PackagePreview? reviewed, bool? overlayOnly = null)
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
            bool useOverlay = !source.IsArchive && Directory.Exists(source.Root)
                && (overlayOnly ?? (plan.Profile.Name is "unity" or "unreal" or "gamemaker"
                    || (rewrites.Count > 0 && index.Entries.Count > 250)));
            var output = useOverlay
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
            var displayFont = settings.ResolveDisplayFont();
            var unityDisplay = settings.ResolveUnityDisplayOptions();
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
                project = settings.Name,
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
                    unityContrastBoost = unityDisplay.ContrastBoost,
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

    public XUnityExportResult ExportXUnityAutoTranslator(string destination, bool final = false)
    {
        var allUnits = store.Query("""
            SELECT id,path,locator,source,target,context,status,warning,revision,imported FROM units
            WHERE active=1 AND target<>'' AND warning=''
            ORDER BY path,locator
            """, ProjectStore.ReadUnit);
        var displayFont = settings.ResolveDisplayFont();
        var unityDisplay = settings.ResolveUnityDisplayOptions();
        string engineName = settings.ScannedEngine;
        if (string.IsNullOrWhiteSpace(engineName)) engineName = "unity";
        return XUnityAutoTranslatorExporter.Export(destination, allUnits, displayFont, unityDisplay, final, engineName);
    }
}
