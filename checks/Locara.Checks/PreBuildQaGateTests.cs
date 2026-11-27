using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class PreBuildQaGateTests
{
    [Fact]
    public void CleanApprovedProjectIsReadyWithZeroErrorsOrWarnings()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "ui.json"),
            """{"play":"Play","score":"Score: {0}","story":"Welcome to Elderbrook, traveler."}""",
            Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("clean.locara"), "CleanGame", gameDir);
        new ProjectService(store).Scan();
        store.Glossary("Elderbrook", "إلدربروك");

        var bySource = store.Units(limit: -1).ToDictionary(u => u.Source);
        bySource["Play"].Target = "العب";
        bySource["Score: {0}"].Target = "النتيجة: {0}";
        bySource["Welcome to Elderbrook, traveler."].Target = "أهلاً بك في إلدربروك يا مسافر.";
        store.SavePage([
            (bySource["Play"], Approve: true),
            (bySource["Score: {0}"], Approve: true),
            (bySource["Welcome to Elderbrook, traveler."], Approve: true)
        ]);

        var report = PreBuildQaGate.Evaluate(store);

        Assert.Equal(QaReadinessLevel.Ready, report.Level);
        Assert.False(report.HasBlockers);
        Assert.False(report.HasWarnings);
        Assert.Equal(3, report.TotalActiveUnits);
        Assert.Equal(3, report.ApprovedUnits);
        Assert.Equal(0, report.DraftUnits);
        Assert.Equal(0, report.TokenErrors.Count);
        Assert.True(report.TokenErrors.IsBlocking);
        Assert.Equal("issues", report.TokenErrors.Key);
        Assert.Equal(0, report.GlossaryViolations.Count);
        Assert.Equal(0, report.UntranslatedUnits.Count);
        Assert.Equal(0, report.LengthWarnings.Count);
        Assert.Equal(0, report.LengthOverflowCount);
        Assert.Empty(report.BlockingScanErrors);
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
    }

    [Fact]
    public void DroppedTokenAndConflictUnitsBlockBuildWithExactUnitIds()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "strings.json"),
            """
            {
              "u_ok": "Settings",
              "u_tok": "HP: {0} / {1}",
              "u_conflict": "Save"
            }
            """,
            Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("blocked.locara"), "BlockedGame", gameDir);
        new ProjectService(store).Scan();

        var bySource = store.Units(limit: -1).ToDictionary(u => u.Source);
        bySource["Settings"].Target = "الإعدادات";
        bySource["HP: {0} / {1}"].Target = "الصحة: {1}"; // Dropped {0}
        bySource["Save"].Target = "حفظ";
        store.SavePage([
            (bySource["Settings"], Approve: true),
            (bySource["HP: {0} / {1}"], Approve: false),
            (bySource["Save"], Approve: true)
        ]);

        // Mark u_conflict as conflict in DB
        store.Execute("UPDATE units SET status='conflict' WHERE id=$id", ("$id", bySource["Save"].Id));

        var report = PreBuildQaGate.Evaluate(store);

        Assert.Equal(QaReadinessLevel.Blocked, report.Level);
        Assert.True(report.HasBlockers);
        Assert.Equal(3, report.TotalActiveUnits);
        Assert.Equal(1, report.ApprovedUnits);
        Assert.Equal(0, report.DraftUnits);
        Assert.Equal(2, report.TokenErrors.Count);
        Assert.Equal("issues", report.TokenErrors.Key);
        Assert.True(report.TokenErrors.IsBlocking);

        var expectedIds = new[] { bySource["HP: {0} / {1}"].Id, bySource["Save"].Id }
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var actualIds = report.TokenErrors.UnitIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public void BlockingScanErrorsMarkReportAsBlocked()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "ui.json"), """{"ok":"Play"}""", Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("scanerr.locara"), "ScanErrGame", gameDir);
        new ProjectService(store).Scan();

        var unit = Assert.Single(store.Units(limit: -1));
        unit.Target = "العب";
        store.Save(unit, approve: true);

        new ProjectSettings(store).WriteScanErrors([new Diagnostic("data/broken.json", "ملف تالف")]);

        var report = PreBuildQaGate.Evaluate(store);

        Assert.Equal(QaReadinessLevel.Blocked, report.Level);
        Assert.True(report.HasBlockers);
        Assert.Equal(0, report.TokenErrors.Count);
        Assert.Single(report.BlockingScanErrors);
        Assert.Contains("data/broken.json", report.BlockingScanErrors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void GlossaryUntranslatedAndLengthWarningsReportWarningsOnlyWithOverflowFirst()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        // Key names deliberately place warning ("a_warn") before overflow ("z_overflow")
        // so ordering by severity (Overflow before Warning) is genuinely verified.
        File.WriteAllText(Path.Combine(gameDir, "ui.json"),
            """
            {
              "a_warn": "Play",
              "b_glossary": "Restore your Mana now.",
              "c_pending": "Exit",
              "d_empty": "Help",
              "e_draft": "Continue journey through the valley.",
              "z_overflow": "Back"
            }
            """,
            Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("warnings.locara"), "WarnGame", gameDir);
        new ProjectService(store).Scan();
        store.Glossary("Mana", "مانا");

        var bySource = store.Units(limit: -1).ToDictionary(u => u.Source);
        // "Play" (4 chars): safeMax=8, warnMax=12 -> 10 chars => Warning
        bySource["Play"].Target = "ابدأ اللعب";
        // "Back" (4 chars): safeMax=8, warnMax=12 -> 21 chars => Overflow
        bySource["Back"].Target = "العودة إلى القائمة السابقة";
        // Glossary violation: missing "مانا"
        bySource["Restore your Mana now."].Target = "استعد طاقتك الآن.";
        // Draft (translated, not approved)
        bySource["Continue journey through the valley."].Target = "واصل الرحلة عبر الوادي.";
        // c_pending stays pending; d_empty stays empty

        store.SavePage([
            (bySource["Play"], Approve: true),
            (bySource["Back"], Approve: true),
            (bySource["Restore your Mana now."], Approve: true),
            (bySource["Continue journey through the valley."], Approve: false)
        ]);

        var report = PreBuildQaGate.Evaluate(store);

        Assert.Equal(QaReadinessLevel.WarningsOnly, report.Level);
        Assert.False(report.HasBlockers);
        Assert.True(report.HasWarnings);
        Assert.Equal(6, report.TotalActiveUnits);
        Assert.Equal(3, report.ApprovedUnits);
        Assert.Equal(1, report.DraftUnits);

        Assert.Equal(0, report.TokenErrors.Count);
        Assert.Equal(1, report.GlossaryViolations.Count);
        Assert.Equal("glossary", report.GlossaryViolations.Key);
        Assert.False(report.GlossaryViolations.IsBlocking);
        Assert.Equal([bySource["Restore your Mana now."].Id], report.GlossaryViolations.UnitIds);
        Assert.Equal(1, report.GlossaryAudit.ViolatingUnitCount);

        Assert.Equal(2, report.UntranslatedUnits.Count);
        Assert.Equal("pending", report.UntranslatedUnits.Key);
        Assert.False(report.UntranslatedUnits.IsBlocking);
        Assert.Contains(bySource["Exit"].Id, report.UntranslatedUnits.UnitIds);
        Assert.Contains(bySource["Help"].Id, report.UntranslatedUnits.UnitIds);

        Assert.Equal(2, report.LengthWarnings.Count);
        Assert.Equal("length", report.LengthWarnings.Key);
        Assert.False(report.LengthWarnings.IsBlocking);
        Assert.Equal(1, report.LengthOverflowCount);

        // Overflow ID ("Back") MUST come before Warning ID ("Play")
        Assert.Equal(bySource["Back"].Id, report.LengthWarnings.UnitIds[0]);
        Assert.Equal(bySource["Play"].Id, report.LengthWarnings.UnitIds[1]);

        // Verify store.UnitsByIds returns the exact matching units
        var matchedLengthUnits = store.UnitsByIds(report.LengthWarnings.UnitIds);
        Assert.Equal(2, matchedLengthUnits.Count);
        Assert.Equal(
            report.LengthWarnings.UnitIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            matchedLengthUnits.Select(u => u.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void InactiveUnitsWithActiveZeroOrTwoAreExcludedFromEvaluation()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "ui.json"),
            """
            {
              "u_active": "Play",
              "u_inactive0": "Score: {0}",
              "u_inactive2": "Restore Mana."
            }
            """,
            Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("inactive.locara"), "InactiveGame", gameDir);
        new ProjectService(store).Scan();
        store.Glossary("Mana", "مانا");

        var bySource = store.Units(limit: -1).ToDictionary(u => u.Source);
        bySource["Play"].Target = "العب";
        bySource["Score: {0}"].Target = "النتيجة بدون رمز"; // Would be a token error if active
        bySource["Restore Mana."].Target = "استعد الطاقة."; // Would be a glossary violation if active
        store.SavePage([
            (bySource["Play"], Approve: true),
            (bySource["Score: {0}"], Approve: false),
            (bySource["Restore Mana."], Approve: true)
        ]);

        store.Execute("UPDATE units SET active=0, status='conflict' WHERE id=$id", ("$id", bySource["Score: {0}"].Id));
        store.Execute("UPDATE units SET active=2 WHERE id=$id", ("$id", bySource["Restore Mana."].Id));

        var report = PreBuildQaGate.Evaluate(store);

        Assert.Equal(QaReadinessLevel.Ready, report.Level);
        Assert.Equal(1, report.TotalActiveUnits);
        Assert.Equal(1, report.ApprovedUnits);
        Assert.Equal(0, report.TokenErrors.Count);
        Assert.Equal(0, report.GlossaryViolations.Count);
        Assert.Equal(0, report.UntranslatedUnits.Count);
        Assert.Equal(0, report.LengthWarnings.Count);
    }

    [Fact]
    public void EmptyProjectWithZeroActiveUnitsReturnsWarningsOnlyWithArabicSummary()
    {
        using var ws = new TempWorkspace();
        using var store = ProjectStore.Create(ws.PathOf("empty.locara"), "EmptyGame");

        var report = PreBuildQaGate.Evaluate(store);

        Assert.Equal(QaReadinessLevel.WarningsOnly, report.Level);
        Assert.False(report.HasBlockers);
        Assert.Equal(0, report.TotalActiveUnits);
        Assert.Contains("لا توجد أسطر نشطة", report.Summary, StringComparison.Ordinal);
    }
}
