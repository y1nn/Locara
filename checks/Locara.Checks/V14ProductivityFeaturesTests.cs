using System.IO;
using System.Text;
using System.Text.Json;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class V14ProductivityFeaturesTests
{
    [Fact]
    public void SharedMemoryRecordsApprovedAcrossProjectsAndPrefillsExactMatches()
    {
        using var ws = new TempWorkspace();
        string tmDb = ws.PathOf("shared-tm.db");

        // Project A: scan and approve common UI strings
        string gameA = Directory.CreateDirectory(ws.PathOf("GameA")).FullName;
        File.WriteAllText(Path.Combine(gameA, "ui.json"),
            """{"new_game":"New Game","settings":"Settings","exit":"Exit","story":"A long story line."}""",
            Encoding.UTF8);

        using (var storeA = ProjectStore.Create(ws.PathOf("projA.locara"), "GameA", gameA))
        {
            new ProjectService(storeA).Scan();
            var unitsA = storeA.Units(limit: -1);
            var mapA = unitsA.ToDictionary(u => u.Source, u => u);
            mapA["New Game"].Target = "لعبة جديدة";
            mapA["Settings"].Target = "الإعدادات";
            mapA["Exit"].Target = "خروج";
            mapA["A long story line."].Target = "سطر قصة طويل.";
            storeA.SavePage([
                (mapA["New Game"], Approve: true),
                (mapA["Settings"], Approve: true),
                (mapA["Exit"], Approve: true),
                (mapA["A long story line."], Approve: false) // Draft only -> should NOT sync
            ]);

            using var tm = new SharedTranslationMemory(tmDb);
            int synced = tm.SyncApprovedFromProject(storeA);
            Assert.Equal(3, synced);
            Assert.Equal("لعبة جديدة", tm.TryLookup("New Game"));
            Assert.Null(tm.TryLookup("A long story line."));
        }

        // Project B: prefill 100% exact matches before bots run
        string gameB = Directory.CreateDirectory(ws.PathOf("GameB")).FullName;
        File.WriteAllText(Path.Combine(gameB, "menu.json"),
            """{"btn1":"New Game","btn2":"Settings","btn3":"New Game Plus","btn4":"Continue Journey"}""",
            Encoding.UTF8);

        using (var storeB = ProjectStore.Create(ws.PathOf("projB.locara"), "GameB", gameB))
        {
            new ProjectService(storeB).Scan();
            using var tm = new SharedTranslationMemory(tmDb);
            var result = tm.PrefillProject(storeB, autoApprove: false);

            Assert.Equal(2, result.MatchedUnits);
            Assert.Equal("New Game".Length + "Settings".Length, result.SavedCharacters);
            Assert.Equal(0, result.SkippedTokenMismatches);
            Assert.Equal(0, result.SkippedGlossaryConflicts);
            Assert.False(result.AutoApproved);

            var bySource = storeB.Units(limit: -1).ToDictionary(u => u.Source);
            Assert.Equal("لعبة جديدة", bySource["New Game"].Target);
            Assert.Equal("translated", bySource["New Game"].Status);
            Assert.Equal("الإعدادات", bySource["Settings"].Target);
            Assert.Equal("translated", bySource["Settings"].Status);
            Assert.Equal("pending", bySource["New Game Plus"].Status);
            Assert.Equal("pending", bySource["Continue Journey"].Status);

            var stats = tm.GetStats();
            Assert.Equal(3, stats.TotalEntries);
            Assert.True(stats.TotalReuses >= 5); // 3 initial records + 2 prefill hits
        }
    }

    [Fact]
    public void SharedMemoryRespectsAutoApproveAndSkipsManualGlossaryConflicts()
    {
        using var ws = new TempWorkspace();
        string tmDb = ws.PathOf("shared-tm.db");

        using (var tm = new SharedTranslationMemory(tmDb))
        {
            int recorded = tm.RecordApproved([
                ("Settings", "الإعدادات"),
                ("Back", "رجوع"),
                ("Volume {0}%", "مستوى الصوت {0}%")
            ], engine: "unity");
            Assert.Equal(3, recorded);
        }

        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "ui.json"),
            """{"s":"Settings","b":"Back","v":"Volume {0}%"}""",
            Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("proj.locara"), "Game", gameDir);
        new ProjectService(store).Scan();
        // Project's manual glossary overrides "Settings" -> must NOT be overwritten by Shared TM
        store.Glossary("Settings", "الخيارات");

        using var tmCheck = new SharedTranslationMemory(tmDb);
        var res = tmCheck.PrefillProject(store, autoApprove: true);

        Assert.Equal(2, res.MatchedUnits);
        Assert.Equal(1, res.SkippedGlossaryConflicts);
        Assert.True(res.AutoApproved);

        var bySource = store.Units(limit: -1).ToDictionary(u => u.Source);
        Assert.Equal("pending", bySource["Settings"].Status);
        Assert.Equal("", bySource["Settings"].Target);
        Assert.Equal("approved", bySource["Back"].Status);
        Assert.Equal("رجوع", bySource["Back"].Target);
        Assert.Equal("approved", bySource["Volume {0}%"].Status);
        Assert.Equal("مستوى الصوت {0}%", bySource["Volume {0}%"].Target);
    }

    [Fact]
    public void SharedMemoryRejectsIneligibleEntriesAndSupportsClearAndProjectSetting()
    {
        using var ws = new TempWorkspace();
        string tmDb = ws.PathOf("shared-tm.db");

        using (var tm = new SharedTranslationMemory(tmDb))
        {
            int recorded = tm.RecordApproved([
                ("", "فارغ"),
                ("Start", "Start"), // Untranslated Latin copy -> rejected
                ("Score: {0}", "النتيجة بدون رمز"), // Missing token {0} -> rejected
                (new string('A', 520), "نص طويل جداً"), // Exceeds 500 chars -> rejected
                ("Line 1\nLine 2\nLine 3\nLine 4", "سطر ١\nسطر ٢\nسطر ٣\nسطر ٤"), // >3 lines -> rejected
                ("Resume", "استئناف") // Valid -> accepted
            ]);
            Assert.Equal(1, recorded);
            Assert.Equal("استئناف", tm.TryLookup("Resume"));
            Assert.Null(tm.TryLookup("Start"));
            Assert.Null(tm.TryLookup("Score: {0}"));

            tm.Clear();
            Assert.Equal(0, tm.GetStats().TotalEntries);
            Assert.Null(tm.TryLookup("Resume"));
        }

        using var store = ProjectStore.Create(ws.PathOf("settings.locara"), "SettingsTest");
        var settings = new ProjectSettings(store);
        Assert.True(settings.UseSharedMemoryPrefill);
        settings.UseSharedMemoryPrefill = false;
        Assert.False(settings.UseSharedMemoryPrefill);
        settings.UseSharedMemoryPrefill = true;
        Assert.True(settings.UseSharedMemoryPrefill);
    }

    [Fact]
    public void GlossaryConsistencyCheckerDetectsViolationsAndAvoidsFalsePositives()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "strings.json"),
            """
            {
              "u1": "Restore your Mana.",
              "u2": "Welcome to Elderbrook.",
              "u3": "<color=red>Start</color> the Party!",
              "u4": "Travel to Elderbrook for Mana.",
              "u5": "Mana",
              "u6": "Pending Mana line."
            }
            """,
            Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("glossary.locara"), "GlossaryGame", gameDir);
        new ProjectService(store).Scan();
        store.Glossary("Mana", "مانا");
        store.Glossary("Elderbrook", "إلدربروك");
        store.Glossary("red", "أحمر");
        store.Glossary("Art", "فن");

        var bySource = store.Units(limit: -1).ToDictionary(u => u.Source);
        bySource["Restore your Mana."].Target = "استعد طاقتك."; // Violates Mana
        bySource["Welcome to Elderbrook."].Target = "أهلاً بك في الدربروك المُشرقة."; // Alef variant + diacritic -> VALID!
        bySource["<color=red>Start</color> the Party!"].Target = "<color=red>ابدأ</color> الحفلة!"; // Masked token + non-whole-word Art -> VALID!
        bySource["Travel to Elderbrook for Mana."].Target = "سافر إلى القرية من أجل الطاقة."; // Violates both Elderbrook & Mana
        bySource["Mana"].Target = "طاقة"; // Exact source match violation!

        store.SavePage([
            (bySource["Restore your Mana."], Approve: false),
            (bySource["Welcome to Elderbrook."], Approve: true),
            (bySource["<color=red>Start</color> the Party!"], Approve: true),
            (bySource["Travel to Elderbrook for Mana."], Approve: true),
            (bySource["Mana"], Approve: true)
        ]);

        var report = GlossaryConsistencyChecker.AuditProject(store);
        Assert.Equal(5, report.ScannedUnits); // Pending u6 excluded
        Assert.Equal(4, report.ManualTermCount);
        Assert.Equal(3, report.ViolatingUnitCount);
        Assert.Equal(4, report.TotalTermViolations);
        Assert.Equal(1, report.ExactFixableCount);

        var multi = Assert.Single(report.Items, i => i.Unit.Source == "Travel to Elderbrook for Mana.");
        Assert.Equal(2, multi.Violations.Count);
        Assert.Contains(multi.Violations, v => v.TermSource == "Elderbrook" && !v.IsExactSourceMatch);
        Assert.Contains(multi.Violations, v => v.TermSource == "Mana" && !v.IsExactSourceMatch);

        // Apply exact-match fixes -> fixes "Mana" preserving approved status, leaves sentences untouched
        int fixedCount = GlossaryConsistencyChecker.ApplyExactTermFixes(store, report.Items);
        Assert.Equal(1, fixedCount);

        var manaUnit = store.FindUnit(bySource["Mana"].Id);
        Assert.Equal("مانا", manaUnit.Target);
        Assert.Equal("approved", manaUnit.Status);

        // Re-audit and verify UnitsByIds filtering & pagination
        var afterReport = GlossaryConsistencyChecker.AuditProject(store);
        Assert.Equal(2, afterReport.ViolatingUnitCount);
        var ids = afterReport.Items.Select(i => i.Unit.Id).ToHashSet(StringComparer.Ordinal);
        var filtered = store.UnitsByIds(ids, search: "Elderbrook", offset: 0, limit: 10);
        Assert.Single(filtered);
        Assert.Equal("Travel to Elderbrook for Mana.", filtered[0].Source);
    }

    [Fact]
    public async Task TargetedGlossaryBotReviewUpdatesApprovedViolatingUnits()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "dialogue.json"),
            """{"d1":"Drink a Mana potion.","d2":"Save game"}""",
            Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("retranslate.locara"), "RpgGame", gameDir);
        new ProjectService(store).Scan();
        store.Glossary("Mana", "مانا");

        var bySource = store.Units(limit: -1).ToDictionary(u => u.Source);
        bySource["Drink a Mana potion."].Target = "اشرب جرعة طاقة.";
        bySource["Save game"].Target = "حفظ اللعبة";
        store.SavePage([
            (bySource["Drink a Mana potion."], Approve: true),
            (bySource["Save game"], Approve: true)
        ]);

        var auditBefore = GlossaryConsistencyChecker.AuditProject(store);
        Assert.Equal(1, auditBefore.ViolatingUnitCount);

        var options = new ProviderOptions("openai-compatible", "test-model", "test-key",
            BaseUrl: "http://127.0.0.1:11434/v1", BatchSize: 10, MaxCharacters: 200000, MaxRequests: 50);
        var settings = new TeamSettings(Workers: 1, BatchSize: 10, ReviewDrafts: true, IncludeApproved: true);
        var team = new TranslationTeam(store);
        var plan = team.Capture(auditBefore.Items.Select(i => i.Unit), options, settings);

        int reviewerCalls = 0;
        var progress = await team.Run(plan, options, progress: null, approveReviewed: true, send: (req, _) =>
        {
            using var doc = JsonDocument.Parse(req.Data);
            var entries = doc.RootElement.GetProperty("entries");
            string wireId = entries[0].GetProperty("id").GetString()!;

            if (req.Role == "reviewer")
            {
                reviewerCalls++;
                if (reviewerCalls == 1)
                {
                    return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        [wireId] = new { ok = false, reason = "مخالفة معجم: Mana -> مانا", suggestion = "اشرب جرعة مانا." }
                    }));
                }
                return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [wireId] = new { ok = true, reason = "", suggestion = "" }
                }));
            }

            return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [wireId] = "اشرب جرعة مانا."
            }));
        });

        Assert.Equal(1, progress.Saved);
        var updated = store.FindUnit(bySource["Drink a Mana potion."].Id);
        Assert.Equal("اشرب جرعة مانا.", updated.Target);
        Assert.Equal("approved", updated.Status);

        var auditAfter = GlossaryConsistencyChecker.AuditProject(store);
        Assert.Equal(0, auditAfter.ViolatingUnitCount);
    }

    [Fact]
    public void UiLengthGuardEvaluatesTiersMarkupDiacriticsAndDialogueCorrectly()
    {
        // 1. Very short button (4 chars): Safe -> Warning -> Overflow
        var okBtn = UiLengthGuard.Evaluate("Play", "العب");
        Assert.Equal(UiLengthSeverity.Safe, okBtn.Severity);
        Assert.Equal(4, okBtn.VisibleSourceLength);
        Assert.Equal(4, okBtn.VisibleTargetLength);

        var warnBtn = UiLengthGuard.Evaluate("Play", "ابدأ اللعبة"); // 11 chars vs safeMax 8, warnMax 12
        Assert.Equal(UiLengthSeverity.Warning, warnBtn.Severity);

        var overflowBtn = UiLengthGuard.Evaluate("Play", "انقر هنا لبدء اللعب الآن");
        Assert.Equal(UiLengthSeverity.Overflow, overflowBtn.Severity);

        // 2. Protected markup & placeholders stripped before measuring
        var tagged = UiLengthGuard.Evaluate("<color=#ffcc00>{0}</color> Play", "<color=#ffcc00>{0}</color> العب");
        Assert.Equal(UiLengthSeverity.Safe, tagged.Severity);
        Assert.Equal(4, tagged.VisibleSourceLength);
        Assert.Equal(4, tagged.VisibleTargetLength);

        // 3. Arabic diacritics (zero-width tashkeel) excluded from visible glyph count
        var diacritized = UiLengthGuard.Evaluate("Play", "اَلْعَبْ");
        Assert.Equal(UiLengthSeverity.Safe, diacritized.Severity);
        Assert.Equal(4, diacritized.VisibleTargetLength);

        // 4. Medium UI label (17 chars)
        var medSafe = UiLengthGuard.Evaluate("Graphics Settings", "إعدادات الرسوميات");
        Assert.Equal(UiLengthSeverity.Safe, medSafe.Severity);

        var medOverflow = UiLengthGuard.Evaluate("Graphics Settings", "إعدادات جودة العرض والرسوميات المتقدمة للعبة");
        Assert.Equal(UiLengthSeverity.Overflow, medOverflow.Severity);

        // 5. Long dialogue & multi-line text -> NotApplicable
        var dialogue = UiLengthGuard.Evaluate(
            "Captain Mira walked down the corridor and opened the heavy steel hatch.",
            "سارت القائدة ميرا في الممر الطويل وفتحت البوابة الفولاذية الثقيلة ببطء شديد وهي تراقب المكان.");
        Assert.Equal(UiLengthSeverity.NotApplicable, dialogue.Severity);

        var multiline = UiLengthGuard.Evaluate("Line 1\nLine 2", "سطر أول طويل جداً\nسطر ثانٍ طويل جداً");
        Assert.Equal(UiLengthSeverity.NotApplicable, multiline.Severity);

        // 6. UI context hint extends evaluation up to 55 chars
        string uiTitle = "Open the main tactical inventory configuration"; // 46 chars, 6 words
        Assert.Equal(UiLengthSeverity.NotApplicable, UiLengthGuard.Evaluate(uiTitle, "افتح شاشة إعدادات المخزون التكتيكي الرئيسية").Severity);
        var hinted = UiLengthGuard.Evaluate(uiTitle, "افتح شاشة إعدادات المخزون التكتيكي الرئيسية", context: "menu_header");
        Assert.Equal(UiLengthSeverity.Safe, hinted.Severity);
        Assert.True(hinted.RecommendedMaxLength > 0);
    }
}
