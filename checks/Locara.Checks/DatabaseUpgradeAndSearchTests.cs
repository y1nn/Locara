using System.Text.Json;
using Locara.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Locara.Checks;

public sealed class DatabaseUpgradeAndSearchTests
{
    [Fact]
    public void Fts5SearchMatchesArabicEnglishSubstringsShortQueriesQuotesAndIds()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("search.json");
        File.WriteAllText(source, """
            {
              "STR_NEWGAME": "Start \"Alpha\" Mission",
              "STR_EXIT": "Quit to Desktop",
              "STR_SHORT": "OK"
            }
            """);

        using var store = ProjectStore.Create(ws.PathOf("fts-project"), "FTS", source);
        new ProjectService(store).Scan();
        var newGame = store.Units().Single(u => u.Locator == "/STR_NEWGAME");
        newGame.Target = "بدء مهمة \"ألفا\" الجديدة";
        store.Save(newGame, approve: true);

        // 3+ char English substring (case-insensitive) via FTS5 trigram
        Assert.Single(store.Units("mission"));
        Assert.Single(store.Units("NEWGAME"));
        // 3+ char Arabic substring via FTS5 trigram (updated via AFTER UPDATE trigger)
        Assert.Equal(newGame.Id, store.Units("الجديدة").Single().Id);
        // Embedded double quotes safely escaped in FTS5 phrase query
        Assert.Equal(newGame.Id, store.Units("\"Alpha\"").Single().Id);
        Assert.Equal(newGame.Id, store.Units("\"ألفا\"").Single().Id);
        // Short 2-char fallback via LIKE
        Assert.Single(store.Units("OK"));
        // Exact unit ID match
        Assert.Equal(newGame.Id, store.Units(newGame.Id).Single().Id);
    }

    [Fact]
    public void SchemaV1UpgradesToV3WithFtsAndPreservesApprovedUnits()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("legacy.json");
        File.WriteAllText(source, """{"welcome":"Welcome aboard"}""");
        string projectDir = ws.PathOf("v1-project");
        Directory.CreateDirectory(projectDir);
        string dbPath = Path.Combine(projectDir, "project.db");

        string unitId = Identity.UnitId("legacy.json", "/welcome");
        using (var db = new SqliteConnection($"Data Source={dbPath}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE files(path TEXT PRIMARY KEY,hash TEXT NOT NULL,format TEXT NOT NULL,snapshot BLOB NOT NULL,active INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE units(id TEXT PRIMARY KEY,path TEXT NOT NULL,locator TEXT NOT NULL,source TEXT NOT NULL,target TEXT NOT NULL DEFAULT '',context TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'pending',warning TEXT NOT NULL DEFAULT '',active INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE glossary(source TEXT PRIMARY KEY,target TEXT NOT NULL);
                CREATE TABLE attempts(id INTEGER PRIMARY KEY,unit_id TEXT NOT NULL,provider TEXT NOT NULL,state TEXT NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                INSERT INTO settings VALUES('name','LegacyV1'),('source',$src),('engine','auto'),('scanned_source',$src);
                INSERT INTO units(id,path,locator,source,target,context,status,warning,active)
                VALUES($id,'legacy.json','/welcome','Welcome aboard','أهلاً على متن السفينة','/welcome','approved','',1);
                PRAGMA user_version=1;
                """;
            cmd.Parameters.AddWithValue("$src", source);
            cmd.Parameters.AddWithValue("$id", unitId);
            cmd.ExecuteNonQuery();
        }

        using (var store = new ProjectStore(projectDir))
        {
            Assert.Equal("LegacyV1", store.Info.Name);
            // FTS5 was rebuilt during upgrade so Arabic target is searchable immediately.
            var found = store.Units("السفينة");
            Assert.Single(found);
            Assert.Equal("approved", found[0].Status);
            Assert.Equal("أهلاً على متن السفينة", found[0].Target);

            new ProjectService(store).Scan();
            Assert.Equal("approved", store.FindUnit(unitId).Status);
        }
    }

    [Fact]
    public void SchemaV2UpgradesToV3MigratesLegacyTeamSettingsAndCleansSettingsTable()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("v2.json");
        File.WriteAllText(source, """{"a":"Alpha","b":"Beta"}""");
        string projectDir = ws.PathOf("v2-project");
        Directory.CreateDirectory(projectDir);
        string dbPath = Path.Combine(projectDir, "project.db");

        const string scope = "scope-v2";
        const string runId = "legacyrun1";
        string idA = Identity.UnitId(scope + "\nv2.json", "/a");
        string idB = Identity.UnitId(scope + "\nv2.json", "/b");

        var legacyPlan = new TeamPlan(
            runId, scope, "", "generic", "conn", new TeamSettings(Style: "فصحى", Workers: 1),
            new Dictionary<string, string> { ["supervisor"] = "s", ["translator"] = "t", ["reviewer"] = "r" },
            new Dictionary<string, string>(),
            [
                new TeamBatch(1, "v2.json", [new TeamRow(idA, 0, "Alpha", "/a")]),
                new TeamBatch(2, "v2.json", [new TeamRow(idB, 0, "Beta", "/b")])
            ],
            1);

        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string batch1Work = JsonSerializer.Serialize(new
        {
            stage = "complete",
            inFlight = false,
            error = "",
            saved = new Dictionary<string, long> { [idA] = 1 },
            notes = new[] { new TeamNote(idA, "Alpha", "ألفا", "ألفا بديلة", "ملاحظة قديمة") },
            corrections = 1
        }, jsonOpts);
        string usageJson = JsonSerializer.Serialize(new TranslationUsage(1234, 2), jsonOpts);

        using (var db = new SqliteConnection($"Data Source={dbPath}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE files(path TEXT PRIMARY KEY,hash TEXT NOT NULL,format TEXT NOT NULL,snapshot BLOB NOT NULL,active INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE units(id TEXT PRIMARY KEY,path TEXT NOT NULL,locator TEXT NOT NULL,source TEXT NOT NULL,target TEXT NOT NULL DEFAULT '',context TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'pending',warning TEXT NOT NULL DEFAULT '',active INTEGER NOT NULL DEFAULT 1,revision INTEGER NOT NULL DEFAULT 0,imported TEXT NOT NULL DEFAULT '');
                CREATE TABLE glossary(source TEXT PRIMARY KEY,target TEXT NOT NULL);
                CREATE TABLE attempts(id INTEGER PRIMARY KEY,unit_id TEXT NOT NULL,provider TEXT NOT NULL,state TEXT NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE sources(path TEXT PRIMARY KEY COLLATE NOCASE, scope TEXT NOT NULL UNIQUE);
                INSERT INTO sources VALUES($src, $scope);
                INSERT INTO settings VALUES
                    ('name','LegacyV2'),
                    ('source',$src),
                    ('source_scope',$scope),
                    ('engine','auto'),
                    ('scanned_source',$src),
                    ('scanned_engine','generic'),
                    ('scanned_package',''),
                    ('team-active:' || $scope, $run),
                    ('translation-team:' || $run || ':plan', $plan),
                    ('translation-team:' || $run || ':usage', $usage),
                    ('translation-team:' || $run || ':pause', 'موقوف مؤقتاً'),
                    ('translation-team:' || $run || ':1', $b1),
                    ('translation-team:stale-old:plan', '{}');
                PRAGMA user_version=2;
                """;
            cmd.Parameters.AddWithValue("$src", source);
            cmd.Parameters.AddWithValue("$scope", scope);
            cmd.Parameters.AddWithValue("$run", runId);
            cmd.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(legacyPlan, jsonOpts));
            cmd.Parameters.AddWithValue("$usage", usageJson);
            cmd.Parameters.AddWithValue("$b1", batch1Work);
            cmd.ExecuteNonQuery();
        }

        using (var store = new ProjectStore(projectDir))
        {
            var team = new TranslationTeam(store);
            var migrated = team.Latest;
            Assert.NotNull(migrated);
            Assert.Equal(runId, migrated!.Id);
            Assert.Equal(2, migrated.Batches.Length);

            var summary = team.Summary(migrated);
            Assert.Equal(1, summary.Completed);
            Assert.Equal(2, summary.Total);
            Assert.Equal(1, summary.Saved);
            Assert.Equal(1234, summary.Usage.Characters);
            Assert.Equal(2, summary.Usage.Requests);
            Assert.Equal("موقوف مؤقتاً", summary.PauseReason);

            var notes = team.Notes(migrated);
            Assert.Single(notes);
            Assert.Equal("ملاحظة قديمة", notes[0].Issue);

            var sections = team.Sections(migrated).ToList();
            Assert.Single(sections);
            Assert.Equal(("v2.json", 1, 2), sections[0]);
        }

        using (var db = new SqliteConnection($"Data Source={dbPath}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal(6L, (long)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT COUNT(*) FROM settings WHERE key LIKE 'translation-team:%' OR key LIKE 'team-active:%'";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void ApproveAllValidDraftsApprovesCleanDraftsAndSkipsInvalidOrPending()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("drafts.json");
        File.WriteAllText(source, """{"a":"Hello {0}","b":"Exit","c":"Pending","d":"Broken {0}"}""");

        using var store = ProjectStore.Create(ws.PathOf("approve-all"), "BulkApprove", source);
        new ProjectService(store).Scan();
        var a = store.Units().Single(u => u.Locator == "/a");
        var b = store.Units().Single(u => u.Locator == "/b");
        var d = store.Units().Single(u => u.Locator == "/d");
        a.Target = "مرحباً {0}";
        b.Target = "خروج";
        d.Target = "مفقود"; // missing {0} -> warning!
        store.SavePage([(a, false), (b, false), (d, false)]);

        Assert.Equal(2, store.ApproveAllValidDrafts());
        Assert.Equal("approved", store.FindUnit(a.Id).Status);
        Assert.Equal("approved", store.FindUnit(b.Id).Status);
        Assert.Equal("pending", store.Units().Single(u => u.Locator == "/c").Status);
        Assert.Equal("translated", store.FindUnit(d.Id).Status);
        Assert.NotEmpty(store.FindUnit(d.Id).Warning);
    }

    [Fact]
    public void UsageLedgerImportsLegacyLogOnV4UpgradeAccumulatesAndPricesModels()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("usage.json");
        File.WriteAllText(source, """{"a":"Start"}""");
        string projectDir = ws.PathOf("usage-project");

        using (ProjectStore.Create(projectDir, "UsageTest", source)) { }
        SqliteConnection.ClearAllPools();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(projectDir, "project.db")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE usage_ledger; PRAGMA user_version=4;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        File.WriteAllText(Path.Combine(projectDir, "team-live.log"), """
            [2026-11-20 21:52:56] === START run=abc mode=review-drafts workers=8 batches=2 primary=gemini-3.8-flash-medium ===
            [2026-11-20 21:53:39] W1 batch=#1 role=reviewer target=gemini-3.8-flash-medium rows=30 autoFixed=1 rejected=0 next=apply ms=42000 tok=10000 (in=4000 out=2500 think=3500)
            [2026-11-20 21:53:41] W2 batch=#2 role=reviewer target=ProxyA/gemini-3.8-flash-medium rows=30 autoFixed=0 rejected=0 next=apply ms=30000 tok=6000 (in=3000 out=3000 think=0)
            """);

        using var upgraded = new ProjectStore(projectDir);
        var imported = upgraded.ProjectUsage();
        Assert.Single(imported);
        Assert.Equal("gemini-3.8-flash-medium", imported[0].Model);
        Assert.Equal(2, imported[0].Requests);
        Assert.Equal(7000, imported[0].InputTokens);
        Assert.Equal(5500, imported[0].OutputTokens);
        Assert.Equal(3500, imported[0].ThinkingTokens);
        Assert.Equal(16000, imported[0].TotalTokens);

        // Recording another model accumulates separately without erasing prior entries
        upgraded.RecordUsage("deepseek", "deepseek-chat", new TokenUsage(1000000, 500000, 1500000), 4000, 3);
        var all = upgraded.ProjectUsage();
        Assert.Equal(2, all.Count);

        var dsEntry = all.Single(e => e.Model == "deepseek-chat");
        var dsPrice = ModelPricing.Suggest("deepseek-chat");
        Assert.NotNull(dsPrice);
        Assert.Equal(0.27 + 0.55, ModelPricing.CalculateCost(dsEntry, dsPrice!), precision: 4);

        var gemEntry = all.Single(e => e.Model == "gemini-3.8-flash-medium");
        var gemPrice = ModelPricing.Suggest("gemini-3.8-flash-medium");
        Assert.NotNull(gemPrice);
        // 7,000 input @ $0.30/1M + (5,500 + 3,500) out+think @ $2.50/1M = 0.0021 + 0.0225 = $0.0246
        Assert.Equal(0.0246, ModelPricing.CalculateCost(gemEntry, gemPrice!), precision: 5);
    }
}
