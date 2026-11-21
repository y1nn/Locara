using System.Text;
using System.Text.Json;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class TranslationAndTeamTests
{
    [Fact]
    public async Task TranslationCancellationAndResume()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "text.json"),
            "{\"a\":\"Back\",\"b\":\"Back\",\"greeting\":\"Welcome [player_name]\",\"number\":3}");

        using var store = ProjectStore.Create(ws.PathOf("api-project"), "Resume", source);
        new ProjectService(store).Scan();
        var runner = new TranslationRunner(store);
        using var cancel = new CancellationTokenSource();
        var fake = new FixtureProvider(cancel);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.Run(new("fixture", "fixture", "", BatchSize: 1),
                new InlineProgress(_ => cancel.Cancel()), cancel.Token, fake));
        Assert.Equal(1, store.Units().Count(u => u.Status == "translated"));

        await runner.Run(new("fixture", "fixture", "", BatchSize: 1), provider: new FixtureProvider());
        Assert.Equal(0, runner.Estimate().Units);
    }

    [Fact]
    public async Task ScopedResumeBudgetSuggestionsCsvReviewAndBackup()
    {
        using var ws = new TempWorkspace();
        string scopedSource = ws.PathOf("scoped.json");
        string scopedProject = ws.PathOf("scoped-project");
        File.WriteAllText(scopedSource, """{"a":"Start {0}","b":"Manual draft","c":"Resume this","d":"Out of scope"}""");
        var scopedOptions = TestHelpers.ScopedOptions;

        using (var store = ProjectStore.Create(scopedProject, "Scope", scopedSource))
        {
            new ProjectService(store).Scan();
            var runner = new TranslationRunner(store);
            var job = runner.Capture("Menu", store.Units().Where(row => row.Locator != "/d"), scopedOptions);
            Assert.Equal((1, "Start {0}".Length), runner.Estimate(job, 1));

            using var stop = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.Run(scopedOptions, new InlineProgress(_ => stop.Cancel()), stop.Token, new FixtureProvider(), job));

            var manual = store.Units().Single(row => row.Locator == "/b");
            manual.Target = "ترجمة يدوية";
            store.Save(manual, false);
        }

        using (var store = new ProjectStore(scopedProject))
        {
            var runner = new TranslationRunner(store);
            var job = runner.LastJob ?? throw new InvalidOperationException("Lost translation job.");
            Assert.Equal(1, runner.Estimate(job).Units);
            Assert.Equal(1, runner.Usage(job).Requests);

            var fake = new FixtureProvider();
            var budgetError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.Run(scopedOptions, provider: fake, job: job));
            Assert.Contains("بلغ التشغيل", budgetError.Message);
            Assert.Equal(0, fake.Calls);

            await runner.Run(scopedOptions with { MaxRequests = 2 }, provider: fake, job: job);
            Assert.Equal(1, fake.Calls);
            Assert.Equal(2, runner.Usage(job).Requests);
            Assert.Equal("ترجمة يدوية", store.Units().Single(row => row.Locator == "/b").Target);
            Assert.Equal("pending", store.Units().Single(row => row.Locator == "/d").Status);

            var menu = store.Units().Single(row => row.Locator == "/a");
            store.Save(menu, true);
            var suggestion = await runner.Suggest(menu.Id, scopedOptions, provider: fake);
            Assert.Equal(menu.Id, suggestion.Original.Id);
            Assert.Equal(menu.Revision, store.FindUnit(menu.Id).Revision);
            Assert.Equal("approved", store.FindUnit(menu.Id).Status);

            string csvReview = ws.PathOf("review-exchange.csv");
            var exchange = new ReviewExchange(store);
            var reviewRows = store.Units().Where(row => row.Locator is "/a" or "/c").ToList();
            exchange.Export(csvReview, reviewRows);

            var revised = reviewRows.Single(row => row.Locator == "/a");
            string exportedMenu = revised.Target;
            revised.Target = "تعديل يَدوي {0}";
            store.Save(revised, false);
            string another = reviewRows.Single(row => row.Locator == "/c").Target;
            File.WriteAllText(csvReview, File.ReadAllText(csvReview)
                .Replace(exportedMenu, "مقترح {0}").Replace(another, "سطر، مراجع"), Encoding.UTF8);

            var changes = exchange.Preview(csvReview);
            Assert.Equal(2, changes.Count);
            Assert.Equal(1, changes.Count(change => change.Issue.Length > 0));
            Assert.Equal(1, exchange.Apply(changes));
            Assert.Equal("تعديل يَدوي {0}", store.FindUnit(menu.Id).Target);

            store.Glossary("Observatory", "مرصد");
            store.Save(store.FindUnit(menu.Id), true);
            string backup = ws.PathOf("scoped-backup.locara");
            store.Backup(backup);

            using var restored = new ProjectStore(backup);
            Assert.Equal("approved", restored.FindUnit(menu.Id).Status);
            Assert.Equal("مرصد", restored.Glossary()["Observatory"]);
            Assert.Equal("سطر، مراجع", restored.Units().Single(row => row.Locator == "/c").Target);
        }
    }

    [Fact]
    public async Task ConcurrentTeamRolesTerminologyCorrectionProtectedEditsAndIdempotentRerun()
    {
        using var ws = new TempWorkspace();
        string teamSource = ws.PathOf("team.json");
        string teamProject = ws.PathOf("team-project");
        File.WriteAllText(teamSource, """{"menu":{"back":"Back"},"dialogue":{"line":"Enter {0}"}}""");

        using var store = ProjectStore.Create(teamProject, "Team", teamSource);
        new ProjectService(store).Scan();
        store.Glossary("Back", "رجوع");
        var team = new TranslationTeam(store);
        var options = TestHelpers.ScopedOptions with { MaxRequests = 20 };
        var settings = new TeamSettings(Style: "فصحى معاصرة", SupervisorModel: "supervisor-fixture",
            TranslatorModel: "translator-fixture", ReviewerModel: "reviewer-fixture", Workers: 2);
        var plan = team.Capture(store.Units(), options, settings);
        Assert.Equal(2, plan.Workers);
        Assert.All(plan.Prompts.Values, prompt => Assert.Contains(settings.Style, prompt));

        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int translators = 0, corrections = 0;

        async Task<string> Mock(TeamRequest request, CancellationToken token)
        {
            Assert.Equal(request.Role + "-fixture", request.Model);
            Assert.Contains(settings.Style, request.Instructions);
            Assert.DoesNotContain(settings.Style, request.Data);
            using var json = JsonDocument.Parse(request.Data);
            if (request.Role == "supervisor")
                return JsonSerializer.Serialize(json.RootElement.GetProperty("sections").EnumerateArray()
                    .Select((item, index) => new
                    {
                        id = item.GetProperty("id").GetString()!,
                        assignment = new { worker = index + 1, guidance = "Use the shared glossary." }
                    })
                    .ToDictionary(item => item.id, item => item.assignment));

            var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            string stage = json.RootElement.GetProperty("stage").GetString()!;
            if (stage == "translate")
            {
                if (Interlocked.Increment(ref translators) == 2) overlap.TrySetResult();
                await overlap.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
                if (entries[0].GetProperty("source").GetString()!.StartsWith("Enter", StringComparison.Ordinal))
                {
                    using var manual = new ProjectStore(teamProject);
                    var row = manual.Units().Single(item => item.Source.StartsWith("Enter", StringComparison.Ordinal));
                    row.Target = "تعديل بشري {0}";
                    manual.Save(row, false);
                }
                return JsonSerializer.Serialize(new
                {
                    entries = entries.Select(item => new
                    {
                        id = item.GetProperty("id").GetString(),
                        translation = item.GetProperty("source").GetString() == "Back" ? "عودة" : "ادخل __LOCARA_0__"
                    })
                });
            }
            if (stage == "correct")
            {
                Interlocked.Increment(ref corrections);
                return JsonSerializer.Serialize(entries.ToDictionary(item => item.GetProperty("id").GetString()!, _ => "رجوع"));
            }
            return JsonSerializer.Serialize(entries.ToDictionary(item => item.GetProperty("id").GetString()!,
                _ => new { ok = true, reason = "", suggestion = "" }));
        }

        var outcome = await team.Run(plan, options, null, send: Mock);
        Assert.Equal(2, outcome.Completed);
        Assert.Equal(1, outcome.Saved);
        Assert.Equal(0, outcome.Unknown);
        Assert.Equal(7, outcome.Usage.Requests);
        Assert.Equal(1, corrections);
        Assert.Equal(2, translators);
        Assert.Equal("رجوع", store.Units().Single(row => row.Source == "Back").Target);
        Assert.Equal("تعديل بشري {0}", store.Units().Single(row => row.Source.StartsWith("Enter", StringComparison.Ordinal)).Target);
        Assert.Contains("تغيّر السطر", team.Notes(plan).Single().Issue);
        Assert.Equal(1, team.ApproveReady(plan));

        var duplicate = await team.Run(plan, options, null,
            send: (_, _) => throw new InvalidOperationException("Completed team work was sent again."));
        Assert.Equal(outcome.Usage, duplicate.Usage);
    }

    [Fact]
    public async Task TeamPausePersistentSharedBudgetAndMalformedRetry()
    {
        using var ws = new TempWorkspace();
        string pauseSource = ws.PathOf("team-pause.json");
        string pauseProject = ws.PathOf("team-pause");
        File.WriteAllText(pauseSource, """{"one":"First","two":"Second"}""");
        var pausedOptions = TestHelpers.ScopedOptions with { MaxRequests = 20 };
        string? completedSource = null;

        using (var store = ProjectStore.Create(pauseProject, "Team pause", pauseSource))
        {
            new ProjectService(store).Scan();
            var team = new TranslationTeam(store);
            var plan = team.Capture(store.Units(), pausedOptions, new(Workers: 1));
            using var stop = new CancellationTokenSource();

            Task<string> StopAfterFirst(TeamRequest request, CancellationToken token)
            {
                using var data = JsonDocument.Parse(request.Data);
                var row = data.RootElement.GetProperty("entries")[0];
                string id = row.GetProperty("id").GetString()!;
                string src = row.GetProperty("source").GetString()!;
                if (completedSource != null && completedSource != src)
                {
                    stop.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                completedSource = src;
                return Task.FromResult(request.Role == "reviewer"
                    ? JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { ok = true, reason = "", suggestion = "" } })
                    : JsonSerializer.Serialize(new Dictionary<string, string> { [id] = "الأول" }));
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                team.Run(plan, pausedOptions, null, stop.Token, StopAfterFirst));
        }

        using (var store = new ProjectStore(pauseProject))
        {
            var team = new TranslationTeam(store);
            var plan = team.Latest ?? throw new InvalidOperationException("Saved team plan missing");
            var pause = team.Summary(plan);
            Assert.Equal(1, pause.Completed);
            Assert.Equal(1, pause.Unknown);
            Assert.Equal(3, pause.Usage.Requests);

            string completedId = store.Units().Single(u => u.Source == completedSource).Id;
            var runner = new TranslationRunner(store);
            await runner.Suggest(completedId, pausedOptions, provider: new FixtureProvider());

            var blocked = await team.Run(plan, pausedOptions with { MaxRequests = 3 }, null,
                send: (_, _) => throw new InvalidOperationException("Request sent beyond the team budget."));
            Assert.Equal(3, blocked.Usage.Requests);
            Assert.Equal(1, blocked.Completed);
            Assert.Equal(0, blocked.Unknown);
            Assert.NotEmpty(blocked.PauseReason);

            bool malformed = true;
            Task<string> Finish(TeamRequest request, CancellationToken cancellation)
            {
                using var data = JsonDocument.Parse(request.Data);
                var row = data.RootElement.GetProperty("entries")[0];
                string id = row.GetProperty("id").GetString()!;
                string src = row.GetProperty("source").GetString()!;
                Assert.NotEqual(completedSource, src);
                if (malformed) { malformed = false; return Task.FromResult("{}"); }
                return Task.FromResult(request.Role == "reviewer"
                    ? JsonSerializer.Serialize(new { entries = new[] { new { id, ok = true, reason = "", suggestion = "" } } })
                    : JsonSerializer.Serialize(new Dictionary<string, string> { [id] = "الثاني" }));
            }

            var finished = await team.Run(plan, pausedOptions, null, send: Finish, approveReviewed: true);
            Assert.Equal(2, finished.Completed);
            Assert.Equal(0, finished.Unknown);
            Assert.Equal(2, finished.Saved);
            Assert.Equal(6, finished.Usage.Requests);
            Assert.All(store.Units(), row => Assert.Equal("approved", row.Status));
        }
    }

    [Fact]
    public async Task TeamMalformedTranslationBatchRetriesOnResumeAndParsesProseJson()
    {
        using var ws = new TempWorkspace();
        string retrySource = ws.PathOf("team-retry.json");
        string retryProject = ws.PathOf("team-retry");
        File.WriteAllText(retrySource, """{"one":"Retry me"}""");
        var options = TestHelpers.ScopedOptions with { MaxRequests = 20 };

        using var store = ProjectStore.Create(retryProject, "Team retry", retrySource);
        new ProjectService(store).Scan();
        var team = new TranslationTeam(store);
        var plan = team.Capture(store.Units(), options, new(Workers: 1));
        var first = await team.Run(plan, options, null, send: (_, _) => Task.FromResult("not json"));
        Assert.Equal(1, first.Completed);
        Assert.Equal(0, first.Saved);
        Assert.Equal("", team.Notes(plan).Single().Draft);

        var resumed = await team.Run(plan, options, null, send: (request, _) =>
        {
            using var data = JsonDocument.Parse(request.Data);
            string id = data.RootElement.GetProperty("entries")[0].GetProperty("id").GetString()!;
            return Task.FromResult(request.Role == "reviewer"
                ? "Review:\n```json\n" + JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { ok = true, reason = "", suggestion = "" } }) + "\n```\nDone."
                : "Translation:\n```json\n" + JsonSerializer.Serialize(new Dictionary<string, string> { [id] = "أعد المحاولة" }) + "\n```\nDone.");
        }, approveReviewed: true);

        Assert.Equal(1, resumed.Completed);
        Assert.Equal(1, resumed.Saved);
        Assert.Equal("approved", store.Units().Single().Status);
        Assert.Equal("أعد المحاولة", store.Units().Single().Target);
        Assert.Empty(team.Notes(plan));
    }

    [Fact]
    public async Task TeamReusesApprovedTranslationMemoryWithoutApiCalls()
    {
        using var ws = new TempWorkspace();
        string xmlSource = ws.PathOf("memory.xml");
        File.WriteAllText(xmlSource, """
            <resources>
              <string>Back</string>
              <string>Back</string>
              <string>Exit</string>
            </resources>
            """);
        var options = TestHelpers.ScopedOptions with { BatchSize = 10, MaxRequests = 20 };

        using var store = ProjectStore.Create(ws.PathOf("team-memory"), "Team memory", xmlSource);
        new ProjectService(store).Scan();
        var units = store.Units();
        var firstBack = units.First(u => u.Source == "Back");
        firstBack.Target = "رجوع";
        store.Save(firstBack, approve: true);

        var team = new TranslationTeam(store);
        var pending = store.Units(status: "pending");
        Assert.Equal(2, pending.Count); // second "Back" + "Exit"
        var plan = team.Capture(pending, options, new(Workers: 1));

        var sentIds = new List<string>();
        var sentSources = new List<string>();
        var result = await team.Run(plan, options, null, send: (request, _) =>
        {
            using var data = JsonDocument.Parse(request.Data);
            var entries = data.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            foreach (var entry in entries)
            {
                sentIds.Add(entry.GetProperty("id").GetString()!);
                sentSources.Add(entry.GetProperty("source").GetString()!);
            }
            if (request.Role == "reviewer")
                return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(
                    e => e.GetProperty("id").GetString()!, _ => new { ok = true, reason = "", suggestion = "" })));
            return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(
                e => e.GetProperty("id").GetString()!, _ => "خروج")));
        }, approveReviewed: true);

        var secondBack = store.Units().First(u => u.Source == "Back" && u.Id != firstBack.Id);
        var exit = store.Units().Single(u => u.Source == "Exit");
        Assert.Equal(2, result.Saved);
        Assert.Equal("رجوع", secondBack.Target);
        Assert.Equal("approved", secondBack.Status);
        Assert.Equal("خروج", exit.Target);
        Assert.DoesNotContain("Back", sentSources);
        Assert.Contains("Exit", sentSources);
        Assert.All(sentIds, id => Assert.Equal("1", id));
    }

    [Fact]
    public async Task TeamNewRunCleansUpPreviousRunsInSameScope()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("cleanup.json");
        File.WriteAllText(source, """{"a":"One","b":"Two"}""");
        var options = TestHelpers.ScopedOptions with { BatchSize = 10, MaxRequests = 20 };

        using var store = ProjectStore.Create(ws.PathOf("team-cleanup"), "Team cleanup", source);
        new ProjectService(store).Scan();
        var team = new TranslationTeam(store);

        var firstPlan = team.Capture([store.Units().Single(u => u.Locator == "/a")], options, new(Workers: 1));
        await team.Run(firstPlan, options, null, send: (request, _) =>
        {
            using var data = JsonDocument.Parse(request.Data);
            string id = data.RootElement.GetProperty("entries")[0].GetProperty("id").GetString()!;
            return Task.FromResult(request.Role == "reviewer"
                ? JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { ok = true, reason = "", suggestion = "" } })
                : JsonSerializer.Serialize(new Dictionary<string, string> { [id] = "واحد" }));
        });

        var secondPlan = team.Capture([store.Units().Single(u => u.Locator == "/b")], options, new(Workers: 1));
        await team.Run(secondPlan, options, null, send: (request, _) =>
        {
            using var data = JsonDocument.Parse(request.Data);
            string id = data.RootElement.GetProperty("entries")[0].GetProperty("id").GetString()!;
            return Task.FromResult(request.Role == "reviewer"
                ? JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { ok = true, reason = "", suggestion = "" } })
                : JsonSerializer.Serialize(new Dictionary<string, string> { [id] = "اثنان" }));
        });

        Assert.Equal(secondPlan.Id, team.Latest?.Id);
        // Verify old run was cleaned up from team_runs (and cascaded to team_batches).
        using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(store.DirectoryPath, "project.db")}");
        verify.Open();
        using var cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM team_runs";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM team_batches";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task ProviderThinkingBlocksErrorDetailAndJsonModeResponseFormat()
    {
        var probeUnit = new Unit { Id = "u1", Source = "Start" };

        string geminiPayload = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new object[]
                        {
                            new { thought = true, text = "Thinking step..." },
                            new { thought = false, text = "{\"u1\":\"ابدأ\"}" }
                        }
                    }
                }
            },
            usageMetadata = new { promptTokenCount = 120, candidatesTokenCount = 45, totalTokenCount = 165 }
        });
        ApiProvider? geminiProvider = null;
        var (geminiResult, _) = await TestHelpers.CallLoopback(200, geminiPayload, url =>
        {
            geminiProvider = new ApiProvider(new("gemini", "gemini-2.5-flash", "k", url));
            return geminiProvider.Translate([probeUnit], new Dictionary<string, string>(), default);
        });
        Assert.Equal("ابدأ", geminiResult["u1"]);
        Assert.Equal(new TokenUsage(120, 45, 165), geminiProvider!.LastTokens);

        string anthropicPayload = JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                new { type = "thinking", thinking = "Reasoning..." },
                new { type = "text", text = "{\"u1\":\"ابدأ\"}" }
            },
            usage = new { input_tokens = 90, output_tokens = 30 }
        });
        ApiProvider? anthropicProvider = null;
        var (anthropicResult, _) = await TestHelpers.CallLoopback(200, anthropicPayload, url =>
        {
            anthropicProvider = new ApiProvider(new("anthropic", "claude-sonnet-4-6", "k", url));
            return anthropicProvider.Translate([probeUnit], new Dictionary<string, string>(), default);
        });
        Assert.Equal("ابدأ", anthropicResult["u1"]);
        Assert.Equal(new TokenUsage(90, 30, 120), anthropicProvider!.LastTokens);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            TestHelpers.CallLoopback(429, """{"error":{"message":"Quota exceeded for model"}}""", url =>
                new ApiProvider(new("openai-compatible", "fixture", "k", url)).Translate([probeUnit], new Dictionary<string, string>(), default)));
        Assert.Contains("Quota exceeded for model", error.Message, StringComparison.Ordinal);

        string chatOk = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "{\"u1\":\"ابدأ\"}" } } },
            usage = new { prompt_tokens = 50, completion_tokens = 15, total_tokens = 65 }
        });

        ApiProvider? deepseekProvider = null;
        var (_, deepseekBody) = await TestHelpers.CallLoopback(200, chatOk, url =>
        {
            deepseekProvider = new ApiProvider(new("deepseek", "deepseek-chat", "k", url));
            return deepseekProvider.Translate([probeUnit], new Dictionary<string, string>(), default);
        });
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", deepseekBody);
        Assert.Equal(new TokenUsage(50, 15, 65), deepseekProvider!.LastTokens);

        var (_, openAiDefaultBody) = await TestHelpers.CallLoopback(200, chatOk, url =>
            new ApiProvider(new("openai-compatible", "fixture", "k", url)).Translate([probeUnit], new Dictionary<string, string>(), default));
        Assert.DoesNotContain("response_format", openAiDefaultBody);

        var (_, openAiJsonModeBody) = await TestHelpers.CallLoopback(200, chatOk, url =>
            new ApiProvider(new("openai-compatible", "fixture", "k", url, JsonMode: true)).Translate([probeUnit], new Dictionary<string, string>(), default));
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", openAiJsonModeBody);
    }

    [Fact]
    public async Task TeamParallelSingleSectionFastModeAndAiDraftReview()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("single-file.json");
        File.WriteAllText(source, """{"k1":"One","k2":"Two","k3":"Three","k4":"Four","k5":"Five","k6":"Six","k7":"Seven","k8":"Eight"}""");
        var options = TestHelpers.ScopedOptions with { BatchSize = 2, MaxRequests = 100 };

        using var store = ProjectStore.Create(ws.PathOf("parallel-single"), "ParallelSingle", source);
        new ProjectService(store).Scan();
        var team = new TranslationTeam(store);

        // 1) Single section sharded across 4 parallel workers in FastMode (no reviewer calls!)
        var fastSettings = new TeamSettings(Workers: 4, BatchSize: 2, FastMode: true);
        var fastPlan = team.Capture(store.Units(), options, fastSettings);
        Assert.Equal(4, fastPlan.Batches.Length);
        Assert.Equal(4, fastPlan.Workers);
        Assert.False(fastPlan.UseSupervisor);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, calls = 0;
        var activities = new System.Collections.Concurrent.ConcurrentBag<AgentActivity>();
        var progress = new Progress<TeamProgress>(p => { if (p.Activity != null) activities.Add(p.Activity); });

        var fastResult = await team.Run(fastPlan, options, progress, send: async (request, token) =>
        {
            Assert.Equal("translator", request.Role);
            Interlocked.Increment(ref calls);
            if (Interlocked.Increment(ref active) == 4) barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            using var data = JsonDocument.Parse(request.Data);
            var entries = data.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            return JsonSerializer.Serialize(entries.ToDictionary(
                e => e.GetProperty("id").GetString()!,
                e => e.GetProperty("source").GetString()! + " عربي"));
        }, approveReviewed: false);

        Assert.Equal(4, fastResult.Completed);
        Assert.Equal(8, fastResult.Saved);
        Assert.Equal(4, calls); // 4 batches * 1 request each (no supervisor, no reviewer!)
        Assert.All(store.Units(), u => Assert.Equal("translated", u.Status));

        // 2) AI Draft Review mode reviews the 8 existing "translated" drafts directly and auto-approves them!
        var reviewSettings = new TeamSettings(Workers: 2, BatchSize: 4, FastMode: false, ReviewDrafts: true);
        var reviewPlan = team.Capture(store.Units(status: "review"), options, reviewSettings);
        Assert.Equal(2, reviewPlan.Batches.Length);

        int reviewerCalls = 0;
        var reviewResult = await team.Run(reviewPlan, options, null, send: (request, _) =>
        {
            Assert.Equal("reviewer", request.Role);
            Interlocked.Increment(ref reviewerCalls);
            using var data = JsonDocument.Parse(request.Data);
            var entries = data.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(
                e => e.GetProperty("id").GetString()!,
                _ => new { ok = true, reason = "", suggestion = "" })));
        }, approveReviewed: true);

        Assert.Equal(2, reviewerCalls);
        Assert.Equal(8, reviewResult.Saved);
        Assert.All(store.Units(), u => Assert.Equal("approved", u.Status));
        Assert.True(File.Exists(team.LogPath));
        Assert.Contains("START", File.ReadAllText(team.LogPath));
    }

    [Fact]
    public async Task DisplayBracketsAllowedAndReviewerSuggestionAppliedInSingleRequest()
    {
        // UI display brackets like [Hold]/[Locked] are translatable; code tokens remain strictly protected.
        Assert.Empty(Tokens.Validate("Remove <color=#ffea00>[Hold]", "إزالة <color=#ffea00>[مطولاً]"));
        Assert.Empty(Tokens.Validate("Access [Locked] {0}", "الوصول [مقفل] {0}"));
        Assert.NotEmpty(Tokens.Validate("Hello [player_name]", "مرحباً [اسم]"));
        Assert.NotEmpty(Tokens.Validate("Press [KEY_JUMP]", "اضغط [قفز]"));

        using var ws = new TempWorkspace();
        string source = ws.PathOf("brackets.json");
        File.WriteAllText(source, """{"a":"Remove <color=#ffea00>[Hold]","b":"Ready"}""");
        var options = TestHelpers.ScopedOptions with { BatchSize = 10, MaxRequests = 20 };

        using var store = ProjectStore.Create(ws.PathOf("auto-fix"), "AutoFix", source);
        new ProjectService(store).Scan();
        var units = store.Units();
        var holdUnit = units.Single(u => u.Locator == "/a");
        holdUnit.Target = "إزالة <color=#ffea00>[Hold]";
        store.Save(holdUnit, approve: false);
        var readyUnit = units.Single(u => u.Locator == "/b");
        readyUnit.Target = "جاهز";
        store.Save(readyUnit, approve: false);

        var team = new TranslationTeam(store);
        var plan = team.Capture(store.Units(status: "review"), options, new(Workers: 16, BatchSize: 10, ReviewDrafts: true));
        int calls = 0;
        var result = await team.Run(plan, options, null, send: (request, _) =>
        {
            calls++;
            Assert.Equal("reviewer", request.Role);
            using var data = JsonDocument.Parse(request.Data);
            var entries = data.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            var reply = new Dictionary<string, object>();
            foreach (var entry in entries)
            {
                string wireId = entry.GetProperty("id").GetString()!;
                Assert.True(wireId.Length <= 2); // Compact wire ID ("1", "2") instead of 64-char SHA-256!
                string src = entry.GetProperty("source").GetString()!;
                reply[wireId] = src.Contains("[Hold]", StringComparison.Ordinal)
                    ? (object)new
                    {
                        ok = false,
                        reason = "بقاء كلمة [Hold] دون تعريب.",
                        suggestion = "إزالة <color=#ffea00>[مطولاً]"
                    }
                    : new { ok = true };
            }
            return Task.FromResult(JsonSerializer.Serialize(reply));
        }, approveReviewed: true);

        Assert.Equal(1, calls); // Reviewer suggestion applied directly with zero extra translator/verify round-trips!
        Assert.Equal(2, result.Saved);
        Assert.Equal("إزالة <color=#ffea00>[مطولاً]", store.FindUnit(holdUnit.Id).Target);
        Assert.Equal("approved", store.FindUnit(holdUnit.Id).Status);
        Assert.Equal("approved", store.FindUnit(readyUnit.Id).Status);
        Assert.Contains("autoFixed=1", File.ReadAllText(team.LogPath));
    }

    [Fact]
    public async Task TeamRoutesRolesAcrossDistinctApiProfilesAndLoopbackServers()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("multi-api.json");
        File.WriteAllText(source, """{"a":"Start"}""");

        using var store = ProjectStore.Create(ws.PathOf("multi-api"), "MultiApi", source);
        new ProjectService(store).Scan();
        var unit = store.Units().Single();
        var team = new TranslationTeam(store);

        string translatorHttpReply = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = JsonSerializer.Serialize(new Dictionary<string, string> { ["1"] = "ابدأ" }) } } },
            usage = new { prompt_tokens = 100, completion_tokens = 25, total_tokens = 125 }
        });
        string reviewerHttpReply = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = JsonSerializer.Serialize(new Dictionary<string, object>
                        {
                            ["1"] = new { ok = true }
                        })
                    }
                }
            },
            usage = new { prompt_tokens = 200, completion_tokens = 40, total_tokens = 240 }
        });

        string translatorReqBody = "";
        string reviewerReqBody = "";
        TeamProgress? outcome = null;

        var (_, outerBody) = await TestHelpers.CallLoopback(200, translatorHttpReply, async translatorUrl =>
        {
            var (_, innerBody) = await TestHelpers.CallLoopback(200, reviewerHttpReply, async reviewerUrl =>
            {
                var primary = new ProviderOptions("openai-compatible", "default-model", "k0", translatorUrl, BatchSize: 10, MaxRequests: 20);
                var profiles = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["FastAPI"] = new("openai-compatible", "fast-model", "k1", translatorUrl, BatchSize: 10, MaxRequests: 20),
                    ["SmartAPI"] = new("openai-compatible", "smart-model", "k2", reviewerUrl, BatchSize: 10, MaxRequests: 20)
                };
                var settings = new TeamSettings(Workers: 1, BatchSize: 10,
                    TranslatorProfile: "FastAPI", TranslatorModel: "fast-model",
                    ReviewerProfile: "SmartAPI", ReviewerModel: "smart-model");
                var plan = team.Capture(store.Units(), primary, settings, profiles);
                outcome = await team.Run(plan, primary, null, approveReviewed: true, profiles: profiles);
                return outcome;
            });
            reviewerReqBody = innerBody;
            return outcome!;
        });
        translatorReqBody = outerBody;

        Assert.NotNull(outcome);
        Assert.Equal(1, outcome!.Saved);
        Assert.Equal(365, outcome.Usage.TotalTokens); // 125 from FastAPI + 240 from SmartAPI
        Assert.Equal("ابدأ", store.FindUnit(unit.Id).Target);
        Assert.Equal("approved", store.FindUnit(unit.Id).Status);
        Assert.Contains("\"model\":\"fast-model\"", translatorReqBody);
        Assert.Contains("\"model\":\"smart-model\"", reviewerReqBody);
        Assert.DoesNotContain(unit.Id, translatorReqBody);
        Assert.DoesNotContain(unit.Id, reviewerReqBody);
        string log = File.ReadAllText(team.LogPath);
        Assert.Contains("target=FastAPI/fast-model", log);
        Assert.Contains("target=SmartAPI/smart-model", log);
    }

    [Fact]
    public async Task TeamRoutesIndividualWorkersToDifferentProfilesModelsAndSupportsResumeOverride()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("per-worker.json");
        File.WriteAllText(source, """{"k1":"One","k2":"Two","k3":"Three","k4":"Four"}""");

        using var store = ProjectStore.Create(ws.PathOf("per-worker"), "PerWorker", source);
        new ProjectService(store).Scan();
        var team = new TranslationTeam(store);

        var primary = TestHelpers.ScopedOptions with { BatchSize = 1, MaxRequests = 2 };
        var profiles = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProfileA"] = new("openai-compatible", "a-default", "ka", "http://127.0.0.1:4243/v1", BatchSize: 1, MaxRequests: 20),
            ["ProfileB"] = new("openai-compatible", "b-default", "kb", "http://127.0.0.1:4244/v1", BatchSize: 1, MaxRequests: 20)
        };
        var initialSettings = new TeamSettings(
            Workers: 2, BatchSize: 1, FastMode: true,
            WorkerTargets: new Dictionary<int, AgentTarget>
            {
                [1] = new("ProfileA", "model-w1"),
                [2] = new("ProfileB", "") // Empty model inherits ProfileB's default model ("b-default")!
            });
        var plan = team.Capture(store.Units(), primary, initialSettings, profiles);
        Assert.Equal(4, plan.Batches.Length);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        var seen = new System.Collections.Concurrent.ConcurrentBag<(int Worker, string Profile, string Model)>();
        var activities = new System.Collections.Concurrent.ConcurrentBag<AgentActivity>();
        var progress = new Progress<TeamProgress>(p => { if (p.Activity != null) activities.Add(p.Activity); });

        // First run stops after 2 requests (budget=2) so 2 batches remain for resume!
        var first = await team.Run(plan, primary, progress, send: async (request, token) =>
        {
            seen.Add((request.WorkerId, request.Profile, request.Model));
            if (Interlocked.Increment(ref active) == 2) barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            using var data = JsonDocument.Parse(request.Data);
            var entry = data.RootElement.GetProperty("entries")[0];
            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [entry.GetProperty("id").GetString()!] = entry.GetProperty("source").GetString()! + " عربي"
            });
        }, approveReviewed: true, profiles: profiles);

        Assert.Equal(2, first.Completed);
        Assert.Contains(seen, item => item == (1, "ProfileA", "model-w1"));
        Assert.Contains(seen, item => item == (2, "ProfileB", "b-default"));

        // Resume with a brand-new primary model & overrideSettings (no connection mismatch error!)
        var resumedPrimary = new ProviderOptions("openai-compatible", "resumed-model", "k-new", "http://127.0.0.1:4245/v1",
            BatchSize: 1, MaxRequests: 20);
        var resumedOverride = new TeamSettings(Workers: 2, BatchSize: 1, FastMode: true,
            TranslatorModel: "resumed-model", WorkerTargets: null);
        var resumedModels = new System.Collections.Concurrent.ConcurrentBag<string>();

        var resumed = await team.Run(team.Latest!, resumedPrimary, null, send: (request, _) =>
        {
            resumedModels.Add(request.Model);
            using var data = JsonDocument.Parse(request.Data);
            var entry = data.RootElement.GetProperty("entries")[0];
            return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [entry.GetProperty("id").GetString()!] = entry.GetProperty("source").GetString()! + " مستأنف"
            }));
        }, approveReviewed: true, profiles: profiles, overrideSettings: resumedOverride);

        Assert.Equal(4, resumed.Completed);
        Assert.Equal(4, resumed.Saved);
        Assert.Equal(2, resumedModels.Count);
        Assert.All(resumedModels, m => Assert.Equal("resumed-model", m));
        Assert.All(store.Units(), u => Assert.Equal("approved", u.Status));
    }

    [Fact]
    public void TransientHttpTransportErrorsRetryWhileAuthAndTlsFailFast()
    {
        // Transport-level drops ("An error occurred while sending the request.") must be retried!
        Assert.True(TranslationRunner.CanRetry(new HttpRequestException("An error occurred while sending the request.")));
        Assert.True(TranslationRunner.CanRetry(new HttpRequestException(HttpRequestError.ResponseEnded, "Premature EOF.")));
        Assert.True(TranslationRunner.CanRetry(new HttpRequestException(HttpRequestError.ConnectionError, "Reset by peer.")));
        Assert.True(TranslationRunner.CanRetry(new IOException("Unable to read data from the transport connection.")));
        Assert.True(TranslationRunner.CanRetry(new HttpRequestException("Rate limited", null, System.Net.HttpStatusCode.TooManyRequests)));
        Assert.True(TranslationRunner.CanRetry(new HttpRequestException("Bad gateway", null, System.Net.HttpStatusCode.BadGateway)));

        // Auth/client errors & TLS certificate failures must fail immediately without wasteful retries!
        Assert.False(TranslationRunner.CanRetry(new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized)));
        Assert.False(TranslationRunner.CanRetry(new HttpRequestException("Bad request", null, System.Net.HttpStatusCode.BadRequest)));
        Assert.False(TranslationRunner.CanRetry(new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS pin mismatch.")));
    }
}
