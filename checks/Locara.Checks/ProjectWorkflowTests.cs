using System.Text;
using System.Text.Json;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class ProjectWorkflowTests
{
    private const string SampleInput = "{\"a\":\"Back\",\"b\":\"Back\",\"greeting\":\"Welcome [player_name]\",\"number\":3}";

    [Fact]
    public void IdentityDuplicateEditOutputChangedSourceTokensAndCancellation()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "text.json"), SampleInput);

        using var store = ProjectStore.Create(ws.PathOf("project"), "Acceptance", source);
        var service = new ProjectService(store);
        service.Scan();
        var first = store.Units();
        Assert.Equal(3, first.Count);

        var back = first.Single(u => u.Locator == "/a");
        back.Target = "عودة";
        store.Save(back, true);

        service.Scan();
        Assert.Equal("عودة", store.Units().Single(u => u.Id == back.Id).Target);

        string cancelledOutput = ws.PathOf("cancelled-output");
        string previousScan = store.Get("scan_version");
        var cancelled = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => service.Scan(cancellation: cancelled));
        Assert.Throws<OperationCanceledException>(() => service.Build(cancelledOutput, cancellation: cancelled));
        Assert.Equal(previousScan, store.Get("scan_version"));
        Assert.Equal("approved", store.FindUnit(back.Id).Status);
        Assert.Equal(SampleInput, File.ReadAllText(Path.Combine(source, "text.json")));
        Assert.False(Directory.Exists(cancelledOutput));
        Assert.Empty(Directory.EnumerateDirectories(ws.Root, "cancelled-output.building-*"));

        var built = service.Build(ws.PathOf("preview"));
        Assert.Equal(1, built.Translations);
        Assert.Equal(SampleInput, File.ReadAllText(Path.Combine(source, "text.json")));
        Assert.Contains("عودة", File.ReadAllText(Path.Combine(built.Directory, "resources/text.json")));

        string gameCopy = ws.PathOf("game-copy");
        Directory.CreateDirectory(gameCopy);
        string copiedText = Path.Combine(gameCopy, "text.json");
        File.WriteAllText(copiedText, SampleInput);
        var installation = PackageInstaller.Install(store, built.Directory, gameCopy);
        Assert.Equal(1, installation.Files);
        Assert.Contains("عودة", File.ReadAllText(copiedText));
        Assert.Equal(1, PackageInstaller.Restore(installation.ReceiptDirectory));
        Assert.Equal(SampleInput, File.ReadAllText(copiedText));

        File.WriteAllText(Path.Combine(source, "text.json"), SampleInput.Replace("Back", "Return"));
        service.Scan();
        Assert.Equal("conflict", store.Units().Single(u => u.Id == back.Id).Status);

        File.WriteAllText(Path.Combine(source, "text.json"), "{\"a\":\"Return\"}");
        service.Scan();
        service.Scan();
        Assert.Single(store.Units());

        Assert.NotEmpty(Tokens.Validate("Hello [player_name]", "أهلاً"));
        const string marked = "<b><i>Welcome {0}</i></b>";
        Assert.Empty(Tokens.Validate(marked, "<b><i>{0} أهلاً</i></b>"));
        Assert.NotEmpty(Tokens.Validate(marked, "<b><i>أهلاً {0}</b></i>"));
    }

    [Fact]
    public void PartialRepeatedLockedAndConflictingRestore()
    {
        using var ws = new TempWorkspace();
        string restoreSource = Directory.CreateDirectory(ws.PathOf("restore-source")).FullName;
        string restoreCopy = Directory.CreateDirectory(ws.PathOf("restore-copy")).FullName;
        foreach (string file in new[] { "a.json", "b.json" })
        {
            File.WriteAllText(Path.Combine(restoreSource, file), SampleInput);
            File.Copy(Path.Combine(restoreSource, file), Path.Combine(restoreCopy, file));
        }

        using var store = ProjectStore.Create(ws.PathOf("restore-project"), "Restore", restoreSource);
        var service = new ProjectService(store);
        service.Scan();
        foreach (var unit in store.Units().Where(unit => unit.Locator == "/a"))
        {
            unit.Target = "رجوع";
            store.Save(unit, true);
        }
        var build = service.Build(ws.PathOf("restore-build"), final: true);
        string receipt = PackageInstaller.Install(store, build.Directory, restoreCopy).ReceiptDirectory;
        var records = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(receipt, "receipt.json")))!;
        string first = Path.Combine(restoreCopy, records.Changes[0].Path);
        string second = Path.Combine(restoreCopy, records.Changes[1].Path);
        string translated = File.ReadAllText(first);

        File.WriteAllText(second, "independent edit");
        var conflictError = Assert.Throws<IOException>(() => PackageInstaller.Restore(receipt));
        Assert.Contains(second, conflictError.Message);
        Assert.Equal(translated, File.ReadAllText(first));
        Assert.Equal("independent edit", File.ReadAllText(second));

        File.WriteAllText(second, translated);
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Throws<IOException>(() => PackageInstaller.Restore(receipt));
            Assert.Equal(translated, File.ReadAllText(first));
            Assert.Empty(Directory.EnumerateDirectories(receipt, "restore-*"));
        }

        File.WriteAllText(second, SampleInput);
        Assert.Equal(1, PackageInstaller.Restore(receipt));
        Assert.Equal(0, PackageInstaller.Restore(receipt));
        Assert.Equal(SampleInput, File.ReadAllText(first));
        Assert.Equal(SampleInput, File.ReadAllText(second));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(receipt, "original")).Count());
        Assert.Empty(Directory.EnumerateDirectories(receipt, "restore-*"));
    }

    [Fact]
    public async Task PartialImportsConcurrentEditingTokenValidationAndSourceIsolation()
    {
        using var ws = new TempWorkspace();
        string firstGame = Directory.CreateDirectory(ws.PathOf("game-a")).FullName;
        string secondGame = Directory.CreateDirectory(ws.PathOf("game-b")).FullName;
        const string table = "string_id,source_text,ar\nback,Back,عودة\nreward,%1 earned %2,\n";
        File.WriteAllText(Path.Combine(firstGame, "ui.csv"), table);
        File.WriteAllText(Path.Combine(secondGame, "ui.csv"), table);

        using var store = ProjectStore.Create(ws.PathOf("review"), "Review", firstGame);
        var service = new ProjectService(store);
        service.Scan();
        var back = store.Units().Single(u => u.Locator == "key:back");
        var reward = store.Units().Single(u => u.Locator == "key:reward");
        var runner = new TranslationRunner(store);

        Assert.Equal("عودة", back.Target);
        Assert.Equal("imported", back.Status);
        Assert.Equal(1, runner.Estimate().Units);
        await Assert.ThrowsAsync<ArgumentException>(() => runner.Run(new("openai-compatible", "fixture", "")));

        await runner.Run(new("fixture", "fixture", ""), provider: new FixtureProvider());
        Assert.Equal("imported", store.FindUnit(back.Id).Status);

        reward = store.FindUnit(reward.Id);
        reward.Target = "%1";
        var tokenError = Assert.Throws<InvalidDataException>(() => store.Save(reward, true));
        Assert.Contains("%2", tokenError.Message);

        reward.Target = "%2 ثم %1";
        store.Save(reward, true);

        using (var peer = new ProjectStore(store.DirectoryPath))
        {
            var stale = peer.FindUnit(back.Id);
            back.Target = "رجوع";
            store.Save(back, true);
            reward.Target = "%1 ثم %2";
            stale.Target = "السابق";
            var editConflict = Assert.Throws<EditConflictException>(() => peer.SavePage([(reward, false), (stale, true)]));
            Assert.Equal("رجوع", editConflict.Current?.Target);
            Assert.Equal("%2 ثم %1", store.FindUnit(reward.Id).Target);
        }

        string reordered = "string_id,source_text,ar\nreward,%1 earned %2,\nback,Back,عودة\n";
        File.WriteAllText(Path.Combine(firstGame, "ui.csv"), reordered);
        service.Scan();
        Assert.Equal("approved", store.FindUnit(back.Id).Status);

        File.WriteAllText(Path.Combine(firstGame, "ui.csv"), reordered.Replace("عودة", "إلى الخلف"));
        service.Scan();
        var conflict = store.FindUnit(back.Id);
        Assert.Equal("conflict", conflict.Status);
        Assert.Equal("إلى الخلف", conflict.ImportedTarget);
        Assert.Equal("رجوع", conflict.Target);
        store.Save(conflict, true);

        store.ChangeSource(secondGame);
        service.Scan();
        var second = store.Units().Single(u => u.Locator == "key:back");
        Assert.NotEqual(back.Id, second.Id);
        Assert.Equal("imported", second.Status);

        store.ChangeSource(firstGame);
        service.Scan();
        string moved = ws.PathOf("moved-game");
        Directory.Move(firstGame, moved);
        store.ChangeSource(moved, relocated: true);
        service.Scan();
        Assert.Equal("approved", store.FindUnit(back.Id).Status);
        Assert.Equal(2, service.Build(ws.PathOf("final-review"), final: true).Translations);
    }

    [Fact]
    public async Task PackageSelectionTranslationIsolationCsvPreservationBuildPreviewAndNestedInstall()
    {
        using var ws = new TempWorkspace();
        var resources = new List<Resource>
        {
            new("text.json", Encoding.UTF8.GetBytes(SampleInput)),
            new("assets/untouched.bin", [0, 1, 255, 23])
        };

        string game = ws.PathOf("packed-game");
        Directory.CreateDirectory(Path.Combine(game, "data"));
        var pck = new PckContainer();
        pck.Write(Path.Combine(game, "data/a.pck"), resources, null, default);

        using (var archiveProject = ProjectStore.Create(ws.PathOf("packed-project"), "Packed", game))
        {
            var service = new ProjectService(archiveProject);
            Assert.Equal("data/a.pck", service.DiscoverPackages().Single().Path);
            Assert.Equal("data/a.pck", service.Scan().Package);

            var first = archiveProject.Units().Single(unit => unit.Locator == "/a");
            first.Target = "رجوع";
            archiveProject.Save(first, true);

            File.WriteAllText(Path.Combine(game, "settings.json"), """{"note":"Loose file"}""");
            Assert.Equal("data/a.pck", service.Scan().Package);

            service.SelectPackage("DATA/A.PCK");
            Assert.Equal("data/a.pck", service.Scan().Package);
            Assert.Equal("approved", archiveProject.FindUnit(first.Id).Status);

            pck.Write(Path.Combine(game, "data/b.pck"), resources, null, default);
            service.SelectPackage("data/b.pck");
            service.Scan();
            var second = archiveProject.Units().Single(unit => unit.Locator == "/a");
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal("pending", second.Status);

            var provider = new FixtureProvider();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new TranslationRunner(archiveProject).Run(new("fixture", "fixture", "", BatchSize: 1),
                    new InlineProgress(_ =>
                    {
                        using var other = new ProjectStore(archiveProject.DirectoryPath);
                        var fromOtherConnection = new ProjectService(other);
                        fromOtherConnection.SelectPackage("data/a.pck");
                        fromOtherConnection.Scan();
                    }), provider: provider));
            Assert.Equal(1, provider.Calls);
            Assert.All(archiveProject.Units(), unit => Assert.NotEqual("translated", unit.Status));

            service.SelectPackage("data/b.pck");
            service.Scan();
            Assert.Equal("translated", archiveProject.FindUnit(second.Id).Status);

            service.SelectPackage("data/a.pck");
            service.Scan();
            Assert.Equal("رجوع", archiveProject.FindUnit(first.Id).Target);

            var preview = service.Preview(final: true);
            Assert.Equal("data/a.pck", preview.Package);
            Assert.Equal(1, preview.Resources.Single().Translations);
            Assert.Equal(["data/a.pck"], preview.ChangedFiles);

            var edited = archiveProject.FindUnit(first.Id);
            edited.Target = "عودة";
            archiveProject.Save(edited, true);
            string staleOutput = ws.PathOf("stale-output");
            Assert.Throws<InvalidOperationException>(() => service.Build(staleOutput, final: true, reviewed: preview));
            Assert.False(Directory.Exists(staleOutput));

            preview = service.Preview(final: true);
            string result = service.Build(ws.PathOf("packed-output"), final: true, reviewed: preview).Directory;
            string rebuilt = Path.Combine(result, "archive/data/a.pck");
            Assert.True(File.Exists(rebuilt));
            Assert.False(File.Exists(Path.Combine(result, "archive/data/b.pck")));
            Assert.Equal(resources[1].Bytes, pck.Read(rebuilt, null, default).Single(r => r.Path.EndsWith(".bin")).Bytes);

            string gameCopy = ws.PathOf("packed-copy");
            Directory.CreateDirectory(Path.Combine(gameCopy, "data"));
            string copied = Path.Combine(gameCopy, "data/a.pck");
            File.Copy(Path.Combine(game, "data/a.pck"), copied);
            var install = PackageInstaller.Install(archiveProject, result, gameCopy);
            Assert.Equal(File.ReadAllBytes(rebuilt), File.ReadAllBytes(copied));
            PackageInstaller.Restore(install.ReceiptDirectory);
            Assert.Equal(File.ReadAllBytes(Path.Combine(game, "data/a.pck")), File.ReadAllBytes(copied));
        }

        string custom = Directory.CreateDirectory(ws.PathOf("custom")).FullName;
        File.WriteAllText(Path.Combine(custom, "menu.csv"), "code,phrase,localized\nback,Back,عودة\nexit,Exit,\n");
        File.WriteAllText(Path.Combine(custom, "menu.json"), """{"label":"Welcome","image":"banner.png"}""");
        string mappedProject = ws.PathOf("mapped-project");
        using (var mapped = ProjectStore.Create(mappedProject, "Mapped", custom))
        {
            var service = new ProjectService(mapped);
            Assert.Equal("phrase", service.InspectResource("menu.csv").Tables.Single().Headers[1]);
            service.ConfigureResource("menu.csv", new(Key: "code", Source: "phrase", Target: "localized"));
            service.ConfigureResource("menu.json", new(Fields: ["/label"]));
        }
        using (var mapped = new ProjectStore(mappedProject))
        {
            var service = new ProjectService(mapped);
            Assert.Equal(3, service.Scan().Units);
            Assert.Equal("imported", mapped.Units().Single(unit => unit.Locator == "key:back").Status);
            var exit = mapped.Units().Single(unit => unit.Locator == "key:exit");
            exit.Target = "خروج";
            mapped.Save(exit, true);
            string output = service.Build(ws.PathOf("mapped-output"), final: true).Directory;
            Assert.Contains("exit,Exit,خروج", File.ReadAllText(Path.Combine(output, "resources/menu.csv")));
        }

        string irregular = ws.PathOf("extra.csv");
        File.WriteAllText(irregular, "key,en\nstart,Start,internal\n");
        using (var uneven = ProjectStore.Create(ws.PathOf("extra-project"), "Extra", irregular))
        {
            var service = new ProjectService(uneven);
            Assert.Equal([2], service.InspectResource("extra.csv").Tables.Single().RowsWiderThanHeader);
            service.Scan();
            var start = uneven.Units().Single();
            start.Target = "ابدأ";
            uneven.Save(start, true);
            var preview = service.Preview(final: true);
            string output = service.Build(ws.PathOf("extra-output"), final: true, reviewed: preview).Directory;
            Assert.Contains("start,Start,internal,ابدأ", File.ReadAllText(Path.Combine(output, "resources/extra.csv")));
        }

        string same = ws.PathOf("same.json");
        const string sameJson = "{\"name\":\"Ada\"}";
        File.WriteAllText(same, sameJson);
        using (var unchanged = ProjectStore.Create(ws.PathOf("same-project"), "Same", same))
        {
            var service = new ProjectService(unchanged);
            service.Scan();
            var name = unchanged.Units().Single();
            name.Target = name.Source;
            unchanged.Save(name, true);
            var preview = service.Preview(final: true);
            Assert.Equal(1, preview.Resources.Single().Translations);
            Assert.Empty(preview.ChangedFiles);
            string output = service.Build(ws.PathOf("same-output"), final: true, reviewed: preview).Directory;
            using var install = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "install.json")));
            Assert.Equal(sameJson, File.ReadAllText(Path.Combine(output, "resources/same.json")));
            Assert.Equal(0, install.RootElement.GetProperty("Changes").GetArrayLength());
        }
    }

    [Fact]
    public void SmartInPlaceDeployUpdateRestoreAndExternalUpdateDetection()
    {
        using var ws = new TempWorkspace();

        // 1. Generic loose-file game directory
        string gameDir = Directory.CreateDirectory(ws.PathOf("generic-game")).FullName;
        string textPath = Path.Combine(gameDir, "loc", "ui.json");
        Directory.CreateDirectory(Path.GetDirectoryName(textPath)!);
        File.WriteAllText(textPath, SampleInput);

        using (var store = ProjectStore.Create(ws.PathOf("generic-project"), "GenericGame", gameDir))
        {
            var service = new ProjectService(store);
            service.Scan();
            var back = store.Units().Single(u => u.Locator == "/a");
            back.Target = "عودة";
            store.Save(back, true);

            Assert.Null(PackageInstaller.GetInPlaceStatus(store));

            // First permanent in-place install
            var first = PackageInstaller.DeployInPlace(store, ws.PathOf("out-1"), final: true);
            Assert.False(first.UpdatedExisting);
            Assert.Equal(1, first.Install.Files);
            Assert.Contains("عودة", File.ReadAllText(textPath));

            var active = PackageInstaller.GetInPlaceStatus(store);
            Assert.NotNull(active);
            Assert.False(active!.ModifiedExternally);
            Assert.Equal(1, active.Files);

            // Edit translation and 1-click update (auto-restores originals -> rebuilds -> re-installs)
            back = store.FindUnit(back.Id);
            back.Target = "رجوع";
            store.Save(back, true);
            var updated = PackageInstaller.DeployInPlace(store, ws.PathOf("out-2"), final: true);
            Assert.True(updated.UpdatedExisting);
            Assert.Contains("رجوع", File.ReadAllText(textPath));

            // Simulate an official external game update (e.g. Steam patch)
            string patchedByGame = SampleInput.Replace("Back", "Previous");
            File.WriteAllText(textPath, patchedByGame);
            var extState = PackageInstaller.GetInPlaceStatus(store);
            Assert.NotNull(extState);
            Assert.True(extState!.ModifiedExternally);
            Assert.Throws<InvalidOperationException>(() => PackageInstaller.DeployInPlace(store, ws.PathOf("out-3"), final: true));

            // Accept external update, re-scan, deploy again, then 1-click restore
            PackageInstaller.ClearInPlaceRecord(store);
            Assert.Null(PackageInstaller.GetInPlaceStatus(store));
            File.WriteAllText(textPath, SampleInput);
            service.Scan();
            back = store.Units().Single(u => u.Locator == "/a");
            back.Target = "رجوع";
            store.Save(back, true);
            PackageInstaller.DeployInPlace(store, ws.PathOf("out-4"), final: true);
            Assert.Contains("رجوع", File.ReadAllText(textPath));

            int restored = PackageInstaller.RestoreInPlace(store);
            Assert.Equal(1, restored);
            Assert.Equal(SampleInput, File.ReadAllText(textPath));
            Assert.Null(PackageInstaller.GetInPlaceStatus(store));
        }

        // 2. Archive-based game (.pck) direct in-place install & restore
        string pckGame = Directory.CreateDirectory(ws.PathOf("pck-game")).FullName;
        string pckPath = Path.Combine(pckGame, "data", "game.pck");
        Directory.CreateDirectory(Path.GetDirectoryName(pckPath)!);
        var pck = new PckContainer();
        pck.Write(pckPath, [new("text.json", Encoding.UTF8.GetBytes(SampleInput))], null, default);
        byte[] originalPckBytes = File.ReadAllBytes(pckPath);

        using (var store = ProjectStore.Create(ws.PathOf("pck-project"), "PckGame", pckGame))
        {
            var service = new ProjectService(store);
            service.Scan();
            var back = store.Units().Single(u => u.Locator == "/a");
            back.Target = "خلف";
            store.Save(back, true);

            var deploy = PackageInstaller.DeployInPlace(store, ws.PathOf("pck-out"), final: true);
            Assert.Equal(1, deploy.Install.Files);
            Assert.NotEqual(originalPckBytes, File.ReadAllBytes(pckPath));
            Assert.Contains("خلف", Encoding.UTF8.GetString(pck.Read(pckPath, null, default).Single().Bytes));

            Assert.Equal(1, PackageInstaller.RestoreInPlace(store));
            Assert.Equal(originalPckBytes, File.ReadAllBytes(pckPath));
            Assert.Null(PackageInstaller.GetInPlaceStatus(store));
        }
    }

    [Fact]
    public void TextQualityClassifierDistinguishesHumanGameTextFromTechnicalIdentifiers()
    {
        // Human UI / dialogue strings (including placeholders, short words, Arabic, punctuation)
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyString("Play Game"));
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyString("Welcome [player_name]"));
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyString("Back"));
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyString("OK"));
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyString("Options"));
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyString("ابدأ اللعبة الجديدة"));
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyString("Press {0} to continue."));

        // Technical / non-human identifiers, namespaces, paths, GUIDs, hashes
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("Sirenix.Serialization.UnityIntegration"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("InitializeRuntime"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("Assembly-CSharp.dll"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("Assets/Prefabs/UI/MainMenu.prefab"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("m_CatalogLocations"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("STR_MENU_START_GAME"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("550e8400-e29b-41d4-a716-446655440000"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("#FFAA00"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("https://example.com/telemetry/v1"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyString("localization-string-tables-english"));

        var techReport = TextQualityClassifier.ClassifyFile("config.json",
            ["Sirenix.Serialization", "InitializeRuntime", "Assembly-CSharp.dll", "Assets/Prefabs/Hero.prefab"]);
        Assert.Equal(FileQualityVerdict.Technical, techReport.Verdict);
        Assert.Equal(4, techReport.Samples.Count);

        var mixedReport = TextQualityClassifier.ClassifyFile("mixed.json",
            ["Start New Game", "Quit to Desktop", "Assets/UI/menu.png", "UnityEngine.CoreModule"]);
        Assert.Equal(FileQualityVerdict.Mixed, mixedReport.Verdict);

        var humanReport = TextQualityClassifier.ClassifyFile("dialogue.json",
            ["Start New Game", "Load Save", "Options", "Quit to Desktop"]);
        Assert.Equal(FileQualityVerdict.Translatable, humanReport.Verdict);
    }

    [Fact]
    public void PreTranslationFileGuardAutoExcludesTechnicalFilesPreservesUserChoicesAndNeverResurrectsObsoleteUnits()
    {
        using var ws = new TempWorkspace();
        string game = Directory.CreateDirectory(ws.PathOf("guarded-game")).FullName;
        string dialoguePath = Path.Combine(game, "dialogue.json");
        string techPath = Path.Combine(game, "runtime_meta.json");
        string mixedPath = Path.Combine(game, "mixed.json");

        File.WriteAllText(dialoguePath, """{"start":"Start Game","quit":"Quit to Desktop","old":"Temporary Banner"}""");
        File.WriteAllText(techPath, """{"asm":"Assembly-CSharp.dll","ns":"Sirenix.Serialization","method":"InitializeRuntime","guid":"550e8400-e29b-41d4-a716-446655440000"}""");
        File.WriteAllText(mixedPath, """{"title":"Main Menu","bg":"Assets/Textures/bg_menu.png","sub":"Press Start","shader":"UniversalRenderPipeline"}""");

        using var store = ProjectStore.Create(ws.PathOf("guard-project"), "GuardTest", game);
        var service = new ProjectService(store);

        // 1. Initial scan auto-excludes runtime_meta.json (Technical) AND mixed.json (Mixed) while keeping dialogue.json active
        var scan = service.Scan();
        Assert.Equal(3, scan.Files);
        Assert.Equal(3, scan.Units); // 3 from dialogue; 0 from auto-excluded mixed.json and runtime_meta.json
        Assert.Contains(scan.Diagnostics, d => d.Path == "runtime_meta.json");
        Assert.Contains(scan.Diagnostics, d => d.Path == "mixed.json");

        var files = service.ScannedFiles();
        var dialogue = files.Single(f => f.Path == "dialogue.json");
        var mixed = files.Single(f => f.Path == "mixed.json");
        var tech = files.Single(f => f.Path == "runtime_meta.json");

        Assert.Equal(FileQualityVerdict.Translatable, dialogue.Verdict);
        Assert.True(dialogue.Included);
        Assert.Equal(3, dialogue.IncludedUnits);
        Assert.Equal(3, dialogue.Samples.Count);

        Assert.Equal(FileQualityVerdict.Mixed, mixed.Verdict);
        Assert.False(mixed.Included);
        Assert.Equal(0, mixed.IncludedUnits);

        Assert.Equal(FileQualityVerdict.Technical, tech.Verdict);
        Assert.False(tech.Included);
        Assert.Equal(4, tech.TotalUnits);
        Assert.Equal(4, tech.Samples.Count);

        // Translation estimate must NOT count units from auto-excluded technical or mixed files
        Assert.Equal(3, new TranslationRunner(store).Estimate().Units);

        // 2. Enable mixed.json in human-only mode -> only human strings ("Main Menu", "Press Start") become active
        service.SetFileIncluded("mixed.json", true, humanOnly: true);
        var mixedAfterHumanOnly = service.ScannedFiles().Single(f => f.Path == "mixed.json");
        Assert.True(mixedAfterHumanOnly.Included);
        Assert.True(mixedAfterHumanOnly.HumanOnlyFilter);
        Assert.Equal(2, mixedAfterHumanOnly.IncludedUnits);
        Assert.Equal(5, new TranslationRunner(store).Estimate().Units); // 3 dialogue + 2 human mixed
        var activeMixedLocators = store.Units().Where(u => u.Path == "mixed.json").Select(u => u.Locator).OrderBy(x => x).ToArray();
        Assert.Equal(["/sub", "/title"], activeMixedLocators);

        // Re-scan must preserve human-only mode for mixed.json
        service.Scan();
        Assert.Equal(["/sub", "/title"], store.Units().Where(u => u.Path == "mixed.json").Select(u => u.Locator).OrderBy(x => x).ToArray());

        // 3. Translate dialogue.json, exclude mixed.json, and verify Build leaves excluded files untouched
        var startUnit = store.Units().Single(u => u.Path == "dialogue.json" && u.Locator == "/start");
        startUnit.Target = "ابدأ اللعبة";
        store.Save(startUnit, approve: true);

        service.SetFileIncluded("mixed.json", false);
        Assert.Equal(3, store.ReviewCounts()["total"]);
        Assert.Equal(2, new TranslationRunner(store).Estimate().Units); // only 2 pending left in dialogue.json

        var preview = service.Preview(final: true);
        Assert.Equal(["dialogue.json"], preview.ChangedFiles);
        var built = service.Build(ws.PathOf("guard-out"), final: true, reviewed: preview);
        Assert.Equal(1, built.Translations);
        Assert.Contains("ابدأ اللعبة", File.ReadAllText(Path.Combine(built.Directory, "resources/dialogue.json")));

        // 4. User manually enables runtime_meta.json -> choice persists across re-scans
        service.SetFileIncluded("runtime_meta.json", true);
        Assert.Equal(7, store.ReviewCounts()["total"]);
        service.Scan();
        Assert.True(service.ScannedFiles().Single(f => f.Path == "runtime_meta.json").Included);
        Assert.False(service.ScannedFiles().Single(f => f.Path == "mixed.json").Included);

        // 5. Remove "/old" from dialogue.json, re-scan, toggle dialogue.json off then on -> "/old" must NEVER resurrect
        File.WriteAllText(dialoguePath, """{"start":"Start Game","quit":"Quit to Desktop"}""");
        service.Scan();
        Assert.DoesNotContain(store.Units(), u => u.Locator == "/old");

        service.SetFileIncluded("dialogue.json", false);
        Assert.DoesNotContain(store.Units(), u => u.Path == "dialogue.json");

        service.SetFileIncluded("dialogue.json", true);
        var restoredDialogue = store.Units().Where(u => u.Path == "dialogue.json").ToList();
        Assert.Equal(2, restoredDialogue.Count);
        Assert.DoesNotContain(restoredDialogue, u => u.Locator == "/old");
        Assert.Equal("ابدأ اللعبة", restoredDialogue.Single(u => u.Locator == "/start").Target);
        Assert.Equal("approved", restoredDialogue.Single(u => u.Locator == "/start").Status);
    }

    [Fact]
    public void ExpandedEngineInternalMetadataIgnoresAntiCheatRedistAndRuntimeConfigs()
    {
        Assert.True(Engines.IsEngineInternalMetadata("EasyAntiCheat/Settings.json"));
        Assert.True(Engines.IsEngineInternalMetadata("BattlEye/BELauncher.ini"));
        Assert.True(Engines.IsEngineInternalMetadata("_CommonRedist/DirectX/config.xml"));
        Assert.True(Engines.IsEngineInternalMetadata("Game.exe.config"));
        Assert.True(Engines.IsEngineInternalMetadata("steam_appid.txt"));
        Assert.True(Engines.IsEngineInternalMetadata("MyGame.deps.json"));
        Assert.True(Engines.IsEngineInternalMetadata("MyGame_Data/RuntimeInitializeOnLoads.json"));

        Assert.False(Engines.IsEngineInternalMetadata("Localization/en.json"));
        Assert.False(Engines.IsEngineInternalMetadata("MyGame_Data/StreamingAssets/Localization/en.json"));
        Assert.False(Engines.IsEngineInternalMetadata("data/System.json"));
        Assert.False(Engines.IsEngineInternalMetadata("strings.xml"));
    }

    [Fact]
    public void AdaptiveLocaleDetectionHandlesUnderscoreTokensAndAvoidsFalsePositives()
    {
        // Standard folder, exact filename, dot suffix, and underscore tokens in localization paths
        Assert.Equal("eng", ProjectService.ExtractPathLocale("localization/eng/cards.json"));
        Assert.Equal("ru", ProjectService.ExtractPathLocale("strings/ru.po"));
        Assert.Equal("fr", ProjectService.ExtractPathLocale("ui.fr.json"));
        Assert.Equal("ko", ProjectService.ExtractPathLocale("StreamingAssets/strings/strings_preinstalled_ko_studio.po"));
        Assert.Equal("ru", ProjectService.ExtractPathLocale("StreamingAssets/strings/strings_preinstalled_ru_studio.po"));
        Assert.Equal("zh", ProjectService.ExtractPathLocale("StreamingAssets/strings/strings_preinstalled_zh_studio.po"));
        Assert.Equal("zh_cn", ProjectService.ExtractPathLocale("data/ui_zh_cn.json"));
        Assert.Equal("it", ProjectService.ExtractPathLocale("strings/strings_it.po"));
        Assert.Equal("french", ProjectService.ExtractPathLocale("data/dialogue_french.json"));

        // Must NOT false-positive on common English/tech identifiers outside dedicated localization endings
        Assert.Null(ProjectService.ExtractPathLocale("data/building_id.json"));
        Assert.Null(ProjectService.ExtractPathLocale("worldgen/item_no.yaml"));
        Assert.Null(ProjectService.ExtractPathLocale("worldgen/shark_fin.yaml"));
        Assert.Null(ProjectService.ExtractPathLocale("worldgen/resort_spa.yaml"));
        Assert.Null(ProjectService.ExtractPathLocale("StreamingAssets/strings/strings_template.pot"));
        Assert.True(ProjectService.IsGettextTemplate("StreamingAssets/strings/strings_template.pot"));
        Assert.True(ProjectService.IsPrimaryLocaleSource("StreamingAssets/strings/strings_template.pot"));
    }

    [Fact]
    public void SingleTokenEnumFilesAreFlaggedMixedWhilePureUiButtonsStayTranslatable()
    {
        // Pure single-word UI labels with zero technical tokens remain Translatable
        var uiButtons = TextQualityClassifier.ClassifyFile("buttons.json",
            ["Play", "Options", "Credits", "Quit"]);
        Assert.Equal(FileQualityVerdict.Translatable, uiButtons.Verdict);

        // Single-word enum/data YAML with CamelCase technical tokens and zero sentences is flagged Mixed/Technical
        var worldgen = TextQualityClassifier.ClassifyFile("worldgen/borders.yaml",
            ["Katairite", "Unobtanium", "Granite", "IgneousRock"]);
        Assert.Equal(FileQualityVerdict.Mixed, worldgen.Verdict);

        var codex = TextQualityClassifier.ClassifyFile("codex/PrintingPod.yaml",
            ["STRINGS.CODEX.HEADQUARTERS.TITLE", "Title", "hqbase", "Vertical", "STRINGS.CODEX.HEADQUARTERS.SUBTITLE", "Subtitle"]);
        Assert.NotEqual(FileQualityVerdict.Translatable, codex.Verdict);
    }

    [Fact]
    public void DedicatedLocalizationPriorityExcludesSidecarDataAndSecondaryLocalesWhileKeepingPrimaryTemplate()
    {
        using var ws = new TempWorkspace();
        string game = Directory.CreateDirectory(ws.PathOf("hybrid-game")).FullName;
        Directory.CreateDirectory(Path.Combine(game, "StreamingAssets", "strings"));
        Directory.CreateDirectory(Path.Combine(game, "StreamingAssets", "worldgen"));
        Directory.CreateDirectory(Path.Combine(game, "StreamingAssets", "codex"));
        Directory.CreateDirectory(Path.Combine(game, "StreamingAssets", "localization", "en"));

        var potSb = new System.Text.StringBuilder();
        potSb.AppendLine("msgid \"\"");
        potSb.AppendLine("msgstr \"\"");
        potSb.AppendLine();
        for (int i = 1; i <= 25; i++)
        {
            potSb.AppendLine($"msgctxt \"STRINGS.UI.ENTRY_{i}\"");
            potSb.AppendLine($"msgid \"Colony mission objective number {i} is ready for launch.\"");
            potSb.AppendLine("msgstr \"\"");
            potSb.AppendLine();
        }
        File.WriteAllText(Path.Combine(game, "StreamingAssets", "strings", "strings_template.pot"), potSb.ToString());
        File.WriteAllText(Path.Combine(game, "StreamingAssets", "strings", "strings_preinstalled_ko_studio.po"), potSb.ToString());
        File.WriteAllText(Path.Combine(game, "StreamingAssets", "strings", "strings_preinstalled_ru_studio.po"), potSb.ToString());

        // Sidecar data files outside localization directories
        File.WriteAllText(Path.Combine(game, "StreamingAssets", "worldgen", "biomes.yaml"),
            "name: Frozen Core\nborder: Igneous Granite\nambient: Cold Air\n");
        File.WriteAllText(Path.Combine(game, "StreamingAssets", "codex", "entry.yaml"),
            "title: STRINGS.CODEX.POD.TITLE\nstyle: Title\nlayout: Vertical\nsub: STRINGS.CODEX.POD.SUB\n");

        // Explicit localization directory JSON file should stay Translatable & included alongside .pot
        File.WriteAllText(Path.Combine(game, "StreamingAssets", "localization", "en", "credits.json"),
            """{"lead":"Directed by the Studio Team","music":"Original Soundtrack Composed Live"}""");

        using var store = ProjectStore.Create(ws.PathOf("hybrid-project"), "HybridGame", game);
        var service = new ProjectService(store);
        var scan = service.Scan();

        // Only strings_template.pot (25) + localization/en/credits.json (2) = 27 active units
        Assert.Equal(27, scan.Units);

        var files = service.ScannedFiles();
        var pot = files.Single(f => f.Path.EndsWith("strings_template.pot", StringComparison.OrdinalIgnoreCase));
        var ko = files.Single(f => f.Path.EndsWith("strings_preinstalled_ko_studio.po", StringComparison.OrdinalIgnoreCase));
        var ru = files.Single(f => f.Path.EndsWith("strings_preinstalled_ru_studio.po", StringComparison.OrdinalIgnoreCase));
        var biomes = files.Single(f => f.Path.EndsWith("biomes.yaml", StringComparison.OrdinalIgnoreCase));
        var codex = files.Single(f => f.Path.EndsWith("entry.yaml", StringComparison.OrdinalIgnoreCase));
        var credits = files.Single(f => f.Path.EndsWith("credits.json", StringComparison.OrdinalIgnoreCase));

        Assert.True(pot.Included);
        Assert.Equal(FileQualityVerdict.Translatable, pot.Verdict);

        Assert.False(ko.Included);
        Assert.Equal(FileQualityVerdict.SecondaryLanguage, ko.Verdict);
        Assert.False(ru.Included);
        Assert.Equal(FileQualityVerdict.SecondaryLanguage, ru.Verdict);

        Assert.False(biomes.Included);
        Assert.Equal(FileQualityVerdict.AuxiliaryData, biomes.Verdict);
        Assert.False(codex.Included);
        Assert.True(codex.Verdict is FileQualityVerdict.AuxiliaryData or FileQualityVerdict.Technical);

        Assert.True(credits.Included);
        Assert.Equal(FileQualityVerdict.Translatable, credits.Verdict);
    }
}
