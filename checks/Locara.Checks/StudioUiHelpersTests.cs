using System.IO;
using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class StudioUiHelpersTests
{
    [Fact]
    public void BotGlossaryEntriesPromoteAndRemoveHandleCaseCollisionsAtomically()
    {
        using var ws = new TempWorkspace();
        string projectDir = ws.PathOf("proj.locara");
        using var store = ProjectStore.Create(projectDir, "TestGame");
        store.Glossary("elderbrook", "إلدربروك القديم");

        store.SaveBotTerms(new Dictionary<string, string>
        {
            ["Elderbrook"] = "إلدربروك",
            ["Iron Warden"] = "الحارس الحديدي"
        }, note: "lore-bot", overwrite: true);
        store.SaveBotTerms(new Dictionary<string, string>
        {
            ["Shadow Vale"] = "وادي الظلال"
        }, note: "translator-bot", overwrite: false);

        List<BotGlossaryEntry> entries = store.BotGlossaryEntries();
        Assert.Equal(3, entries.Count);
        Assert.Equal("lore-bot", entries[0].Note);
        Assert.Contains(entries, e => e.Source == "Shadow Vale" && e.Note == "translator-bot");

        // Promote Elderbrook with an override: updates manual glossary case-insensitively without duplicate keys and removes from bot_glossary
        store.PromoteBotTerm("Elderbrook", "مدينة إلدربروك");
        var manual = store.Glossary();
        Assert.Single(manual, kv => kv.Key.Equals("Elderbrook", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("مدينة إلدربروك", manual.First(kv => kv.Key.Equals("Elderbrook", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.DoesNotContain(store.BotGlossaryEntries(), e => e.Source.Equals("Elderbrook", StringComparison.OrdinalIgnoreCase));

        // Promote Iron Warden using its stored bot target
        store.PromoteBotTerm("Iron Warden");
        Assert.Equal("الحارس الحديدي", store.Glossary()["Iron Warden"]);
        Assert.Single(store.BotGlossaryEntries());

        // Remove remaining bot term
        store.RemoveBotTerm("shadow vale");
        Assert.Empty(store.BotGlossaryEntries());
    }

    [Fact]
    public void ReadRecentSummaryExtractsMetadataAndHandlesMissingOrCorruptProjectGracefully()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("GameFolder")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "en.json"), """{"play":"Play","exit":"Exit","title":"Welcome"}""", Encoding.UTF8);

        string projectDir = ws.PathOf("valid.locara");
        using (var store = ProjectStore.Create(projectDir, "My Cool Game", gameDir))
        {
            var svc = new ProjectService(store);
            svc.Scan();
            var units = store.Units(limit: -1);
            Assert.Equal(3, units.Count);
            units[0].Target = "العب";
            units[1].Target = "خروج";
            store.SavePage([(units[0], Approve: true), (units[1], Approve: false)]);
        }

        RecentProjectSummary summary = ProjectStore.ReadRecentSummary(projectDir);
        Assert.True(summary.Exists);
        Assert.Equal("My Cool Game", summary.Name);
        Assert.Equal(gameDir, summary.Source);
        Assert.Equal(3, summary.TotalUnits);
        Assert.Equal(2, summary.CompletedUnits);
        Assert.Equal(1, summary.ApprovedUnits);

        string missingDir = ws.PathOf("deleted.locara");
        RecentProjectSummary missing = ProjectStore.ReadRecentSummary(missingDir);
        Assert.False(missing.Exists);
        Assert.Equal("deleted", missing.Name);
        Assert.Equal(0, missing.TotalUnits);

        string corruptDir = Directory.CreateDirectory(ws.PathOf("corrupt.locara")).FullName;
        File.WriteAllBytes(Path.Combine(corruptDir, "project.db"), [0x01, 0x02, 0x03, 0x04]);
        RecentProjectSummary corrupt = ProjectStore.ReadRecentSummary(corruptDir);
        Assert.False(corrupt.Exists);
    }

    [Fact]
    public void TokensInspectPreservesFirstAppearanceOrderAndCountsRepeatedPlaceholders()
    {
        string source = "<color=#ffcc00>{0}</color> scored {0} of {1} in %s!";
        string partialTarget = "<color=#ffcc00>{0}</color> أحرز نقطة في %s!";

        List<TokenInspection> inspections = Tokens.Inspect(source, partialTarget);
        Assert.Equal(5, inspections.Count);

        Assert.Equal("<color=#ffcc00>", inspections[0].Token);
        Assert.True(inspections[0].IsSatisfied);

        Assert.Equal("{0}", inspections[1].Token);
        Assert.Equal(2, inspections[1].ExpectedCount);
        Assert.Equal(1, inspections[1].PresentCount);
        Assert.False(inspections[1].IsSatisfied);

        Assert.Equal("</color>", inspections[2].Token);
        Assert.True(inspections[2].IsSatisfied);

        Assert.Equal("{1}", inspections[3].Token);
        Assert.Equal(1, inspections[3].ExpectedCount);
        Assert.Equal(0, inspections[3].PresentCount);
        Assert.False(inspections[3].IsSatisfied);

        Assert.Equal("%s", inspections[4].Token);
        Assert.True(inspections[4].IsSatisfied);
    }

    [Fact]
    public async Task TeamEmitsLiveSamplePairsExposesWorldBriefAndRefreshesGlossaryOnResume()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        var sb = new StringBuilder("{");
        for (int i = 1; i <= 12; i++)
        {
            if (i > 1) sb.Append(',');
            sb.Append($"\"line_{i:00}\":\"Captain Mira enters Starforge room {i}.\"");
        }
        sb.Append('}');
        File.WriteAllText(Path.Combine(gameDir, "dialogue.json"), sb.ToString(), Encoding.UTF8);

        string projectDir = ws.PathOf("team.locara");
        using var store = ProjectStore.Create(projectDir, "SpaceGame", gameDir);
        new ProjectService(store).Scan();

        var options = new ProviderOptions("openai-compatible", "test-model", "test-key", BaseUrl: "http://127.0.0.1:11434/v1", BatchSize: 6, MaxCharacters: 500000, MaxRequests: 100);
        var settings = new TeamSettings(Workers: 1, BatchSize: 6, SmartReview: true);
        var team = new TranslationTeam(store);
        var units = store.Units(limit: -1, status: "pending");
        var plan = team.Capture(units, options, settings);

        var observedActivities = new List<AgentActivity>();
        var progress = new Progress<TeamProgress>(p =>
        {
            if (p.Activity != null)
            {
                lock (observedActivities) observedActivities.Add(p.Activity);
            }
        });

        using var cts = new CancellationTokenSource();
        int translateCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await team.Run(plan, options, progress, cts.Token, send: (req, ct) =>
            {
                if (req.Role == "supervisor")
                {
                    return Task.FromResult("""
                        {"worldBrief":"لعبة خيال علمي بقيادة القبطان ميرا داخل محطة ستارفورج.","glossary":{"Starforge":"ستارفورج"},"sections":{}}
                        """);
                }
                if (req.Role == "translator")
                {
                    int call = Interlocked.Increment(ref translateCalls);
                    using var doc = System.Text.Json.JsonDocument.Parse(req.Data);
                    var map = new Dictionary<string, string>();
                    foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                    {
                        string id = entry.GetProperty("id").GetString()!;
                        map[id] = $"تدخل القبطان ميرا غرفة ستارفورج {id}.";
                    }
                    if (call >= 2)
                    {
                        cts.Cancel();
                        ct.ThrowIfCancellationRequested();
                    }
                    return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { translations = map }));
                }
                else
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(req.Data);
                    var rev = new Dictionary<string, object>();
                    foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                    {
                        string id = entry.GetProperty("id").GetString()!;
                        rev[id] = new { ok = true, reason = "", suggestion = "" };
                    }
                    return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(rev));
                }
            }, approveReviewed: true);
        });

        await Task.Delay(50);
        Assert.Contains("القبطان ميرا", team.WorldBrief(plan));
        Assert.Contains(observedActivities, a => a.Stage == "مكتمل"
            && !string.IsNullOrWhiteSpace(a.SampleSource)
            && a.SampleTarget != null && a.SampleTarget.Contains("ستارفورج", StringComparison.Ordinal));

        // While paused, promote/override "Starforge" in manual glossary to "مصهر النجوم" and resume!
        store.PromoteBotTerm("Starforge", "مصهر النجوم");

        string? resumedPayload = null;
        var latestPlan = team.Latest!;
        await team.Run(latestPlan, options, progress: null, send: (req, _) =>
        {
            if (req.Role == "translator")
            {
                resumedPayload = req.Data;
                using var doc = System.Text.Json.JsonDocument.Parse(req.Data);
                var map = new Dictionary<string, string>();
                foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                {
                    string id = entry.GetProperty("id").GetString()!;
                    map[id] = $"تدخل القبطان ميرا غرفة مصهر النجوم {id}.";
                }
                return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { translations = map }));
            }
            else
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.Data);
                var rev = new Dictionary<string, object>();
                foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                {
                    string id = entry.GetProperty("id").GetString()!;
                    rev[id] = new { ok = true, reason = "", suggestion = "" };
                }
                return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(rev));
            }
        }, approveReviewed: true);

        Assert.NotNull(resumedPayload);
        Assert.Contains("مصهر النجوم", resumedPayload!);
    }

    [Fact]
    public void UnitNotifiesPropertyChangedOnTargetStatusAndWarning()
    {
        var unit = new Unit { Id = "id1", Path = "ui.json", Locator = "/play", Source = "Play", Status = "pending" };
        var changedProps = new List<string>();
        unit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        unit.Target = "العب";
        unit.Status = "approved";
        unit.Warning = "تنبيه";

        Assert.Contains(nameof(Unit.Target), changedProps);
        Assert.Contains(nameof(Unit.Status), changedProps);
        Assert.Contains(nameof(Unit.Warning), changedProps);
    }

    [Fact]
    public async Task RemovedBotTermDoesNotReturnFromCachedSupervisorJsonOnResume()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("Game")).FullName;
        var sb = new StringBuilder("{");
        for (int i = 1; i <= 12; i++)
        {
            if (i > 1) sb.Append(',');
            sb.Append($"\"line_{i:00}\":\"Visit Crimson Keep gate {i}.\"");
        }
        sb.Append('}');
        File.WriteAllText(Path.Combine(gameDir, "dialogue.json"), sb.ToString(), Encoding.UTF8);

        string projectDir = ws.PathOf("team-remove.locara");
        using var store = ProjectStore.Create(projectDir, "KeepGame", gameDir);
        new ProjectService(store).Scan();

        var options = new ProviderOptions("openai-compatible", "test-model", "test-key", BaseUrl: "http://127.0.0.1:11434/v1", BatchSize: 6, MaxCharacters: 500000, MaxRequests: 100);
        var settings = new TeamSettings(Workers: 1, BatchSize: 6, SmartReview: true);
        var team = new TranslationTeam(store);
        var plan = team.Capture(store.Units(limit: -1, status: "pending"), options, settings);

        using var cts = new CancellationTokenSource();
        int translateCalls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await team.Run(plan, options, progress: null, cts.Token, send: (req, ct) =>
            {
                if (req.Role == "supervisor")
                {
                    return Task.FromResult("""
                        {"worldBrief":"عالم القلعة.","glossary":{"Crimson Keep":"الحصن القرمزي الخاطئ"},"sections":{}}
                        """);
                }
                if (req.Role == "translator")
                {
                    if (Interlocked.Increment(ref translateCalls) >= 2)
                    {
                        cts.Cancel();
                        ct.ThrowIfCancellationRequested();
                    }
                    using var doc = System.Text.Json.JsonDocument.Parse(req.Data);
                    var map = new Dictionary<string, string>();
                    foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                        map[entry.GetProperty("id").GetString()!] = "زر البوابة.";
                    return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { translations = map }));
                }
                using var rdoc = System.Text.Json.JsonDocument.Parse(req.Data);
                var rev = new Dictionary<string, object>();
                foreach (var entry in rdoc.RootElement.GetProperty("entries").EnumerateArray())
                    rev[entry.GetProperty("id").GetString()!] = new { ok = true, reason = "", suggestion = "" };
                return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(rev));
            }, approveReviewed: true);
        });

        // User deletes the bad bot-inferred term while paused
        store.RemoveBotTerm("Crimson Keep");
        Assert.Empty(store.BotGlossaryEntries());

        string? resumedTranslatorPayload = null;
        await team.Run(team.Latest!, options, progress: null, send: (req, _) =>
        {
            if (req.Role == "translator")
            {
                resumedTranslatorPayload = req.Data;
                using var doc = System.Text.Json.JsonDocument.Parse(req.Data);
                var map = new Dictionary<string, string>();
                foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                    map[entry.GetProperty("id").GetString()!] = "زر البوابة.";
                return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { translations = map }));
            }
            using var rdoc = System.Text.Json.JsonDocument.Parse(req.Data);
            var rev = new Dictionary<string, object>();
            foreach (var entry in rdoc.RootElement.GetProperty("entries").EnumerateArray())
                rev[entry.GetProperty("id").GetString()!] = new { ok = true, reason = "", suggestion = "" };
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(rev));
        }, approveReviewed: true);

        Assert.NotNull(resumedTranslatorPayload);
        Assert.DoesNotContain("الحصن القرمزي الخاطئ", resumedTranslatorPayload!);
    }

    [Fact]
    public void StudioXamlDefinesDarkSafeTemplatesAndAvoidsInvertedRtlDockPanels()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string stylesXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "Locara.Desktop", "Styles", "StudioStyles.xaml"));
        string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "Locara.Desktop", "MainWindow.xaml"));

        Assert.Contains("TargetType=\"DataGridColumnHeader\"", stylesXaml);
        Assert.Contains("ControlTemplate TargetType=\"DataGridColumnHeader\"", stylesXaml);
        Assert.Contains("ControlTemplate TargetType=\"MenuItem\"", stylesXaml);
        Assert.Contains("TargetType=\"ContextMenu\"", stylesXaml);

        // No horizontal DockPanel.Dock="Left" or "Right" inside MainWindow.xaml (which invert under RTL)
        Assert.DoesNotContain("DockPanel.Dock=\"Right\"", mainXaml);
        Assert.DoesNotContain("DockPanel.Dock=\"Left\"", mainXaml);
    }
}
