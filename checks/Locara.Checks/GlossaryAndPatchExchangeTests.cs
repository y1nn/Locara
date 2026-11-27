using System.Text;
using System.Text.Json;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class GlossaryAndPatchExchangeTests
{
    [Fact]
    public void GlossaryJsonExportAndImportRoundTripWorks()
    {
        using var ws = new TempWorkspace();
        string jsonFile = ws.PathOf("exports", "glossary.json");

        using (var sourceStore = ProjectStore.Create(ws.PathOf("src.locara"), "SourceProj"))
        {
            sourceStore.Glossary("Mana", "مانا");
            sourceStore.Glossary("Health Potion", "جرعة صحة");
            sourceStore.Glossary("Elderbrook", "إلدربروك");

            int exported = GlossaryExchange.ExportJson(sourceStore, jsonFile);
            Assert.Equal(3, exported);
        }

        string rawJson = File.ReadAllText(jsonFile, Encoding.UTF8);
        using (var doc = JsonDocument.Parse(rawJson))
        {
            Assert.Equal("locara-glossary-v1", doc.RootElement.GetProperty("schema").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("exportedAtUtc").GetString()));
            Assert.Equal(3, doc.RootElement.GetProperty("terms").GetArrayLength());
        }

        using var targetStore = ProjectStore.Create(ws.PathOf("dst.locara"), "TargetProj");
        var preview = GlossaryExchange.PreviewImport(targetStore, jsonFile);

        Assert.Equal("json", preview.SourceFormat);
        Assert.Equal(3, preview.NewCount);
        Assert.Equal(0, preview.IdenticalCount);
        Assert.Equal(0, preview.ConflictCount);
        Assert.Equal(0, preview.DuplicateCount);
        Assert.Equal(0, preview.InvalidCount);

        var applied = GlossaryExchange.ApplyImport(targetStore, preview, GlossaryImportMode.AddMissingOnly);
        Assert.Equal(3, applied.Added);
        Assert.Equal(0, applied.Updated);
        Assert.Equal(0, applied.Skipped);

        var loaded = targetStore.Glossary();
        Assert.Equal("مانا", loaded["Mana"]);
        Assert.Equal("جرعة صحة", loaded["Health Potion"]);
        Assert.Equal("إلدربروك", loaded["Elderbrook"]);
    }

    [Fact]
    public void GlossaryCsvExportNeutralizesFormulasAndRoundTrips()
    {
        using var ws = new TempWorkspace();
        string csvFile = ws.PathOf("glossary.csv");

        using (var sourceStore = ProjectStore.Create(ws.PathOf("src.locara"), "FormulaSource"))
        {
            sourceStore.Glossary("=SUM(A1)", "=المجموع(A1)");
            sourceStore.Glossary("+Attack", "+هجوم");
            sourceStore.Glossary("-Defense", "-دفاع");
            sourceStore.Glossary("@Mention", "@إشارة");
            sourceStore.Glossary("'Quoted", "'مقتبس");
            sourceStore.Glossary("Sword, \"Iron\"", "سيف، \"حديد\"");

            int exported = GlossaryExchange.ExportCsv(sourceStore, csvFile);
            Assert.Equal(6, exported);
        }

        byte[] rawBytes = File.ReadAllBytes(csvFile);
        Assert.True(rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF);

        string csvText = Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3);
        Assert.StartsWith("source,target", csvText, StringComparison.Ordinal);
        Assert.Contains("\"'=SUM(A1)\",\"'=المجموع(A1)\"", csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"=SUM(A1)\"", csvText, StringComparison.Ordinal);
        Assert.Contains("\"'+Attack\",\"'+هجوم\"", csvText, StringComparison.Ordinal);
        Assert.Contains("\"'-Defense\",\"'-دفاع\"", csvText, StringComparison.Ordinal);
        Assert.Contains("\"'@Mention\",\"'@إشارة\"", csvText, StringComparison.Ordinal);
        Assert.Contains("\"''Quoted\",\"''مقتبس\"", csvText, StringComparison.Ordinal);

        using var targetStore = ProjectStore.Create(ws.PathOf("dst.locara"), "FormulaTarget");
        var preview = GlossaryExchange.PreviewImport(targetStore, csvFile);
        Assert.Equal("csv", preview.SourceFormat);
        Assert.Equal(6, preview.NewCount);
        Assert.Equal(0, preview.InvalidCount);

        var result = GlossaryExchange.ApplyImport(targetStore, preview, GlossaryImportMode.AddMissingOnly);
        Assert.Equal(6, result.Added);

        var imported = targetStore.Glossary();
        Assert.Equal("=المجموع(A1)", imported["=SUM(A1)"]);
        Assert.Equal("+هجوم", imported["+Attack"]);
        Assert.Equal("-دفاع", imported["-Defense"]);
        Assert.Equal("@إشارة", imported["@Mention"]);
        Assert.Equal("'مقتبس", imported["'Quoted"]);
        Assert.Equal("سيف، \"حديد\"", imported["Sword, \"Iron\""]);
    }

    [Fact]
    public void GlossaryImportDetectsDuplicatesConflictsAndRespectsImportModes()
    {
        using var ws = new TempWorkspace();
        string file = ws.PathOf("mixed-glossary.json");
        File.WriteAllText(file, """
            {
              "schema": "locara-glossary-v1",
              "terms": [
                { "source": "Mana", "target": "مانا" },
                { "source": "health", "target": "صحة محدثة" },
                { "source": "Stamina", "target": "قدرة تحمل" },
                { "source": "stamina", "target": "لياقة" },
                { "source": "", "target": "فارغ" },
                { "source": "Shield", "target": "   " }
              ]
            }
            """, Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("proj.locara"), "GlossaryModes");
        store.Glossary("Mana", "مانا");
        store.Glossary("Health", "صحة قديمة");

        var preview = GlossaryExchange.PreviewImport(store, file);
        Assert.Equal(1, preview.IdenticalCount);
        Assert.Equal(1, preview.ConflictCount);
        Assert.Equal(1, preview.NewCount);
        Assert.Equal(1, preview.DuplicateCount);
        Assert.Equal(2, preview.InvalidCount);

        var addOnly = GlossaryExchange.ApplyImport(store, preview, GlossaryImportMode.AddMissingOnly);
        Assert.Equal(1, addOnly.Added);
        Assert.Equal(0, addOnly.Updated);
        Assert.Equal(5, addOnly.Skipped);

        var afterAddOnly = store.Glossary();
        Assert.Equal("صحة قديمة", afterAddOnly["Health"]);
        Assert.Equal("قدرة تحمل", afterAddOnly["Stamina"]);
        Assert.Equal(3, afterAddOnly.Count);

        var previewAgain = GlossaryExchange.PreviewImport(store, file);
        var overwrite = GlossaryExchange.ApplyImport(store, previewAgain, GlossaryImportMode.OverwriteExisting);
        Assert.Equal(0, overwrite.Added);
        Assert.Equal(1, overwrite.Updated);
        Assert.Equal(5, overwrite.Skipped);

        var afterOverwrite = store.Glossary();
        Assert.Equal(3, afterOverwrite.Count);
        Assert.False(afterOverwrite.ContainsKey("Health"));
        Assert.Equal("صحة محدثة", afterOverwrite["health"]);
    }

    [Fact]
    public void PatchExportIncludesOnlyCleanApprovedActiveUnitsAndZeroLocalPaths()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("GameSrc")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "ui.json"), """
            {
              "approved_ok": "Start Game",
              "translated_only": "Options",
              "pending_line": "Quit",
              "conflict_line": "Damage: {0}",
              "inactive_line": "Legacy Line"
            }
            """, Encoding.UTF8);

        string patchPath = ws.PathOf("patches", "out.locara-patch.json");
        using (var store = ProjectStore.Create(ws.PathOf("proj.locara"), "PatchExportTest", gameDir))
        {
            new ProjectService(store).Scan();
            var units = store.Units(limit: -1).ToDictionary(u => u.Source);

            units["Start Game"].Target = "ابدأ اللعبة";
            units["Options"].Target = "الخيارات";
            units["Damage: {0}"].Target = "الضرر بدون رمز";
            units["Legacy Line"].Target = "سطر قديم";

            store.SavePage([
                (units["Start Game"], Approve: true),
                (units["Options"], Approve: false),
                (units["Damage: {0}"], Approve: false),
                (units["Legacy Line"], Approve: true)
            ]);

            // Force one row to conflict and one approved row to inactive
            store.Execute("UPDATE units SET status='conflict' WHERE id=$id", ("$id", units["Damage: {0}"].Id));
            store.Execute("UPDATE units SET active=0 WHERE id=$id", ("$id", units["Legacy Line"].Id));

            var export = LocaraPatchExchange.ExportApprovedPatch(store, patchPath);
            Assert.Equal(1, export.ExportedCount);
            Assert.Equal(Path.GetFullPath(patchPath), export.FilePath);
        }

        string rawJson = File.ReadAllText(patchPath, Encoding.UTF8);
        Assert.DoesNotContain(ws.Root, rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", rawJson, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        Assert.Equal("locara-patch-v1", root.GetProperty("schema").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("locaraVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("exportedAtUtc").GetString()));

        var entries = root.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        var only = entries[0];
        Assert.Equal("ui.json", only.GetProperty("path").GetString());
        Assert.Equal("Start Game", only.GetProperty("source").GetString());
        Assert.Equal("ابدأ اللعبة", only.GetProperty("target").GetString());
        Assert.Equal(
            Identity.Hash(Encoding.UTF8.GetBytes("Start Game")),
            only.GetProperty("sourceHash").GetString());
    }

    [Fact]
    public void PatchImportRespectsFillEmptyVsUpdateAllAndApprovalStatus()
    {
        using var ws = new TempWorkspace();
        string gameA = Directory.CreateDirectory(ws.PathOf("GameA")).FullName;
        string jsonContent = """
            {
              "k1": "New Game",
              "k2": "Settings",
              "k3": "Continue"
            }
            """;
        File.WriteAllText(Path.Combine(gameA, "ui.json"), jsonContent, Encoding.UTF8);

        string patchPath = ws.PathOf("community.locara-patch.json");
        using (var storeA = ProjectStore.Create(ws.PathOf("projA.locara"), "AuthorProj", gameA))
        {
            new ProjectService(storeA).Scan();
            var unitsA = storeA.Units(limit: -1).ToDictionary(u => u.Source);
            unitsA["New Game"].Target = "لعبة جديدة";
            unitsA["Settings"].Target = "الإعدادات النهائية";
            unitsA["Continue"].Target = "متابعة";
            storeA.SavePage([
                (unitsA["New Game"], Approve: true),
                (unitsA["Settings"], Approve: true),
                (unitsA["Continue"], Approve: true)
            ]);

            var export = LocaraPatchExchange.ExportApprovedPatch(storeA, patchPath);
            Assert.Equal(3, export.ExportedCount);
        }

        // Recipient project: k1 pending, k2 already has older draft, k3 already identical
        string gameB = Directory.CreateDirectory(ws.PathOf("GameB")).FullName;
        File.WriteAllText(Path.Combine(gameB, "ui.json"), jsonContent, Encoding.UTF8);

        using var storeB = ProjectStore.Create(ws.PathOf("projB.locara"), "RecipientProj", gameB);
        new ProjectService(storeB).Scan();
        var unitsB = storeB.Units(limit: -1).ToDictionary(u => u.Source);
        unitsB["Settings"].Target = "إعدادات قديمة";
        unitsB["Continue"].Target = "متابعة";
        storeB.SavePage([
            (unitsB["Settings"], Approve: false),
            (unitsB["Continue"], Approve: false)
        ]);

        // 1) FillEmptyOnly with default approveImported: false -> status == "imported"
        var fillPreview = LocaraPatchExchange.PreviewImport(storeB, patchPath, PatchMergeMode.FillEmptyOnly);
        Assert.Equal(1, fillPreview.ReadyCount);
        Assert.Equal(1, fillPreview.SkippedExistingCount);
        Assert.Equal(1, fillPreview.IdenticalCount);
        Assert.Equal(0, fillPreview.TokenErrorCount);
        Assert.Equal(0, fillPreview.SourceMismatchCount);
        Assert.Equal(0, fillPreview.NotFoundCount);

        var fillResult = LocaraPatchExchange.ApplyImport(storeB, fillPreview, approveImported: false);
        Assert.Equal(1, fillResult.Applied);
        Assert.Equal(2, fillResult.Skipped);
        Assert.False(fillResult.ApprovedDirectly);

        var afterFill = storeB.Units(limit: -1).ToDictionary(u => u.Source);
        Assert.Equal("لعبة جديدة", afterFill["New Game"].Target);
        Assert.Equal("imported", afterFill["New Game"].Status);
        Assert.Equal("", afterFill["New Game"].Warning);
        Assert.Equal("إعدادات قديمة", afterFill["Settings"].Target);
        Assert.Equal("translated", afterFill["Settings"].Status);

        // Rescanning must NOT wipe out patch-imported translation
        new ProjectService(storeB).Scan();
        var afterRescan = storeB.Units(limit: -1).ToDictionary(u => u.Source);
        Assert.Equal("لعبة جديدة", afterRescan["New Game"].Target);
        Assert.Equal("imported", afterRescan["New Game"].Status);

        // 2) UpdateAll with approveImported: true -> updates Settings and marks status == "approved"
        var updatePreview = LocaraPatchExchange.PreviewImport(storeB, patchPath, PatchMergeMode.UpdateAll);
        Assert.Equal(1, updatePreview.ReadyCount);
        Assert.Equal(0, updatePreview.SkippedExistingCount);
        Assert.Equal(2, updatePreview.IdenticalCount);

        var updateResult = LocaraPatchExchange.ApplyImport(storeB, updatePreview, approveImported: true);
        Assert.Equal(1, updateResult.Applied);
        Assert.Equal(2, updateResult.Skipped);
        Assert.True(updateResult.ApprovedDirectly);

        var afterUpdate = storeB.Units(limit: -1).ToDictionary(u => u.Source);
        Assert.Equal("الإعدادات النهائية", afterUpdate["Settings"].Target);
        Assert.Equal("approved", afterUpdate["Settings"].Status);
        Assert.Equal("imported", afterUpdate["New Game"].Status);
    }

    [Fact]
    public void PatchImportRejectsTokenErrorSourceMismatchAndNotFound()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "strings.json"), """
            {
              "good": "Hello {0}!",
              "bad_token": "Score: {0}",
              "changed": "Updated source text"
            }
            """, Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("proj.locara"), "ValidationProj", gameDir);
        new ProjectService(store).Scan();
        var bySource = store.Units(limit: -1).ToDictionary(u => u.Source);

        string patchPath = ws.PathOf("broken.locara-patch.json");
        var payload = new
        {
            schema = "locara-patch-v1",
            locaraVersion = "v1.5.0",
            engine = "generic",
            exportedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            entries = new object[]
            {
                new
                {
                    path = bySource["Hello {0}!"].Path,
                    locator = bySource["Hello {0}!"].Locator,
                    sourceHash = Identity.Hash(Encoding.UTF8.GetBytes("Hello {0}!")),
                    source = "Hello {0}!",
                    target = "مرحباً {0}!"
                },
                new
                {
                    path = bySource["Score: {0}"].Path,
                    locator = bySource["Score: {0}"].Locator,
                    sourceHash = Identity.Hash(Encoding.UTF8.GetBytes("Score: {0}")),
                    source = "Score: {0}",
                    target = "النتيجة بدون متغير"
                },
                new
                {
                    path = bySource["Updated source text"].Path,
                    locator = bySource["Updated source text"].Locator,
                    sourceHash = Identity.Hash(Encoding.UTF8.GetBytes("Original older source")),
                    source = "Original older source",
                    target = "نص قديم"
                },
                new
                {
                    path = "strings.json",
                    locator = "/does_not_exist",
                    sourceHash = Identity.Hash(Encoding.UTF8.GetBytes("Ghost")),
                    source = "Ghost",
                    target = "شبح"
                }
            }
        };
        File.WriteAllText(patchPath, JsonSerializer.Serialize(payload), Encoding.UTF8);

        var preview = LocaraPatchExchange.PreviewImport(store, patchPath, PatchMergeMode.UpdateAll);
        Assert.Equal(1, preview.ReadyCount);
        Assert.Equal(1, preview.TokenErrorCount);
        Assert.Equal(1, preview.SourceMismatchCount);
        Assert.Equal(1, preview.NotFoundCount);

        var tokenEntry = Assert.Single(preview.Entries, e => e.Status == PatchEntryStatus.TokenError);
        Assert.False(tokenEntry.Include);
        Assert.Contains("{0}", tokenEntry.Issue, StringComparison.Ordinal);

        var mismatchEntry = Assert.Single(preview.Entries, e => e.Status == PatchEntryStatus.SourceMismatch);
        Assert.False(mismatchEntry.Include);

        var notFoundEntry = Assert.Single(preview.Entries, e => e.Status == PatchEntryStatus.NotFound);
        Assert.False(notFoundEntry.Include);

        // Even if someone flips Include=true on invalid rows, ApplyImport must still ignore non-Ready entries
        tokenEntry.Include = true;
        mismatchEntry.Include = true;

        var result = LocaraPatchExchange.ApplyImport(store, preview, approveImported: false);
        Assert.Equal(1, result.Applied);
        Assert.Equal(3, result.Skipped);

        var after = store.Units(limit: -1).ToDictionary(u => u.Source);
        Assert.Equal("مرحباً {0}!", after["Hello {0}!"].Target);
        Assert.Equal("imported", after["Hello {0}!"].Status);
        Assert.Equal("", after["Score: {0}"].Target);
        Assert.Equal("pending", after["Score: {0}"].Status);
        Assert.Equal("", after["Updated source text"].Target);
        Assert.Equal("pending", after["Updated source text"].Status);
    }
}
