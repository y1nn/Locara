using System.Buffers.Binary;
using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class ExpandedFormatsAndEnginesTests
{
    [Fact]
    public void TomlFormatExtractsAndRewritesPreservingCommentsTypesAndArrayOfTables()
    {
        const string toml = """
            # Game UI & Quest Localization
            version = 2
            enabled = true

            [ui]
            play = "Start New Game"
            tooltip = 'Press "Enter" to continue'
            intro = '''
            Line one
            Line two'''

            [[quests]]
            id = "QUEST_01"
            title = "Find the Lost Relic"
            choices = ["Accept Quest", "Decline"]

            [[quests]]
            id = "QUEST_02"
            title = "Defend the Outpost"
            """;

        var format = Formats.For("localization/en.toml");
        Assert.NotNull(format);
        Assert.Equal("toml", format!.Name);
        Assert.True(Formats.Configurable("localization/en.toml"));

        byte[] raw = Encoding.UTF8.GetBytes(toml);
        var slots = format.Extract(raw);
        Assert.Contains(slots, s => s.Locator == "/ui/play" && s.Source == "Start New Game");
        Assert.Contains(slots, s => s.Locator == "/ui/tooltip" && s.Source == "Press \"Enter\" to continue");
        Assert.Contains(slots, s => s.Locator == "/quests/0/title" && s.Source == "Find the Lost Relic");
        Assert.Contains(slots, s => s.Locator == "/quests/0/choices/0" && s.Source == "Accept Quest");
        Assert.Contains(slots, s => s.Locator == "/quests/1/title" && s.Source == "Defend the Outpost");

        // FieldSemantics normalizes array-of-tables locators for SchemaClusterer & AI Scout
        Assert.Equal("/quests/*/title", FieldSemantics.NormalizeLocatorPattern("/quests/1/title"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyEntry("/quests/0/id", "QUEST_01", "toml"));

        byte[] rewritten = format.Apply(raw, new Dictionary<string, string>
        {
            ["/ui/play"] = "ابدأ لعبة جديدة",
            ["/ui/tooltip"] = "اضغط \"إدخال\" للمتابعة",
            ["/quests/0/title"] = "اعثر على الأثر المفقود",
            ["/quests/0/choices/0"] = "قبول المهمة"
        });

        string updatedText = Encoding.UTF8.GetString(rewritten);
        Assert.Contains("# Game UI & Quest Localization", updatedText);
        Assert.Contains("version = 2", updatedText);
        Assert.Contains("enabled = true", updatedText);
        Assert.Contains("id = \"QUEST_01\"", updatedText);
        Assert.Contains("play = \"ابدأ لعبة جديدة\"", updatedText);

        var reExtracted = format.Extract(rewritten);
        Assert.Equal("ابدأ لعبة جديدة", reExtracted.Single(s => s.Locator == "/ui/play").Source);
        Assert.Equal("اضغط \"إدخال\" للمتابعة", reExtracted.Single(s => s.Locator == "/ui/tooltip").Source);
        Assert.Equal("اعثر على الأثر المفقود", reExtracted.Single(s => s.Locator == "/quests/0/title").Source);
        Assert.Equal("قبول المهمة", reExtracted.Single(s => s.Locator == "/quests/0/choices/0").Source);
    }

    [Theory]
    [InlineData(false)] // UTF-8
    [InlineData(true)]  // UTF-16LE with BOM (standard Apple/Cocoa Localizable.strings)
    public void StringsFormatExtractsCommentsAndPreservesUtf8AndUtf16BomEncodings(bool utf16Le)
    {
        const string content =
            "/* Main menu start button */\n" +
            "\"MENU_START\" = \"Start Adventure\";\n" +
            "// Confirmation dialog with escaped quotes\n" +
            "\"CONFIRM_QUIT\" = \"Are you sure you want to \\\"Quit\\\"?\\nUnsaved progress will be lost.\";\n";

        Encoding enc = utf16Le ? new UnicodeEncoding(bigEndian: false, byteOrderMark: true) : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        byte[] raw = [.. enc.GetPreamble(), .. enc.GetBytes(content)];

        var format = Formats.For("en.lproj/Localizable.strings");
        Assert.NotNull(format);
        Assert.Equal("strings", format!.Name);
        Assert.True(ProjectService.IsDedicatedLocalizationFormat("en.lproj/Localizable.strings", format.Name));

        var slots = format.Extract(raw);
        Assert.Equal(2, slots.Count);
        var startSlot = slots.Single(s => s.Locator == "key:MENU_START");
        Assert.Equal("Start Adventure", startSlot.Source);
        Assert.Contains("Main menu start button", startSlot.Context);

        var quitSlot = slots.Single(s => s.Locator == "key:CONFIRM_QUIT");
        Assert.Equal("Are you sure you want to \"Quit\"?\nUnsaved progress will be lost.", quitSlot.Source);
        Assert.Contains("Confirmation dialog", quitSlot.Context);

        byte[] rewritten = format.Apply(raw, new Dictionary<string, string>
        {
            ["key:MENU_START"] = "ابدأ المغامرة",
            ["key:CONFIRM_QUIT"] = "هل أنت متأكد من \"الخروج\"؟\nسيُفقد التقدّم غير المحفوظ."
        });

        if (utf16Le)
        {
            Assert.Equal(0xFF, rewritten[0]);
            Assert.Equal(0xFE, rewritten[1]);
        }

        var reExtracted = format.Extract(rewritten);
        Assert.Equal("ابدأ المغامرة", reExtracted.Single(s => s.Locator == "key:MENU_START").Source);
        Assert.Equal("هل أنت متأكد من \"الخروج\"?\nسيُفقد التقدّم غير المحفوظ.".Replace('?', '؟'),
            reExtracted.Single(s => s.Locator == "key:CONFIRM_QUIT").Source);
    }

    [Fact]
    public void LuaFormatExtractsLocalizationTablesWhileIgnoringRequireCallsAndTableKeys()
    {
        const string lua = """
            local utils = require("core.localization_utils")
            -- Localization table
            local Strings = {
                id = "LOC_EN_MAIN",
                ["menu.play"] = "Play Campaign",
                ['menu.quit'] = 'Exit Game',
                dialogue = {
                    greeting = "Welcome back, Commander!",
                    lines = { "Shields are holding.", [[ Hull integrity at 100%. ]] }
                }
            }
            print("debug_ignore_me")
            return Strings
            """;

        var format = Formats.For("scripts/lang/en.lua");
        Assert.NotNull(format);
        Assert.Equal("lua", format!.Name);

        byte[] raw = Encoding.UTF8.GetBytes(lua);
        var slots = format.Extract(raw);

        // require(...) and print(...) arguments and bracketed keys ["menu.play"] must NOT be extracted
        Assert.DoesNotContain(slots, s => s.Source.Contains("localization_utils") || s.Source.Contains("debug_ignore_me"));
        Assert.DoesNotContain(slots, s => s.Source == "menu.play");

        Assert.Contains(slots, s => s.Locator == "/Strings/id" && s.Source == "LOC_EN_MAIN");
        Assert.Contains(slots, s => s.Locator == "/Strings/menu.play" && s.Source == "Play Campaign");
        Assert.Contains(slots, s => s.Locator == "/Strings/menu.quit" && s.Source == "Exit Game");
        Assert.Contains(slots, s => s.Locator == "/Strings/dialogue/greeting" && s.Source == "Welcome back, Commander!");
        Assert.Contains(slots, s => s.Locator == "/Strings/dialogue/lines/0" && s.Source == "Shields are holding.");

        byte[] rewritten = format.Apply(raw, new Dictionary<string, string>
        {
            ["/Strings/menu.play"] = "ابدأ الحملة",
            ["/Strings/menu.quit"] = "الخروج من اللعبة",
            ["/Strings/dialogue/greeting"] = "مرحباً بعودتك أيها القائد!"
        });

        string updatedLua = Encoding.UTF8.GetString(rewritten);
        Assert.Contains("require(\"core.localization_utils\")", updatedLua);
        Assert.Contains("[\"menu.play\"] = \"ابدأ الحملة\"", updatedLua);
        Assert.Contains("print(\"debug_ignore_me\")", updatedLua);

        var reExtracted = format.Extract(rewritten);
        Assert.Equal("ابدأ الحملة", reExtracted.Single(s => s.Locator == "/Strings/menu.play").Source);
        Assert.Equal("الخروج من اللعبة", reExtracted.Single(s => s.Locator == "/Strings/menu.quit").Source);
        Assert.Equal("مرحباً بعودتك أيها القائد!", reExtracted.Single(s => s.Locator == "/Strings/dialogue/greeting").Source);
    }

    [Fact]
    public void UnrealStringTableFormatSupportsSplitCookedUassetUexpOnDiskAndInsidePak()
    {
        using var ws = new TempWorkspace();
        var (uassetBytes, uexpBytes) = BuildSyntheticCookedUnrealStringTable(
            tableNamespace: "UI_MainMenu",
            entries: [("Btn_Play", "Start Expedition"), ("Btn_Settings", "Settings")],
            metaEntries: [("Btn_Play", "Comment", "Main menu button")]);

        // 1. Loose cooked split .uasset + .uexp in an Unreal folder project
        string looseGame = Directory.CreateDirectory(ws.PathOf("ue-loose-st")).FullName;
        string contentDir = Directory.CreateDirectory(Path.Combine(looseGame, "MyGame", "Content", "Localization")).FullName;
        Directory.CreateDirectory(Path.Combine(looseGame, "MyGame", "Content", "Paks"));
        File.WriteAllBytes(Path.Combine(looseGame, "MyGame", "Content", "Paks", "dummy.pak"), new byte[16]); // Unreal marker
        File.WriteAllBytes(Path.Combine(contentDir, "ST_Menu.uasset"), uassetBytes);
        File.WriteAllBytes(Path.Combine(contentDir, "ST_Menu.uexp"), uexpBytes);

        using (var store = ProjectStore.Create(ws.PathOf("ue-loose-proj"), "UELooseST", looseGame))
        {
            var service = new ProjectService(store);
            var scan = service.Scan();
            Assert.Equal("unreal", store.Get("scanned_engine"));
            Assert.Equal(1, scan.Files);
            Assert.Equal(2, scan.Units);

            foreach (var u in store.Units())
            {
                u.Target = u.Source == "Start Expedition" ? "ابدأ البعثة" : "الإعدادات";
                store.Save(u, approve: true);
            }

            var deployed = PackageInstaller.DeployInPlace(store, ws.PathOf("ue-loose-out"), final: true);
            Assert.Equal(2, deployed.Install.Files); // Both ST_Menu.uasset (SerialSize updated) and ST_Menu.uexp (UTF-16LE Arabic payload)

            var reExtracted = UnrealStringTableFormat.ExtractFromFiles(
                Path.Combine(contentDir, "ST_Menu.uasset"),
                Path.Combine(contentDir, "ST_Menu.uexp"));
            Assert.Equal("ابدأ البعثة", reExtracted.Single(s => s.Locator.EndsWith(":Btn_Play")).Source);
            Assert.Equal("الإعدادات", reExtracted.Single(s => s.Locator.EndsWith(":Btn_Settings")).Source);

            PackageInstaller.RestoreInPlace(store);
            Assert.Equal(uassetBytes, File.ReadAllBytes(Path.Combine(contentDir, "ST_Menu.uasset")));
            Assert.Equal(uexpBytes, File.ReadAllBytes(Path.Combine(contentDir, "ST_Menu.uexp")));
        }

        // 2. Cooked StringTable (.uasset + .uexp) packed inside an Unreal v11 .pak archive (with zero .locres files!)
        string pakGame = Directory.CreateDirectory(ws.PathOf("ue-pak-st")).FullName;
        string paksDir = Directory.CreateDirectory(Path.Combine(pakGame, "Explorer", "Content", "Paks")).FullName;
        string basePak = Path.Combine(paksDir, "Explorer-Windows.pak");
        UnrealPakCodec.WritePatchPak(basePak, "../../../", [
            new UnrealPakPatchFile("Explorer/Content/Data/ST_UI.uasset", uassetBytes),
            new UnrealPakPatchFile("Explorer/Content/Data/ST_UI.uexp", uexpBytes)
        ], targetVersion: 11);

        using (var pakStore = ProjectStore.Create(ws.PathOf("ue-pak-proj"), "UEPakST", pakGame))
        {
            var pakService = new ProjectService(pakStore);
            var pakScan = pakService.Scan();
            Assert.Equal(1, pakScan.Files);
            Assert.Equal(2, pakScan.Units);

            foreach (var u in pakStore.Units())
            {
                u.Target = u.Source == "Start Expedition" ? "انطلق في الرحلة" : "الخيارات";
                pakStore.Save(u, approve: true);
            }

            var built = pakService.Build(ws.PathOf("ue-pak-out"), final: true);
            string patchPak = Path.Combine(built.Directory, "resources", "Explorer", "Content", "Paks", "Explorer-Windows_Locara_P.pak");
            Assert.True(File.Exists(patchPak));

            var rePakSlots = UnrealPakFormat.ExtractFromFile(patchPak);
            Assert.Equal("انطلق في الرحلة", rePakSlots.Single(s => s.Locator.EndsWith(":Btn_Play")).Source);
            Assert.Equal("الخيارات", rePakSlots.Single(s => s.Locator.EndsWith(":Btn_Settings")).Source);
        }
    }

    [Fact]
    public void GameMakerEngineProfileDetectsDataWinSupportsExternalLocalizationAndAdvisesWhenCompiledOnly()
    {
        using var ws = new TempWorkspace();

        // Case 1: Compiled data.win only -> detected as gamemaker with clear Arabic diagnostic & advisor note
        string compiledOnly = Directory.CreateDirectory(ws.PathOf("gm-compiled")).FullName;
        File.WriteAllBytes(Path.Combine(compiledOnly, "data.win"), "FORM\x04\x00\x00\x00STRG"u8.ToArray());

        using (var store1 = ProjectStore.Create(ws.PathOf("gm-proj-1"), "GMCompiled", compiledOnly))
        {
            var svc1 = new ProjectService(store1);
            var cap1 = svc1.Capabilities();
            Assert.Equal("gamemaker", cap1.Engine);
            Assert.False(cap1.CanRead);

            var scan1 = svc1.Scan();
            Assert.Equal(0, scan1.Units);
            Assert.Contains(scan1.Diagnostics, d => d.Message.Contains("GameMaker") && d.Message.Contains("data.win"));

            var advice1 = svc1.AnalyzeDeployment();
            Assert.Equal("gamemaker", advice1.Engine);
            Assert.Contains(advice1.Notes, n => n.Contains("data.win"));
        }

        // Case 2: GameMaker game with external localization CSV/JSON -> extracts & builds cleanly under gamemaker profile
        string gmExternal = Directory.CreateDirectory(ws.PathOf("gm-external")).FullName;
        File.WriteAllBytes(Path.Combine(gmExternal, "data.win"), "FORM\x04\x00\x00\x00STRG"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(gmExternal, "lang"));
        File.WriteAllText(Path.Combine(gmExternal, "lang", "english.json"), """{"start":"Start Quest","quit":"Exit"}""");

        using (var store2 = ProjectStore.Create(ws.PathOf("gm-proj-2"), "GMExternal", gmExternal))
        {
            var svc2 = new ProjectService(store2);
            var cap2 = svc2.Capabilities();
            Assert.Equal("gamemaker", cap2.Engine);
            Assert.True(cap2.CanRead);

            var scan2 = svc2.Scan();
            Assert.Equal(2, scan2.Units);
        }
    }

    [Fact]
    public void SecondaryLocaleFilesInPckAreLazySkippedDuringInitialScanAndExtractedOnDemandWhenIncluded()
    {
        using var ws = new TempWorkspace();
        string pckPath = ws.PathOf("multilang_godot.pck");
        var pck = new PckContainer();
        var templateIdx = new ResourceIndex { GodotVersion = [4u, 5u, 0u], PckFormatVersion = 3u, PckUsesResPrefix = false };
        pck.WriteIndexed(pckPath, [
            ResourceEntry.FromBytes("project.binary", "ECFG"u8.ToArray()),
            ResourceEntry.FromBytes("localization/eng/ui.json", """{"play":"Play Game","quit":"Exit"}"""u8.ToArray()),
            ResourceEntry.FromBytes("localization/deu/ui.json", """{"play":"Spiel starten","quit":"Beenden"}"""u8.ToArray()),
            ResourceEntry.FromBytes("localization/fra/unused.json", "{ UNPARSED_SECONDARY_LOCALE"u8.ToArray())
        ], null, CancellationToken.None, templateIdx);

        using var store = ProjectStore.Create(ws.PathOf("lazy-pck-proj"), "LazyPck", pckPath);
        var service = new ProjectService(store);
        var scan = service.Scan();

        Assert.Equal(3, scan.Files);
        Assert.Equal(2, scan.Units);

        // Secondary locales were NOT parsed during initial scan:
        // 1) Zero parse error diagnostics from the malformed unused French locale
        Assert.DoesNotContain(scan.Diagnostics, d => d.Path.Contains("fra", StringComparison.OrdinalIgnoreCase) && !d.Message.Contains("استُبعد"));
        // 2) Zero excluded secondary-locale rows inserted into SQLite units table
        int totalDbUnits = store.Query("SELECT COUNT(*) FROM units WHERE active IN (1, 2)", r => r.GetInt32(0))[0];
        Assert.Equal(2, totalDbUnits);

        // ScannedFiles still lists all 3 files with SecondaryLanguage verdicts
        var files = service.ScannedFiles();
        Assert.Equal(3, files.Count);
        var deuSummary = files.Single(f => f.Path.Contains("deu", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(FileQualityVerdict.SecondaryLanguage, deuSummary.Verdict);
        Assert.False(deuSummary.Included);

        // Preview and Build succeed with full SHA-256 container integrity verification even with lazy-skipped entries
        var engUnit = store.Units().First();
        engUnit.Target = "ابدأ اللعب";
        store.Save(engUnit, approve: true);
        var built = service.Build(ws.PathOf("lazy-pck-out"), final: true);
        Assert.Equal(1, built.Translations);

        // On-demand extraction when the user manually includes the German file
        service.SetFileIncluded(deuSummary.Path, included: true);
        Assert.Equal(4, store.Units().Count);
        Assert.Contains(store.Units(), u => u.Source == "Spiel starten");
        Assert.Equal(2, service.ScannedFiles().Single(f => f.Path == deuSummary.Path).TotalUnits);
    }

    [Fact]
    public void UnityTmpArabicPatcherAppliesContrastBoostToFontWeightAndMaterialFaceDilate()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return;

        var opts = UnityArabicDisplayOptions.Parse("auto", "0.85", "1", "1");
        Assert.True(opts.ContrastBoost);
        Assert.False(UnityArabicDisplayOptions.Parse("auto", "0.85", "1", "0").ContrastBoost);

        // Regular built-in font upgrades to its Bold counterpart when ContrastBoost is enabled
        var regularNoto = ArabicFontCatalog.Resolve("builtin:noto-sans-regular");
        var boostedChoice = ArabicFontCatalog.ResolveContrastVariant(regularNoto);
        Assert.Equal("builtin:noto-sans-bold", boostedChoice.Id);

        // Build synthetic TMP Fallback Material (classId = 21) containing _FaceDilate = 0.0f
        byte[] fallbackMat = BuildSyntheticTmpFallbackMaterial(initialFaceDilate: 0.0f, out int dilateValueOffset);
        byte[] patchedMat = UnityTmpArabicPatcher.TryBoostFallbackMaterial(fallbackMat)!;
        Assert.NotNull(patchedMat);
        Assert.Equal(fallbackMat.Length, patchedMat.Length);
        float boostedDilate = BinaryPrimitives.ReadSingleLittleEndian(patchedMat.AsSpan(dilateValueOffset));
        Assert.InRange(boostedDilate, 0.12f, 0.25f);
    }

    [Fact]
    public void ArabicPromptPresetsProvideCuratedRoleInstructionsAndPreserveRoutingSettings()
    {
        Assert.True(ArabicPromptPresets.All.Count >= 6);
        var rpg = ArabicPromptPresets.Find("story-rpg");
        Assert.NotNull(rpg);
        Assert.NotEmpty(rpg!.Style);
        Assert.NotEmpty(rpg.Supervisor);
        Assert.NotEmpty(rpg.Translator);
        Assert.NotEmpty(rpg.Reviewer);

        var initial = new TeamSettings(Workers: 8, BatchSize: 25, TranslatorProfile: "FastAPI", TranslatorModel: "gemini-2.5-flash");
        var updated = ArabicPromptPresets.ApplyTo(initial, "story-rpg");
        Assert.Equal(rpg.Style, updated.Style);
        Assert.Equal(rpg.Translator, updated.Translator);
        Assert.Equal(8, updated.Workers);
        Assert.Equal(25, updated.BatchSize);
        Assert.Equal("FastAPI", updated.TranslatorProfile);

        string preview = TranslationTeam.PromptPreview("translator", updated);
        Assert.Contains(rpg.Style, preview);
        Assert.Contains(rpg.Translator, preview);
    }

    private static byte[] BuildSyntheticTmpFallbackMaterial(float initialFaceDilate, out int dilateFloatOffset)
    {
        using var ms = new MemoryStream();
        static void WriteAligned(Stream s, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            Span<byte> lenBuf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, utf8.Length);
            s.Write(lenBuf);
            s.Write(utf8);
            int pad = (4 - (utf8.Length % 4)) % 4;
            if (pad > 0) s.Write(new byte[pad]);
        }

        WriteAligned(ms, "LiberationSans SDF - Fallback Material");
        ms.Write(new byte[32]); // shader PPtr + keywords stub
        WriteAligned(ms, "_FaceDilate");
        dilateFloatOffset = (int)ms.Position;
        Span<byte> flt = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(flt, initialFaceDilate);
        ms.Write(flt);
        WriteAligned(ms, "_OutlineWidth");
        BinaryPrimitives.WriteSingleLittleEndian(flt, 0.0f);
        ms.Write(flt);
        return ms.ToArray();
    }

    private static (byte[] Uasset, byte[] Uexp) BuildSyntheticCookedUnrealStringTable(
        string tableNamespace,
        (string Key, string Value)[] entries,
        (string Key, string MetaId, string MetaValue)[] metaEntries)
    {
        // NameMap: [0]="None", [1]="StringTable", [2]="ST_Menu", [3..]=metaIds
        var names = new List<string> { "None", "StringTable", "ST_Menu" };
        foreach (var m in metaEntries)
        {
            if (!names.Contains(m.MetaId, StringComparer.Ordinal))
                names.Add(m.MetaId);
        }

        byte[] payload;
        using (var pms = new MemoryStream())
        {
            // UObject::Serialize tagged properties terminator: FName("None") = (0, 0) + int32 bHasGuid = 0
            WriteI32(pms, 0); // NameIndex = 0 ("None")
            WriteI32(pms, 0); // Number = 0
            WriteI32(pms, 0); // bHasGuid = 0

            // FStringTable
            UnrealLocresFormat.WriteFString(pms, tableNamespace);
            WriteI32(pms, entries.Length);
            foreach (var (k, v) in entries)
            {
                UnrealLocresFormat.WriteFString(pms, k);
                UnrealLocresFormat.WriteFString(pms, v);
            }

            var groupedMeta = metaEntries.GroupBy(m => m.Key, StringComparer.Ordinal).ToList();
            WriteI32(pms, groupedMeta.Count);
            foreach (var g in groupedMeta)
            {
                UnrealLocresFormat.WriteFString(pms, g.Key);
                var list = g.ToList();
                WriteI32(pms, list.Count);
                foreach (var item in list)
                {
                    int nameIdx = names.IndexOf(item.MetaId);
                    WriteI32(pms, nameIdx);
                    WriteI32(pms, 0);
                    UnrealLocresFormat.WriteFString(pms, item.MetaValue);
                }
            }
            payload = pms.ToArray();
        }

        byte[] uexp = new byte[payload.Length + 4];
        Buffer.BlockCopy(payload, 0, uexp, 0, payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(uexp.AsSpan(payload.Length), 0x9E2A83C1u);

        // Build minimal UE4 (-7, FileVersionUE4 = 522) cooked FPackageFileSummary + NameMap + ImportMap + ExportMap
        using var hms = new MemoryStream();
        WriteU32(hms, 0x9E2A83C1u); // Tag
        WriteI32(hms, -7);          // LegacyFileVersion (UE4)
        WriteI32(hms, 864);         // LegacyUE3Version
        WriteI32(hms, 522);         // FileVersionUE4 (UE 4.27)
        WriteI32(hms, 0);           // FileVersionLicenseeUE
        WriteI32(hms, 0);           // CustomVersionContainer.Count = 0

        long totalHeaderSizePos = hms.Position;
        WriteI32(hms, 0);           // TotalHeaderSize placeholder
        UnrealLocresFormat.WriteFString(hms, "None"); // FolderName
        WriteU32(hms, 0x80000000u); // PackageFlags = PKG_FilterEditorOnly (cooked!)

        long nameCountPos = hms.Position;
        WriteI32(hms, names.Count);
        WriteI32(hms, 0);           // NameOffset placeholder

        WriteI32(hms, 0);           // GatherableTextDataCount
        WriteI32(hms, 0);           // GatherableTextDataOffset

        long exportCountPos = hms.Position;
        WriteI32(hms, 1);           // ExportCount = 1
        WriteI32(hms, 0);           // ExportOffset placeholder
        WriteI32(hms, 1);           // ImportCount = 1
        WriteI32(hms, 0);           // ImportOffset placeholder
        WriteI32(hms, 0);           // DependsOffset
        hms.Write(new byte[64]);    // remaining summary padding before tables

        int nameOffset = (int)hms.Position;
        foreach (string n in names)
        {
            UnrealLocresFormat.WriteFString(hms, n);
            WriteU32(hms, 0); // NonCasePreservingHash + CasePreservingHash (2x uint16 = 4B)
        }

        int importOffset = (int)hms.Position;
        // 1 FObjectImport (28 bytes): ClassPackage("None"), ClassName("Class"), OuterIndex(0), ObjectName("StringTable")
        WriteI32(hms, 0); WriteI32(hms, 0);
        WriteI32(hms, 0); WriteI32(hms, 0);
        WriteI32(hms, 0);
        WriteI32(hms, 1); WriteI32(hms, 0); // ObjectName = "StringTable" (index 1)

        int exportOffset = (int)hms.Position;
        // 1 FObjectExport (UE 4.27 cooked):
        WriteI32(hms, -1); // ClassIndex = -1 -> Import[0] ("StringTable")
        WriteI32(hms, 0);  // SuperIndex
        WriteI32(hms, 0);  // TemplateIndex
        WriteI32(hms, 0);  // OuterIndex
        WriteI32(hms, 2); WriteI32(hms, 0); // ObjectName = "ST_Menu" (index 2)
        WriteU32(hms, 0);  // ObjectFlags
        long serialSizePos = hms.Position;
        WriteI64(hms, payload.LongLength); // SerialSize (int64 at ExportOffset + 28)
        long serialOffsetPos = hms.Position;
        WriteI64(hms, 0);  // SerialOffset placeholder (int64 at ExportOffset + 36)
        hms.Write(new byte[60]); // rest of FObjectExport fields

        int totalHeaderSize = (int)hms.Position;
        hms.Position = totalHeaderSizePos; WriteI32(hms, totalHeaderSize);
        hms.Position = nameCountPos + 4; WriteI32(hms, nameOffset);
        hms.Position = exportCountPos + 4; WriteI32(hms, exportOffset);
        hms.Position = exportCountPos + 12; WriteI32(hms, importOffset);
        hms.Position = serialOffsetPos; WriteI64(hms, totalHeaderSize);
        return (hms.ToArray(), uexp);

        static void WriteI32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }
        static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
        static void WriteI64(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); s.Write(b); }
    }
}
