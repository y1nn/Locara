using System.Buffers.Binary;
using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class GameMakerDataWinTests
{
    private static readonly byte[] SamplePngBlob =
    [
        0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x10, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0xF3, 0xFF
    ];

    private static readonly byte[] SampleQoiBlob =
    [
        (byte)'q', (byte)'o', (byte)'i', (byte)'f',
        0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08, 0x04, 0x00,
        0xFE, 0x11, 0x22, 0x33, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
    ];

    private static readonly byte[] SampleWavBlob =
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x24, 0x00, 0x00, 0x00,
        (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ',
        0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x44, 0xAC, 0x00, 0x00
    ];

    private sealed record SyntheticDataWin(
        byte[] Bytes,
        int StrgHeaderOffset,
        uint OriginalFormSize,
        uint OriginalStrgSize,
        int TxtrHeaderOffset,
        int AudoHeaderOffset,
        uint OriginalPngOffset,
        uint OriginalQoiOffset,
        uint OriginalWavOffset,
        Dictionary<string, int> StringIndices);

    [Theory]
    [InlineData("data.win", true)]
    [InlineData("C:\\Games\\Sample\\DATA.WIN", true)]
    [InlineData("assets/game.unx", true)]
    [InlineData("Payload/App.app/game.ios", true)]
    [InlineData("strings.json", false)]
    [InlineData("game.pck", false)]
    [InlineData("data.win.bak", false)]
    public void SupportsMatchesOnlyGameMakerIffDataFiles(string path, bool expected)
    {
        Assert.Equal(expected, GameMakerDataWinFormat.Instance.Supports(path));
        Assert.Equal("gamemaker-datawin", GameMakerDataWinFormat.Instance.Name);
    }

    [Fact]
    public void ExtractExcludesAllStructuralIdentifiersAndTechnicalStringsAndKeepsOnlyHumanText()
    {
        using var ws = new TempWorkspace();
        SyntheticDataWin fixture = BuildSyntheticDataWin();
        string filePath = ws.PathOf("data.win");
        File.WriteAllBytes(filePath, fixture.Bytes);

        Assert.True(GameMakerDataWinFormat.HasTranslatableStrings(filePath));

        // Corrupt/truncated stub must safely probe false.
        string stubPath = ws.PathOf("stub.win");
        File.WriteAllBytes(stubPath, "FORM\x04\x00\x00\x00STRG"u8.ToArray());
        Assert.False(GameMakerDataWinFormat.HasTranslatableStrings(stubPath));

        IReadOnlyList<TextSlot> fromBytes = GameMakerDataWinFormat.Instance.Extract(fixture.Bytes);
        IReadOnlyList<TextSlot> fromFile = GameMakerDataWinFormat.ExtractFromFile(filePath);

        Assert.Equal(fromBytes, fromFile);
        Assert.Equal(4, fromFile.Count);
        Assert.All(fromFile, slot => Assert.Equal("GameMaker STRG", slot.Context));

        var extractedSources = fromFile.Select(s => s.Source).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Press Enter to begin your journey.", extractedSources);
        Assert.Contains("Start Game", extractedSources);
        Assert.Contains("Welcome to the ancient village, traveler!", extractedSources);
        Assert.Contains("Inventory Full", extractedSources);

        // Verify locators match exact STRG indices.
        Assert.Equal($"strg:{fixture.StringIndices["human_start"]}",
            fromFile.Single(s => s.Source == "Start Game").Locator);
        Assert.Equal($"strg:{fixture.StringIndices["human_welcome"]}",
            fromFile.Single(s => s.Source == "Welcome to the ancient village, traveler!").Locator);

        // Structural identifiers MUST be excluded — including human-looking GEN8.DisplayName and ROOM.Caption!
        Assert.DoesNotContain("Chronicles of Dawn", extractedSources);        // GEN8.DisplayName
        Assert.DoesNotContain("ChroniclesGame", extractedSources);            // GEN8.Name
        Assert.DoesNotContain("Castle Entrance", extractedSources);           // ROOM.Caption
        Assert.DoesNotContain("Noto Sans", extractedSources);                 // FONT.DisplayName
        Assert.DoesNotContain("HeroSprite", extractedSources);                // SPRT.Name
        Assert.DoesNotContain("PlayerObject", extractedSources);              // OBJT.Name
        Assert.DoesNotContain("MainRoom", extractedSources);                  // ROOM.Name
        Assert.DoesNotContain("IntroShader", extractedSources);               // SHDR.Name
        Assert.DoesNotContain("StepScript", extractedSources);                // CODE.Name
        Assert.DoesNotContain("health", extractedSources);                    // VARI
        Assert.DoesNotContain("draw_dialog", extractedSources);               // FUNC

        // Unreferenced technical strings (shader code, GM prefixes, filenames) MUST also be excluded.
        Assert.DoesNotContain(fromFile, s => s.Source.Contains("gl_Position", StringComparison.Ordinal));
        Assert.DoesNotContain(fromFile, s => s.Source.Contains("gl_FragColor", StringComparison.Ordinal));
        Assert.DoesNotContain(fromFile, s => s.Source.StartsWith("gml_", StringComparison.Ordinal));
        Assert.DoesNotContain(fromFile, s => s.Source.StartsWith("@@", StringComparison.Ordinal));
        Assert.DoesNotContain(fromFile, s => s.Source.StartsWith("spr_", StringComparison.Ordinal));
        Assert.DoesNotContain(fromFile, s => s.Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fromFile, s => s.Source.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InPlaceShorterEditKeepsZeroDeltaIdenticalFileLengthAndRoundTrips()
    {
        SyntheticDataWin fixture = BuildSyntheticDataWin();
        string startLocator = $"strg:{fixture.StringIndices["human_start"]}";
        string journeyLocator = $"strg:{fixture.StringIndices["human_journey"]}";

        byte[] rebuilt = GameMakerDataWinFormat.Instance.Apply(fixture.Bytes, new Dictionary<string, string>
        {
            [startLocator] = "Play Now",
            [journeyLocator] = "Press Enter to start."
        });

        // Delta == 0: file size and FORM/STRG chunk lengths unchanged.
        Assert.Equal(fixture.Bytes.Length, rebuilt.Length);
        Assert.Equal(fixture.OriginalFormSize, ReadUInt32(rebuilt, 4));
        Assert.Equal(fixture.OriginalStrgSize, ReadUInt32(rebuilt, fixture.StrgHeaderOffset + 4));

        // Pre-STRG chunks byte-for-byte identical.
        Assert.Equal(
            fixture.Bytes.AsSpan(8, fixture.StrgHeaderOffset - 8).ToArray(),
            rebuilt.AsSpan(8, fixture.StrgHeaderOffset - 8).ToArray());

        // Post-STRG chunks (TXTR + AUDO) byte-for-byte identical.
        Assert.Equal(
            fixture.Bytes.AsSpan(fixture.TxtrHeaderOffset).ToArray(),
            rebuilt.AsSpan(fixture.TxtrHeaderOffset).ToArray());

        IReadOnlyList<TextSlot> roundTrip = GameMakerDataWinFormat.Instance.Extract(rebuilt);
        Assert.Equal("Play Now", roundTrip.Single(s => s.Locator == startLocator).Source);
        Assert.Equal("Press Enter to start.", roundTrip.Single(s => s.Locator == journeyLocator).Source);
        Assert.Equal("Welcome to the ancient village, traveler!",
            roundTrip.Single(s => s.Locator == $"strg:{fixture.StringIndices["human_welcome"]}").Source);
    }

    [Fact]
    public void ExpandedArabicEditMaintains128ByteAlignmentShiftsTxtrAndAudoAndPreservesPreStrgChunks()
    {
        using var ws = new TempWorkspace();
        SyntheticDataWin fixture = BuildSyntheticDataWin();
        string srcPath = ws.PathOf("data.win");
        string dstPath = ws.PathOf("rebuilt", "data.win");
        File.WriteAllBytes(srcPath, fixture.Bytes);

        string startLocator = $"strg:{fixture.StringIndices["human_start"]}";
        string welcomeLocator = $"strg:{fixture.StringIndices["human_welcome"]}";
        const string arabicStart = "ابدأ المغامرة الكبرى الآن";
        const string arabicWelcome = "مرحباً بك أيها المسافر في القرية القديمة المليئة بالأسرار والحكايات!";

        GameMakerDataWinFormat.ApplyToFile(srcPath, dstPath, new Dictionary<string, string>
        {
            [startLocator] = arabicStart,
            [welcomeLocator] = arabicWelcome
        }, shapeArabic: false);

        byte[] rebuilt = File.ReadAllBytes(dstPath);
        int delta = rebuilt.Length - fixture.Bytes.Length;

        // Expansion invariants: delta > 0 and exact multiple of 128 bytes.
        Assert.True(delta > 0);
        Assert.Equal(0, delta % 128);
        Assert.Equal(fixture.OriginalFormSize + (uint)delta, ReadUInt32(rebuilt, 4));
        Assert.Equal(fixture.OriginalStrgSize + (uint)delta, ReadUInt32(rebuilt, fixture.StrgHeaderOffset + 4));

        // Pre-STRG chunks (GEN8..FUNC) are 100% byte-for-byte identical.
        Assert.Equal(
            fixture.Bytes.AsSpan(8, fixture.StrgHeaderOffset - 8).ToArray(),
            rebuilt.AsSpan(8, fixture.StrgHeaderOffset - 8).ToArray());

        // Verify TXTR chunk header shifted cleanly by delta and texture blobs remain 128-byte aligned and intact.
        int newTxtrHeader = fixture.TxtrHeaderOffset + delta;
        Assert.Equal("TXTR", Encoding.ASCII.GetString(rebuilt, newTxtrHeader, 4));
        int txtrPayload = newTxtrHeader + 8;
        uint texCount = ReadUInt32(rebuilt, txtrPayload);
        Assert.Equal(3u, texCount);

        uint texEntry0 = ReadUInt32(rebuilt, txtrPayload + 4);
        uint texEntry1 = ReadUInt32(rebuilt, txtrPayload + 8);
        uint texEntry2 = ReadUInt32(rebuilt, txtrPayload + 12);

        uint shiftedPngOffset = ReadUInt32(rebuilt, (int)texEntry0 + 12);
        uint shiftedExternalOffset = ReadUInt32(rebuilt, (int)texEntry1 + 12);
        uint shiftedQoiOffset = ReadUInt32(rebuilt, (int)texEntry2 + 12);

        Assert.Equal(fixture.OriginalPngOffset + (uint)delta, shiftedPngOffset);
        Assert.Equal(0u, shiftedExternalOffset); // External texture pointer remains 0.
        Assert.Equal(fixture.OriginalQoiOffset + (uint)delta, shiftedQoiOffset);

        Assert.Equal(0u, shiftedPngOffset % 128u);
        Assert.Equal(0u, shiftedQoiOffset % 128u);
        Assert.Equal(SamplePngBlob, rebuilt.AsSpan((int)shiftedPngOffset, SamplePngBlob.Length).ToArray());
        Assert.Equal(SampleQoiBlob, rebuilt.AsSpan((int)shiftedQoiOffset, SampleQoiBlob.Length).ToArray());

        // Verify AUDO chunk shifted by delta and WAV blob intact.
        int newAudoHeader = fixture.AudoHeaderOffset + delta;
        Assert.Equal("AUDO", Encoding.ASCII.GetString(rebuilt, newAudoHeader, 4));
        int audoPayload = newAudoHeader + 8;
        Assert.Equal(1u, ReadUInt32(rebuilt, audoPayload));
        uint shiftedWavEntry = ReadUInt32(rebuilt, audoPayload + 4);
        Assert.Equal(fixture.OriginalWavOffset + (uint)delta, shiftedWavEntry);
        Assert.Equal((uint)SampleWavBlob.Length, ReadUInt32(rebuilt, (int)shiftedWavEntry));
        Assert.Equal(SampleWavBlob, rebuilt.AsSpan((int)shiftedWavEntry + 4, SampleWavBlob.Length).ToArray());

        // Round-trip extraction returns exact logical UTF-8 Arabic strings.
        IReadOnlyList<TextSlot> roundTrip = GameMakerDataWinFormat.ExtractFromFile(dstPath);
        Assert.Equal(arabicStart, roundTrip.Single(s => s.Locator == startLocator).Source);
        Assert.Equal(arabicWelcome, roundTrip.Single(s => s.Locator == welcomeLocator).Source);
        Assert.Equal("Inventory Full", roundTrip.Single(s => s.Locator == $"strg:{fixture.StringIndices["human_inv"]}").Source);
    }

    [Fact]
    public void ApplyToFileWithShapeArabicTrueShapesGlyphsIntoPresentationForms()
    {
        using var ws = new TempWorkspace();
        SyntheticDataWin fixture = BuildSyntheticDataWin();
        string srcPath = ws.PathOf("data.win");
        string dstPath = ws.PathOf("shaped.win");
        File.WriteAllBytes(srcPath, fixture.Bytes);

        string locator = $"strg:{fixture.StringIndices["human_start"]}";
        const string logicalArabic = "ابدأ اللعبة الجديدة";
        string expectedShaped = ArabicTextEngine.Shape(logicalArabic);

        Assert.NotEqual(logicalArabic, expectedShaped);
        Assert.Contains(expectedShaped, c => (c >= '\uFB50' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF'));

        GameMakerDataWinFormat.ApplyToFile(srcPath, dstPath, new Dictionary<string, string>
        {
            [locator] = logicalArabic
        }, shapeArabic: true);

        IReadOnlyList<TextSlot> roundTrip = GameMakerDataWinFormat.ExtractFromFile(dstPath);
        Assert.Equal(expectedShaped, roundTrip.Single(s => s.Locator == locator).Source);
    }

    [Fact]
    public void ExpandingStrgThrowsNotSupportedExceptionWhenUnknownChunkFollowsStrg()
    {
        SyntheticDataWin fixture = BuildSyntheticDataWin(appendUnknownChunkAfterStrg: true);
        string startLocator = $"strg:{fixture.StringIndices["human_start"]}";

        // Expanded edit (delta > 0) MUST fail fast instead of corrupting unknown post-STRG chunk.
        var ex = Assert.Throws<NotSupportedException>(() =>
            GameMakerDataWinFormat.Instance.Apply(fixture.Bytes, new Dictionary<string, string>
            {
                [startLocator] = "A much longer replacement string that forces STRG expansion beyond original length"
            }));
        Assert.Contains("UNKN", ex.Message, StringComparison.Ordinal);

        // In-place edit (delta == 0) does NOT shift downstream chunks and still succeeds safely.
        byte[] inPlace = GameMakerDataWinFormat.Instance.Apply(fixture.Bytes, new Dictionary<string, string>
        {
            [startLocator] = "Play"
        });
        Assert.Equal(fixture.Bytes.Length, inPlace.Length);
        Assert.Equal("Play", GameMakerDataWinFormat.Instance.Extract(inPlace).Single(s => s.Locator == startLocator).Source);
    }

    [Fact]
    public void DeploymentAdvisorAndEnginesProfileRecognizeGameMakerDataWinStrategy()
    {
        IEngineProfile gmProfile = Engines.All.Single(p => p.Name == "gamemaker");
        Assert.Contains("data.win", gmProfile.Deployment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STRG", gmProfile.Deployment, StringComparison.Ordinal);

        var cap = new SourceCapability("gamemaker", "", true, true, GameLaunch.Steam, gmProfile.Deployment, "");
        var dataWinFile = new FileQualitySummary(
            "data.win", "gamemaker-datawin", 120, 120, 0, 0, 120, 0, 0.0,
            FileQualityVerdict.Translatable, "Human", ["Start Game"], Included: true, IncludedUnits: 120);

        DeploymentAdvice advice = DeploymentAdvisor.Analyze(cap, [dataWinFile]);
        Assert.Equal("gamemaker-datawin", advice.Strategy);
        Assert.Contains(advice.Notes, n => n.Contains("STRG", StringComparison.Ordinal) && n.Contains("SHA-256", StringComparison.Ordinal));
        Assert.Contains(advice.Notes, n => n.Contains("UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanTranslateBuildInstallInPlaceAndRestoreRoundTripsDataWinWithShaBackup()
    {
        using var ws = new TempWorkspace();
        string gameDir = ws.PathOf("gm-game");
        Directory.CreateDirectory(gameDir);
        SyntheticDataWin fixture = BuildSyntheticDataWin();
        string dataWinPath = Path.Combine(gameDir, "data.win");
        File.WriteAllBytes(dataWinPath, fixture.Bytes);
        string originalSha = PackageInstaller.FileHash(dataWinPath);

        using var store = ProjectStore.Create(ws.PathOf("gm-proj"), "GameMaker E2E", gameDir);
        var service = new ProjectService(store);

        ScanResult scan = service.Scan();
        Assert.Equal(1, scan.Files);
        Assert.Equal(4, scan.Units);
        Assert.Equal("gamemaker", new ProjectSettings(store).ScannedEngine);

        const string arabicStart = "ابدأ اللعبة";
        Unit startUnit = store.Units("Start Game").Single();
        Assert.Equal($"strg:{fixture.StringIndices["human_start"]}", startUnit.Locator);
        startUnit.Target = arabicStart;
        store.Save(startUnit, approve: true);

        var plan = service.Preview(final: true);
        Assert.Equal(["data.win"], plan.ChangedFiles);

        BuildResult build = service.Build(ws.PathOf("gm-out"), final: true, reviewed: plan, overlayOnly: true);
        Assert.Equal(1, build.Files);
        Assert.Equal(1, build.Translations);

        InstallResult install = PackageInstaller.InstallInPlace(store, build.Directory, "gamemaker", "e2e");
        Assert.Equal(1, install.Files);

        string installedSha = PackageInstaller.FileHash(dataWinPath);
        Assert.NotEqual(originalSha, installedSha);
        var installedSlots = GameMakerDataWinFormat.ExtractFromFile(dataWinPath);
        Assert.Equal(arabicStart, installedSlots.Single(s => s.Locator == startUnit.Locator).Source);
        Assert.DoesNotContain(installedSlots, s => s.Source == "Start Game");

        var state = PackageInstaller.GetInPlaceStatus(store);
        Assert.NotNull(state);
        Assert.False(state.ModifiedExternally);

        Assert.Equal(1, PackageInstaller.RestoreInPlace(store));
        Assert.Equal(originalSha, PackageInstaller.FileHash(dataWinPath));
        Assert.Equal(fixture.Bytes, File.ReadAllBytes(dataWinPath));
        Assert.Null(PackageInstaller.GetInPlaceStatus(store));
    }

    /// <summary>
    /// Builds a realistic two-pass GameMaker IFF FORM binary with valid absolute file offsets across
    /// GEN8, SPRT, SHDR, FONT, OBJT, ROOM, SOND, CODE, VARI, FUNC, STRG, TXTR (128-byte aligned PNG + external + QOI),
    /// and AUDO (WAV blob), plus an optional trailing unknown chunk for fail-fast testing.
    /// </summary>
    private static SyntheticDataWin BuildSyntheticDataWin(bool appendUnknownChunkAfterStrg = false)
    {
        var stringEntries = new (string Key, string Value)[]
        {
            ("gen8_filename", "chronicles"),
            ("gen8_config", "Default"),
            ("gen8_name", "ChroniclesGame"),
            ("gen8_display", "Chronicles of Dawn"),          // Human-looking DisplayName — MUST still be excluded!
            ("sprt_name", "HeroSprite"),
            ("shdr_name", "IntroShader"),
            ("shdr_vert", "attribute vec3 in_Position;\nvoid main() { gl_Position = gm_Matrices[MATRIX_WORLD_VIEW_PROJECTION] * vec4(in_Position, 1.0); }"),
            ("shdr_frag", "varying vec2 v_vTexcoord;\nvoid main() { gl_FragColor = texture2D(gm_BaseTexture, v_vTexcoord); }"),
            ("font_name", "MainFont"),
            ("font_display", "Noto Sans"),                   // Human-looking font display name — excluded via FONT
            ("objt_name", "PlayerObject"),
            ("room_name", "MainRoom"),
            ("room_caption", "Castle Entrance"),             // Human-looking room caption — excluded via ROOM
            ("sond_name", "ThemeSong"),
            ("sond_type", ".ogg"),
            ("sond_file", "theme_song.ogg"),
            ("code_name", "StepScript"),
            ("vari_name", "health"),
            ("func_name", "draw_dialog"),
            ("tech_gml", "gml_Object_obj_player_Step_0"),
            ("tech_meta", "@@NewGMLObject@@"),
            ("tech_spr", "spr_unused_icon"),
            ("tech_shader", "#version 330\nuniform sampler2D u_tex;\nvoid main() { gl_FragColor = vec4(1.0); }"),
            ("tech_png", "textures/ui.png"),
            ("tech_ini", "settings.ini"),
            ("human_journey", "Press Enter to begin your journey."),
            ("human_start", "Start Game"),
            ("human_welcome", "Welcome to the ancient village, traveler!"),
            ("human_inv", "Inventory Full")
        };

        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < stringEntries.Length; i++)
            indexByKey[stringEntries[i].Key] = i;

        // Pass 1: build fixed-size metadata chunk templates to know exact STRG offset.
        // Every chunk size is fixed; pass 2 writes exact absolute offsets.
        uint[] strDataPtrs = new uint[stringEntries.Length];
        byte[] finalBytes = [];
        int strgHeaderOff = 0;
        int txtrHeaderOff = 0;
        int audoHeaderOff = 0;
        uint origFormSize = 0;
        uint origStrgSize = 0;
        uint origPngOffset = 0;
        uint origQoiOffset = 0;
        uint origWavOffset = 0;

        for (int pass = 0; pass < 2; pass++)
        {
            using var ms = new MemoryStream();
            WriteAscii(ms, "FORM");
            WriteUInt32(ms, 0); // patched at end of pass

            // GEN8 (108 bytes: FileName@+4, Config@+8, Name@+40, DisplayName@+100)
            byte[] gen8 = new byte[108];
            gen8[0] = 0;  // Debug
            gen8[1] = 17; // BytecodeVersion
            WriteUInt32(gen8, 4, strDataPtrs[indexByKey["gen8_filename"]]);
            WriteUInt32(gen8, 8, strDataPtrs[indexByKey["gen8_config"]]);
            WriteUInt32(gen8, 40, strDataPtrs[indexByKey["gen8_name"]]);
            WriteUInt32(gen8, 60, 1280);
            WriteUInt32(gen8, 64, 720);
            WriteUInt32(gen8, 100, strDataPtrs[indexByKey["gen8_display"]]);
            WriteChunk(ms, "GEN8", gen8);

            // SPRT (1 entry: namePtr@+0, width, height)
            WritePointerListChunk(ms, "SPRT",
            [
                BuildWords(strDataPtrs[indexByKey["sprt_name"]], 32, 32)
            ]);

            // SHDR (1 entry: namePtr@+0, type@+4, glslVert@+8, glslFrag@+12)
            WritePointerListChunk(ms, "SHDR",
            [
                BuildWords(strDataPtrs[indexByKey["shdr_name"]], 0, strDataPtrs[indexByKey["shdr_vert"]], strDataPtrs[indexByKey["shdr_frag"]])
            ]);

            // FONT (1 entry: namePtr@+0, displayNamePtr@+4, size@+8)
            WritePointerListChunk(ms, "FONT",
            [
                BuildWords(strDataPtrs[indexByKey["font_name"]], strDataPtrs[indexByKey["font_display"]], 14)
            ]);

            // OBJT (1 entry: namePtr@+0, spriteIndex@+4)
            WritePointerListChunk(ms, "OBJT",
            [
                BuildWords(strDataPtrs[indexByKey["objt_name"]], 0)
            ]);

            // ROOM (1 entry: namePtr@+0, captionPtr@+4, width@+8, height@+12)
            WritePointerListChunk(ms, "ROOM",
            [
                BuildWords(strDataPtrs[indexByKey["room_name"]], strDataPtrs[indexByKey["room_caption"]], 1280, 720)
            ]);

            // SOND (1 entry: namePtr@+0, flags@+4, typePtr@+8, filePtr@+12)
            WritePointerListChunk(ms, "SOND",
            [
                BuildWords(strDataPtrs[indexByKey["sond_name"]], 1, strDataPtrs[indexByKey["sond_type"]], strDataPtrs[indexByKey["sond_file"]])
            ]);

            // CODE (1 entry: namePtr@+0, length@+4, dummy bytecode)
            WritePointerListChunk(ms, "CODE",
            [
                BuildWords(strDataPtrs[indexByKey["code_name"]], 8, 0x11223344u, 0x55667788u)
            ]);

            // VARI (GMS2 header + 1 variable entry with namePtr)
            WriteChunk(ms, "VARI", BuildWords(1, 1, 0, strDataPtrs[indexByKey["vari_name"]], 0, 0, 0, 0));

            // FUNC (1 function entry with namePtr)
            WriteChunk(ms, "FUNC", BuildWords(1, strDataPtrs[indexByKey["func_name"]], 0, 0));

            // STRG
            strgHeaderOff = (int)ms.Position;
            WriteAscii(ms, "STRG");
            long strgSizePos = ms.Position;
            WriteUInt32(ms, 0);
            long strgPayloadStart = ms.Position;
            WriteUInt32(ms, (uint)stringEntries.Length);
            long strgTablePos = ms.Position;
            for (int i = 0; i < stringEntries.Length; i++)
                WriteUInt32(ms, 0);

            uint[] entryOffsets = new uint[stringEntries.Length];
            for (int i = 0; i < stringEntries.Length; i++)
            {
                entryOffsets[i] = (uint)ms.Position;
                strDataPtrs[i] = entryOffsets[i] + 4u;
                byte[] utf8 = Encoding.UTF8.GetBytes(stringEntries[i].Value);
                WriteUInt32(ms, (uint)utf8.Length);
                ms.Write(utf8);
                ms.WriteByte(0x00);
                PadTo(ms, 4);
            }

            // Pad STRG end so TXTR starts cleanly on 4-byte boundary.
            PadTo(ms, 4);
            long strgEnd = ms.Position;
            origStrgSize = (uint)(strgEnd - strgPayloadStart);
            ms.Position = strgSizePos;
            WriteUInt32(ms, origStrgSize);
            ms.Position = strgTablePos;
            for (int i = 0; i < entryOffsets.Length; i++)
                WriteUInt32(ms, entryOffsets[i]);
            ms.Position = strgEnd;

            // TXTR (3 entries: 128-byte aligned PNG, external texture = 0, 128-byte aligned QOI)
            txtrHeaderOff = (int)ms.Position;
            WriteAscii(ms, "TXTR");
            long txtrSizePos = ms.Position;
            WriteUInt32(ms, 0);
            long txtrPayloadStart = ms.Position;
            WriteUInt32(ms, 3u); // count
            long txtrTablePos = ms.Position;
            WriteUInt32(ms, 0);
            WriteUInt32(ms, 0);
            WriteUInt32(ms, 0);

            uint entry0Pos = (uint)ms.Position;
            WriteUInt32(ms, 1u); // scaled
            WriteUInt32(ms, 0u); // generatedMips
            WriteUInt32(ms, (uint)SamplePngBlob.Length); // blockSize
            long pngPtrFieldPos = ms.Position;
            WriteUInt32(ms, 0u); // textureDataOffset (patched below)

            uint entry1Pos = (uint)ms.Position;
            WriteUInt32(ms, 1u);
            WriteUInt32(ms, 0u);
            WriteUInt32(ms, 0u);
            WriteUInt32(ms, 0u); // external texture -> 0

            uint entry2Pos = (uint)ms.Position;
            WriteUInt32(ms, 1u);
            WriteUInt32(ms, 0u);
            WriteUInt32(ms, (uint)SampleQoiBlob.Length);
            long qoiPtrFieldPos = ms.Position;
            WriteUInt32(ms, 0u); // textureDataOffset (patched below)

            PadTo(ms, 128);
            origPngOffset = (uint)ms.Position;
            ms.Write(SamplePngBlob);

            PadTo(ms, 128);
            origQoiOffset = (uint)ms.Position;
            ms.Write(SampleQoiBlob);
            PadTo(ms, 4);

            long txtrEnd = ms.Position;
            ms.Position = txtrSizePos;
            WriteUInt32(ms, (uint)(txtrEnd - txtrPayloadStart));
            ms.Position = txtrTablePos;
            WriteUInt32(ms, entry0Pos);
            WriteUInt32(ms, entry1Pos);
            WriteUInt32(ms, entry2Pos);
            ms.Position = pngPtrFieldPos;
            WriteUInt32(ms, origPngOffset);
            ms.Position = qoiPtrFieldPos;
            WriteUInt32(ms, origQoiOffset);
            ms.Position = txtrEnd;

            // AUDO (1 entry: WAV blob)
            audoHeaderOff = (int)ms.Position;
            WriteAscii(ms, "AUDO");
            long audoSizePos = ms.Position;
            WriteUInt32(ms, 0);
            long audoPayloadStart = ms.Position;
            WriteUInt32(ms, 1u);
            long audoTablePos = ms.Position;
            WriteUInt32(ms, 0u);
            origWavOffset = (uint)ms.Position;
            WriteUInt32(ms, (uint)SampleWavBlob.Length);
            ms.Write(SampleWavBlob);
            PadTo(ms, 4);
            long audoEnd = ms.Position;
            ms.Position = audoSizePos;
            WriteUInt32(ms, (uint)(audoEnd - audoPayloadStart));
            ms.Position = audoTablePos;
            WriteUInt32(ms, origWavOffset);
            ms.Position = audoEnd;

            if (appendUnknownChunkAfterStrg)
                WriteChunk(ms, "UNKN", [0x01, 0x02, 0x03, 0x04]);

            origFormSize = (uint)(ms.Length - 8);
            ms.Position = 4;
            WriteUInt32(ms, origFormSize);
            finalBytes = ms.ToArray();
        }

        return new SyntheticDataWin(
            finalBytes,
            strgHeaderOff,
            origFormSize,
            origStrgSize,
            txtrHeaderOff,
            audoHeaderOff,
            origPngOffset,
            origQoiOffset,
            origWavOffset,
            indexByKey);
    }

    private static void WritePointerListChunk(MemoryStream ms, string name, byte[][] items)
    {
        WriteAscii(ms, name);
        long sizePos = ms.Position;
        WriteUInt32(ms, 0);
        long payloadStart = ms.Position;
        WriteUInt32(ms, (uint)items.Length);
        long tablePos = ms.Position;
        for (int i = 0; i < items.Length; i++)
            WriteUInt32(ms, 0);

        uint[] offsets = new uint[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            offsets[i] = (uint)ms.Position;
            ms.Write(items[i]);
        }

        long end = ms.Position;
        ms.Position = sizePos;
        WriteUInt32(ms, (uint)(end - payloadStart));
        ms.Position = tablePos;
        for (int i = 0; i < offsets.Length; i++)
            WriteUInt32(ms, offsets[i]);
        ms.Position = end;
    }

    private static void WriteChunk(MemoryStream ms, string name, byte[] payload)
    {
        WriteAscii(ms, name);
        WriteUInt32(ms, (uint)payload.Length);
        ms.Write(payload);
    }

    private static byte[] BuildWords(params uint[] words)
    {
        byte[] buf = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4, 4), words[i]);
        return buf;
    }

    private static void PadTo(MemoryStream ms, int alignment)
    {
        int rem = (int)(ms.Position % alignment);
        if (rem == 0) return;
        int pad = alignment - rem;
        for (int i = 0; i < pad; i++)
            ms.WriteByte(0x00);
    }

    private static void WriteAscii(Stream stream, string text) =>
        stream.Write(Encoding.ASCII.GetBytes(text));

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
}
