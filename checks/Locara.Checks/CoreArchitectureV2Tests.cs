using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class CoreArchitectureV2Tests
{
    [Fact]
    public void LocalizationProviderRegistryUnifiesAllFormatsAndDeclarativeMetadata()
    {
        var providers = LocalizationProviderRegistry.All;
        Assert.Equal(Formats.All.Count, providers.Count);

        foreach (var fmt in Formats.All)
        {
            Assert.True(LocalizationProviderRegistry.TryGetByName(fmt.Name, out var provider),
                $"Format '{fmt.Name}' must be registered in LocalizationProviderRegistry.");
            Assert.Equal(fmt.Name, provider.Metadata.Name);
        }

        Assert.True(LocalizationProviderRegistry.TryGetByName("unity-i2", out var i2));
        Assert.True(i2.Metadata.IsDedicatedLocalization);
        Assert.True(i2.Metadata.UsesStreamedFileAccess);
        Assert.False(i2.Metadata.IsConfigurable);

        Assert.True(LocalizationProviderRegistry.TryGetByName("json", out var json));
        Assert.False(json.Metadata.IsDedicatedLocalization);
        Assert.True(json.Metadata.IsOpenDataFormat);
        Assert.True(json.Metadata.IsConfigurable);
        Assert.True(Formats.Configurable("locales/en.json"));
        Assert.False(Formats.Configurable("locales/en.po"));

        Assert.Contains("POT", LocalizationProviderRegistry.GetScanReason("strings/messages.pot", "po") ?? "");
        Assert.Contains("I2", LocalizationProviderRegistry.GetScanReason("Game_Data/resources.assets", "unity-i2") ?? "");
    }

    [Fact]
    public void ArabicTextEngineAndProjectSettingsProvideTypedDecoupledAccess()
    {
        Assert.True(ArabicTextEngine.HasArabic("مرحبا"));
        Assert.False(ArabicTextEngine.HasArabic("Hello 123"));
        string shaped = ArabicTextEngine.WrapAndShape("اضغط [gold]Enter[/gold] للمتابعة", maxVisibleCharsPerLine: 25);
        Assert.Contains("[gold]", shaped);
        Assert.Contains("[/gold]", shaped);
        Assert.Equal("", ArabicTextEngine.ValidateTokens("Score: {0}", "النقاط: {0}"));
        Assert.NotEqual("", ArabicTextEngine.ValidateTokens("Score: {0}", "النقاط"));

        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("typed-game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "ui.json"), """{"play":"Play"}""");
        using var store = ProjectStore.Create(ws.PathOf("typed-proj"), "TypedGame", gameDir);
        var settings = new ProjectSettings(store);

        Assert.Equal("TypedGame", settings.Name);
        Assert.Null(settings.ResolveUnrealAesKeyBytes());

        string hexKey = "0x0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20";
        settings.UnrealAesKey = hexKey;
        byte[]? resolvedHex = settings.ResolveUnrealAesKeyBytes();
        Assert.NotNull(resolvedHex);
        Assert.Equal(32, resolvedHex!.Length);
        Assert.Equal(0x01, resolvedHex[0]);
        Assert.Equal(0x20, resolvedHex[31]);

        string b64Key = Convert.ToBase64String(resolvedHex);
        settings.UnrealAesKey = b64Key;
        Assert.Equal(resolvedHex, settings.ResolveUnrealAesKeyBytes());

        settings.GettextPoTarget = "locale/fr.po";
        Assert.Equal("locale/fr.po", settings.GettextPoTarget);

        var customDisplay = new UnityArabicDisplayOptions("32", 0.90f, false, true);
        settings.SaveUnityDisplayOptions(customDisplay);
        var loadedDisplay = settings.ResolveUnityDisplayOptions();
        Assert.Equal("32", loadedDisplay.WrapMode);
        Assert.Equal(0.90f, loadedDisplay.FontScale);
        Assert.False(loadedDisplay.ShapeArabic);
        Assert.True(loadedDisplay.ContrastBoost);

        settings.RenpySdk = "sdk-placeholder";
        Assert.Equal("sdk-placeholder", settings.RenpySdk);
        settings.AutomaticOutput = true;
        Assert.True(settings.AutomaticOutput);
        settings.SetEditorViewRaw("{\"Search\":\"hello\"}");
        Assert.Equal("{\"Search\":\"hello\"}", settings.GetEditorViewRaw());
    }

    [Fact]
    public void SingleFileLocalizationTextSourceScansTranslatesAndRebuildsPreservingParentFolderContext()
    {
        using var ws = new TempWorkspace();
        string locDir = Directory.CreateDirectory(ws.PathOf("Game/Game_Data/Localisation Files")).FullName;
        string englishTxt = Path.Combine(locDir, "English.txt");
        File.WriteAllText(englishTxt, "1\tStart Expedition\n2\tSave Progress\n3\tExit Game\n", Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("single-txt-proj"), "SingleTxt", englishTxt);
        var svc = new ProjectService(store);

        var caps = svc.Capabilities();
        Assert.True(caps.CanRead);

        var scan = svc.Scan();
        Assert.Equal(1, scan.Files);
        Assert.Equal(3, scan.Units);

        var first = store.Units("Start Expedition").Single();
        first.Target = "ابدأ البعثة";
        store.Save(first, approve: true);

        var preview = svc.Preview(final: true);
        Assert.Single(preview.ChangedFiles);

        var built = svc.Build(ws.PathOf("single-txt-out"), final: true, reviewed: preview);
        Assert.Equal(1, built.Translations);
        string rebuilt = File.ReadAllText(Path.Combine(built.Directory, "resources/English.txt"), Encoding.UTF8);
        Assert.Contains("1\tابدأ البعثة", rebuilt);
        Assert.Contains("2\tSave Progress", rebuilt);
    }

    [Fact]
    public void GenericLooseGameInPlaceDeployUsesOverlayOnlyAndSkipsUntouchedAssets()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("huge-generic-game")).FullName;
        Directory.CreateDirectory(Path.Combine(gameDir, "locales", "en-US"));
        Directory.CreateDirectory(Path.Combine(gameDir, "assets"));
        string locPath = Path.Combine(gameDir, "locales", "en-US", "ui.json");
        string untouchedBin = Path.Combine(gameDir, "assets", "big_textures.bin");
        File.WriteAllText(locPath, """{"play":"Play Game","quit":"Quit"}""", Encoding.UTF8);
        File.WriteAllBytes(untouchedBin, new byte[4096]);

        using var store = ProjectStore.Create(ws.PathOf("overlay-proj"), "HugeGeneric", gameDir);
        var svc = new ProjectService(store);
        Assert.Equal(2, svc.Scan().Units);

        var unit = store.Units("Play Game").Single();
        unit.Target = "ابدأ اللعب";
        store.Save(unit, approve: true);

        var (build, install, _) = PackageInstaller.DeployInPlace(store, ws.PathOf("overlay-out"), final: true);
        Assert.Equal(1, build.Translations);
        Assert.Equal(1, install.Files);
        // Overlay-only build MUST only emit changed localization file, NEVER copy untouched binary assets!
        Assert.True(File.Exists(Path.Combine(build.Directory, "resources/locales/en-US/ui.json")));
        Assert.False(File.Exists(Path.Combine(build.Directory, "resources/assets/big_textures.bin")));
        Assert.Contains("ابدأ اللعب", File.ReadAllText(locPath, Encoding.UTF8));

        int restored = PackageInstaller.RestoreInPlace(store);
        Assert.Equal(1, restored);
        Assert.Contains("Play Game", File.ReadAllText(locPath, Encoding.UTF8));
    }

    [Fact]
    public void PreviewFingerprintInvalidatesWhenUnrealAesKeyChanges()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("fp-game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "ui.json"), """{"play":"Play"}""", Encoding.UTF8);
        using var store = ProjectStore.Create(ws.PathOf("fp-proj"), "FpGame", gameDir);
        var settings = new ProjectSettings(store);
        var svc = new ProjectService(store);
        svc.Scan();

        var unit = store.Units().Single();
        unit.Target = "العب";
        store.Save(unit, approve: true);

        var previewWithoutKey = svc.Preview(final: true);
        settings.UnrealAesKey = "0x" + new string('A', 64);
        var previewWithKey = svc.Preview(final: true);

        Assert.NotEqual(previewWithoutKey.Fingerprint, previewWithKey.Fingerprint);
        Assert.Throws<InvalidOperationException>(() =>
            svc.Build(ws.PathOf("fp-stale-out"), final: true, reviewed: previewWithoutKey));
    }

    [Fact]
    public void SchemaClustererSkipsLazySkippedZeroUnitFilesAndDeploymentAdvisorReflectsCoreV2()
    {
        var files = new List<FileQualitySummary>
        {
            new("locales/en-US/ui.json", "json", 10, 10, 0, 0, 10, 0, 0.0, FileQualityVerdict.Translatable, "Primary", ["Play the game"], true, 10, false),
            new("locales/fr-FR/ui.json", "json", 0, 0, 0, 0, 0, 0, 0.0, FileQualityVerdict.SecondaryLanguage, "Secondary", [], false, 0, false),
            new("vehicles/car/config.json", "json", 0, 0, 0, 0, 0, 0, 0.0, FileQualityVerdict.AuxiliaryData, "Auxiliary", [], false, 0, false)
        };
        var entries = new Dictionary<string, IReadOnlyList<(string Locator, string Source)>>
        {
            ["locales/en-US/ui.json"] = [("/play", "Play the game")]
        };
        var clusters = SchemaClusterer.Build(files, entries);
        Assert.Single(clusters);
        Assert.Equal("locales/en-US/ui.json", clusters[0].FilePaths.Single());

        var ueCaps = new SourceCapability("Unreal Engine", "", false, false, GameLaunch.None, "", "");
        var ueAdvice = DeploymentAdvisor.Analyze(ueCaps, []);
        Assert.Contains(ueAdvice.Notes, n => n.Contains("AES-256", StringComparison.OrdinalIgnoreCase));

        var unityCaps = new SourceCapability("Unity", "", true, true, GameLaunch.None, "", "");
        var unityTxtFile = new FileQualitySummary("Localisation Files/English.txt", "loc-txt", 50, 50, 0, 0, 50, 0, 0.0,
            FileQualityVerdict.Translatable, "Dedicated", ["Start"], true, 50, false);
        var unityAdvice = DeploymentAdvisor.Analyze(unityCaps, [unityTxtFile]);
        Assert.Contains(unityAdvice.Notes, n => n.Contains("TextMeshPro", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CustomEngineProprietaryPackArchiveReportsClearArabicDiagnostic()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("custom-engine-game")).FullName;
        byte[] customPack = new byte[128 * 1024];
        Encoding.ASCII.GetBytes("CUSTPACK").CopyTo(customPack, 0);
        File.WriteAllBytes(Path.Combine(gameDir, "data.pack"), customPack);
        File.WriteAllBytes(Path.Combine(gameDir, "game.exe"), "MZ\0\0"u8.ToArray());

        using var store = ProjectStore.Create(ws.PathOf("custom-engine-proj"), "CustomGame", gameDir);
        var svc = new ProjectService(store);

        var caps = svc.Capabilities();
        Assert.False(caps.CanRead);
        Assert.Contains("data.pack", caps.Deployment, StringComparison.Ordinal);
        Assert.Contains("محركاً خاصاً", caps.Deployment, StringComparison.Ordinal);

        var scan = svc.Scan();
        Assert.Equal(0, scan.Units);
        Assert.Contains(scan.Diagnostics, d =>
            d.Message.Contains("data.pack", StringComparison.Ordinal)
            && d.Message.Contains("محركاً خاصاً", StringComparison.Ordinal));
    }

    [Fact]
    public void IniFormatSupportsRepeatedKeysLosslesslyWhilePreservingSingleKeyCompatibility()
    {
        var ini = new IniFormat();
        const string unrealStyleIni =
            "[/Script/Engine.Engine]\r\n" +
            "GameTitle=Start Expedition\r\n" +
            "+ActiveRedirects=First Entry\r\n" +
            "+ActiveRedirects=Second Entry\r\n" +
            "+ActiveRedirects=Third Entry\r\n";

        byte[] raw = Encoding.UTF8.GetBytes(unrealStyleIni);
        var slots = ini.Extract(raw);
        Assert.Equal(4, slots.Count);
        Assert.Equal("[/Script/Engine.Engine]/GameTitle", slots[0].Locator);
        Assert.Equal("[/Script/Engine.Engine]/+ActiveRedirects", slots[1].Locator);
        Assert.Equal("[/Script/Engine.Engine]/+ActiveRedirects#2", slots[2].Locator);
        Assert.Equal("[/Script/Engine.Engine]/+ActiveRedirects#3", slots[3].Locator);

        byte[] updated = ini.Apply(raw, new Dictionary<string, string>
        {
            ["[/Script/Engine.Engine]/GameTitle"] = "ابدأ البعثة",
            ["[/Script/Engine.Engine]/+ActiveRedirects#2"] = "المدخل الثاني"
        });
        string updatedText = Encoding.UTF8.GetString(updated);
        Assert.Contains("GameTitle=ابدأ البعثة\r\n", updatedText);
        Assert.Contains("+ActiveRedirects=First Entry\r\n", updatedText);
        Assert.Contains("+ActiveRedirects=المدخل الثاني\r\n", updatedText);
        Assert.Contains("+ActiveRedirects=Third Entry\r\n", updatedText);
    }

    [Fact]
    public void UnrealAes256EncryptedPakExtractsTranslatesAndBuildsBothPatchPakAndIoStorePatch()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("ue-aes-game")).FullName;
        string paksDir = Directory.CreateDirectory(Path.Combine(gameDir, "StarGame", "Content", "Paks")).FullName;

        byte[] aesKey = Convert.FromHexString("A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF");
        string hexKey = "0x" + Convert.ToHexString(aesKey);
        string wrongHexKey = "0x" + new string('1', 64);

        byte[] dummyLatinTtf = [0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] ufontPayload = UnrealPakFormat.WrapUe5Ufont(dummyLatinTtf);
        byte[] englishLocres = UnrealLocresFormat.Write(new UnrealLocresFormat.LocresDocument(3, [
            new UnrealLocresFormat.LocresNamespace(10u, "UI", [
                new UnrealLocresFormat.LocresEntry(20u, "Play", 30u, "Begin Journey"),
                new UnrealLocresFormat.LocresEntry(21u, "Exit", 31u, "Exit to Menu")
            ])
        ]));

        string pakPath = Path.Combine(paksDir, "pakchunk0-WindowsNoEditor.pak");
        WriteAesEncryptedV11Pak(pakPath, "../../../", [
            ("StarGame/Content/Localization/Game/en/Game.locres", englishLocres),
            ("StarGame/Content/UI/Fonts/MainFont.ufont", ufontPayload)
        ], aesKey);

        using var store = ProjectStore.Create(ws.PathOf("ue-aes-proj"), "UeAesGame", gameDir);
        var settings = new ProjectSettings(store);
        var svc = new ProjectService(store);

        // 1. Without key -> 0 units, clear Arabic AES diagnostic
        var noKeyScan = svc.Scan();
        Assert.Equal(0, noKeyScan.Units);
        Assert.Contains(noKeyScan.Diagnostics, d => d.Message.Contains("AES", StringComparison.OrdinalIgnoreCase));

        // 2. With wrong key -> 0 units, clear Arabic mismatch diagnostic
        settings.UnrealAesKey = wrongHexKey;
        var wrongKeyScan = svc.Scan();
        Assert.Equal(0, wrongKeyScan.Units);
        Assert.Contains(wrongKeyScan.Diagnostics, d => d.Message.Contains("غير مطابق", StringComparison.OrdinalIgnoreCase));

        // 3. With valid AES-256 key -> extracts both English strings cleanly!
        settings.UnrealAesKey = hexKey;
        var okScan = svc.Scan();
        Assert.Equal(1, okScan.Files);
        Assert.Equal(2, okScan.Units);

        var playUnit = store.Units("Begin Journey").Single();
        playUnit.Target = "ابدأ الرحلة";
        store.Save(playUnit, approve: true);

        // 4. Build standalone *_Locara_P.pak patch archive from encrypted source pak
        var builtPatch = svc.Build(ws.PathOf("ue-aes-patch-out"), final: true);
        Assert.Equal(1, builtPatch.Translations);
        string generatedPatchPak = Path.Combine(builtPatch.Directory, "resources/StarGame/Content/Paks/pakchunk0-WindowsNoEditor_Locara_P.pak");
        Assert.True(File.Exists(generatedPatchPak));
        var patchSlots = UnrealPakFormat.ExtractFromFile(generatedPatchPak);
        Assert.Contains(patchSlots, s => s.Source == "ابدأ الرحلة");

        // 5. Also verify UE5 IoStore mode (companion .utoc) stream-patches & re-encrypts index cleanly!
        File.WriteAllBytes(Path.Combine(paksDir, "pakchunk0-WindowsNoEditor.utoc"), "-==--==--==--==-"u8.ToArray());
        var ioBuilt = svc.Build(ws.PathOf("ue-aes-iostore-out"), final: true);
        Assert.Equal(1, ioBuilt.Translations);
        string patchedIoPak = Path.Combine(ioBuilt.Directory, "resources/StarGame/Content/Paks/pakchunk0-WindowsNoEditor.pak");
        Assert.True(File.Exists(patchedIoPak));
        // Patched pak remains AES-index-encrypted and reads back the Arabic string with the game's AES key
        Assert.Throws<NotSupportedException>(() => UnrealPakCodec.ReadIndex(patchedIoPak));
        var ioSlots = UnrealPakFormat.ExtractFromFile(patchedIoPak, aesKey: aesKey);
        Assert.Contains(ioSlots, s => s.Source == "ابدأ الرحلة");
    }

    private static void WriteAesEncryptedV11Pak(
        string destinationPath,
        string mountPoint,
        IReadOnlyList<(string Path, byte[] Bytes)> entries,
        byte[] aesKey)
    {
        const ulong pathHashSeed = 0x9B56FB58ul;
        using var fs = File.Open(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var written = new List<(string Path, long Offset, long Size, byte[] Sha1)>();
        Span<byte> head = stackalloc byte[53];
        foreach (var (relPath, plainBytes) in entries)
        {
            string rel = relPath.Replace('\\', '/').TrimStart('/');
            byte[] sha1 = SHA1.HashData(plainBytes);
            long entryOffset = fs.Position;

            // Inline FPakEntry header (53 bytes, unencrypted; flags byte = 1 for IsEncrypted)
            head.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(head[0..], 0L);
            BinaryPrimitives.WriteInt64LittleEndian(head[8..], plainBytes.LongLength);
            BinaryPrimitives.WriteInt64LittleEndian(head[16..], plainBytes.LongLength);
            BinaryPrimitives.WriteUInt32LittleEndian(head[24..], 0u);
            sha1.CopyTo(head[28..48]);
            head[48] = 1; // IsEncrypted = true
            BinaryPrimitives.WriteUInt32LittleEndian(head[49..], 0u);
            fs.Write(head);

            byte[] encPayload = UnrealPakCodec.EncryptAesEcbAligned(plainBytes, aesKey);
            fs.Write(encPayload);
            written.Add((rel, entryOffset, plainBytes.LongLength, sha1));
        }

        // Build EncodedPakEntries bitfields (with bit 22 = IsEncrypted set!)
        using var encEntriesMs = new MemoryStream();
        var encodedOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Span<byte> rec = stackalloc byte[12];
        foreach (var item in written)
        {
            int encOff = (int)encEntriesMs.Position;
            uint flags = (1u << 31) | (1u << 30) | (1u << 29) | (1u << 22); // offset32 | uncomp32 | size32 | isEncrypted
            BinaryPrimitives.WriteUInt32LittleEndian(rec[0..], flags);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[4..], (uint)item.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[8..], (uint)item.Size);
            encEntriesMs.Write(rec);
            encodedOffsets[item.Path] = encOff;
        }
        byte[] encodedEntriesBytes = encEntriesMs.ToArray();

        // Build FullDirectoryIndex plain -> encrypt aligned
        var byDir = written.GroupBy(w =>
        {
            int slash = w.Path.LastIndexOf('/');
            return slash < 0 ? "/" : w.Path[..(slash + 1)];
        }, StringComparer.OrdinalIgnoreCase).ToList();

        byte[] fullDirPlain;
        using (var fdMs = new MemoryStream())
        {
            Span<byte> i32 = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(i32, byDir.Count);
            fdMs.Write(i32);
            foreach (var grp in byDir)
            {
                UnrealLocresFormat.WriteFString(fdMs, grp.Key);
                var items = grp.ToList();
                BinaryPrimitives.WriteInt32LittleEndian(i32, items.Count);
                fdMs.Write(i32);
                foreach (var it in items)
                {
                    int slash = it.Path.LastIndexOf('/');
                    string fileName = slash < 0 ? it.Path : it.Path[(slash + 1)..];
                    UnrealLocresFormat.WriteFString(fdMs, fileName);
                    BinaryPrimitives.WriteInt32LittleEndian(i32, encodedOffsets[it.Path]);
                    fdMs.Write(i32);
                }
            }
            fullDirPlain = fdMs.ToArray();
        }
        byte[] fullDirEncrypted = UnrealPakCodec.EncryptAesEcbAligned(fullDirPlain, aesKey);

        byte[] BuildPrimaryPlain(long fdOff)
        {
            using var pMs = new MemoryStream();
            Span<byte> buf8 = stackalloc byte[8];
            UnrealLocresFormat.WriteFString(pMs, mountPoint);
            BinaryPrimitives.WriteInt32LittleEndian(buf8[..4], written.Count); pMs.Write(buf8[..4]);
            BinaryPrimitives.WriteUInt64LittleEndian(buf8, pathHashSeed); pMs.Write(buf8);
            BinaryPrimitives.WriteInt32LittleEndian(buf8[..4], 0); pMs.Write(buf8[..4]); // hasPathHash = 0
            BinaryPrimitives.WriteInt32LittleEndian(buf8[..4], 1); pMs.Write(buf8[..4]); // hasFullDir = 1
            BinaryPrimitives.WriteInt64LittleEndian(buf8, fdOff); pMs.Write(buf8);
            BinaryPrimitives.WriteInt64LittleEndian(buf8, fullDirEncrypted.LongLength); pMs.Write(buf8);
            pMs.Write(SHA1.HashData(fullDirPlain));
            BinaryPrimitives.WriteInt32LittleEndian(buf8[..4], encodedEntriesBytes.Length); pMs.Write(buf8[..4]);
            pMs.Write(encodedEntriesBytes);
            BinaryPrimitives.WriteInt32LittleEndian(buf8[..4], 0); pMs.Write(buf8[..4]); // fallback files count = 0
            return pMs.ToArray();
        }

        long primaryOffset = fs.Position;
        int primaryEncryptedLen = (BuildPrimaryPlain(0).Length + 15) & ~15;
        long fullDirOffset = primaryOffset + primaryEncryptedLen;
        byte[] primaryPlain = BuildPrimaryPlain(fullDirOffset);
        byte[] primaryEncrypted = UnrealPakCodec.EncryptAesEcbAligned(primaryPlain, aesKey);

        fs.Write(primaryEncrypted);
        fs.Write(fullDirEncrypted);

        // 221-byte v11 footer with bEncryptedIndex = 1
        byte[] footer = new byte[221];
        footer[16] = 1; // bEncryptedIndex = true
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(17), UnrealPakCodec.PakMagic);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(21), 11);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(25), primaryOffset);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(33), primaryEncrypted.LongLength);
        SHA1.HashData(primaryPlain).CopyTo(footer.AsSpan(41));
        fs.Write(footer);
    }
}
