using System.Net;
using System.Text;
using System.Text.Json;
using Locara.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Locara.Checks;

public sealed class AutonomousBotsTests
{
    [Fact]
    public void SchemaV5UpgradesToV6AndBotGlossaryRemainsSeparateFromManualGlossary()
    {
        using var ws = new TempWorkspace();
        string projDir = Directory.CreateDirectory(ws.PathOf("v5-proj")).FullName;
        string dbPath = Path.Combine(projDir, "project.db");

        // Build a real Schema v5 database
        using (var db = new SqliteConnection($"Data Source={dbPath}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                INSERT INTO settings VALUES('name','V5Upgrade');
                CREATE TABLE files(path TEXT PRIMARY KEY,hash TEXT NOT NULL,format TEXT NOT NULL,snapshot BLOB NOT NULL,active INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE units(id TEXT PRIMARY KEY,path TEXT NOT NULL,locator TEXT NOT NULL,source TEXT NOT NULL,target TEXT NOT NULL DEFAULT '',context TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'pending',warning TEXT NOT NULL DEFAULT '',active INTEGER NOT NULL DEFAULT 1,revision INTEGER NOT NULL DEFAULT 0,imported TEXT NOT NULL DEFAULT '');
                CREATE TABLE glossary(source TEXT PRIMARY KEY,target TEXT NOT NULL);
                INSERT INTO glossary VALUES('Iron Warden','الآمر الحديدي');
                CREATE TABLE attempts(id INTEGER PRIMARY KEY,unit_id TEXT NOT NULL,provider TEXT NOT NULL,state TEXT NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE sources(path TEXT PRIMARY KEY COLLATE NOCASE, scope TEXT NOT NULL UNIQUE);
                CREATE TABLE team_runs(id TEXT PRIMARY KEY,scope TEXT NOT NULL,package TEXT NOT NULL,engine TEXT NOT NULL,connection TEXT NOT NULL,settings_json TEXT NOT NULL,prompts_json TEXT NOT NULL,glossary_json TEXT NOT NULL,workers INTEGER NOT NULL,supervisor_inflight INTEGER NOT NULL DEFAULT 0,supervisor_sections_json TEXT NOT NULL DEFAULT '',supervisor_error TEXT NOT NULL DEFAULT '',used_characters INTEGER NOT NULL DEFAULT 0,used_requests INTEGER NOT NULL DEFAULT 0,pause_reason TEXT NOT NULL DEFAULT '',active INTEGER NOT NULL DEFAULT 1,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,used_input_tokens INTEGER NOT NULL DEFAULT 0,used_output_tokens INTEGER NOT NULL DEFAULT 0,used_total_tokens INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE team_batches(run_id TEXT NOT NULL,batch_id INTEGER NOT NULL,section TEXT NOT NULL,rows_json TEXT NOT NULL,stage TEXT NOT NULL DEFAULT 'translate',in_flight INTEGER NOT NULL DEFAULT 0,error TEXT NOT NULL DEFAULT '',drafts_json TEXT NOT NULL DEFAULT '',reviews_json TEXT NOT NULL DEFAULT '',saved_json TEXT NOT NULL DEFAULT '',skipped_json TEXT NOT NULL DEFAULT '',corrections INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(run_id,batch_id));
                CREATE TABLE team_notes(id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL,batch_id INTEGER NOT NULL,unit_id TEXT NOT NULL,source TEXT NOT NULL,draft TEXT NOT NULL DEFAULT '',suggestion TEXT NOT NULL DEFAULT '',issue TEXT NOT NULL DEFAULT '');
                CREATE TABLE usage_ledger(provider TEXT NOT NULL,model TEXT NOT NULL,requests INTEGER NOT NULL DEFAULT 0,input_tokens INTEGER NOT NULL DEFAULT 0,output_tokens INTEGER NOT NULL DEFAULT 0,thinking_tokens INTEGER NOT NULL DEFAULT 0,total_tokens INTEGER NOT NULL DEFAULT 0,characters INTEGER NOT NULL DEFAULT 0,updated TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,PRIMARY KEY(provider,model));
                PRAGMA user_version=5;
                """;
            cmd.ExecuteNonQuery();
        }

        using (var store = new ProjectStore(projDir))
        {
            store.SaveBotTerms(new Dictionary<string, string>
            {
                ["Iron Warden"] = "الحارس الحديدي",
                ["Crimson Citadel"] = "القلعة القرمزية"
            }, note: "lore-bot");

            // Manual glossary remains untouched!
            Assert.Equal("الآمر الحديدي", store.Glossary()["Iron Warden"]);
            Assert.False(store.Glossary().ContainsKey("Crimson Citadel"));

            var botTerms = store.BotGlossary();
            Assert.Equal("القلعة القرمزية", botTerms["Crimson Citadel"]);
            Assert.Equal("الحارس الحديدي", botTerms["Iron Warden"]);

            store.ClearBotGlossary();
            Assert.Empty(store.BotGlossary());
            Assert.Equal("الآمر الحديدي", store.Glossary()["Iron Warden"]);
        }

        using (var verify = new SqliteConnection($"Data Source={dbPath}"))
        {
            verify.Open();
            using var cmd = verify.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal(6L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task MultiTurnChatProviderFormatsOpenAiAnthropicGeminiAndParsesNewTermsEnvelope()
    {
        var history = new List<ChatTurn>
        {
            new("user", """{"entries":[{"id":"1","source":"Meet Elena"}]}"""),
            new("assistant", """{"1":"قابل إيلينا"}""")
        };

        // 1. OpenAI-compatible: system first, then user/assistant turns, then current user payload
        string openAiReply = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = """{"translations":{"1":"إيلينا تنتظرك"},"newTerms":{"Elena":"إيلينا"}}""" } } },
            usage = new { prompt_tokens = 80, completion_tokens = 20, total_tokens = 100 }
        });
        var (openAiRes, openAiBody) = await TestHelpers.CallLoopback(200, openAiReply, url =>
            new ApiProvider(new("openai-compatible", "gpt-test", "k", url, JsonMode: true))
                .RequestChatWithUsage("SYSTEM_RULES", history, """{"entries":[{"id":"1","source":"Elena awaits"}]}""", default));

        using (var bodyDoc = JsonDocument.Parse(openAiBody))
        {
            var msgs = bodyDoc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(4, msgs.Length);
            Assert.Equal("system", msgs[0].GetProperty("role").GetString());
            Assert.Equal("SYSTEM_RULES", msgs[0].GetProperty("content").GetString());
            Assert.Equal("user", msgs[1].GetProperty("role").GetString());
            Assert.Equal("assistant", msgs[2].GetProperty("role").GetString());
            Assert.Equal("user", msgs[3].GetProperty("role").GetString());
        }

        var parsed = ApiProvider.ReadTranslationsAndTermsPartial(openAiRes.Content, ["1"], out var missing, out var newTerms);
        Assert.Empty(missing);
        Assert.Equal("إيلينا تنتظرك", parsed["1"]);
        Assert.Equal("إيلينا", newTerms["Elena"]);

        // 2. Gemini: maps "assistant" -> "model" in contents[] while keeping systemInstruction separate
        string geminiReply = JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = """{"1":"حسناً"}""" } } } } },
            usageMetadata = new { promptTokenCount = 40, candidatesTokenCount = 10, totalTokenCount = 50 }
        });
        var (_, geminiBody) = await TestHelpers.CallLoopback(200, geminiReply, url =>
            new ApiProvider(new("gemini", "gemini-2.5-flash", "k", url))
                .RequestChatWithUsage("SYS", history, """{"1":"OK"}""", default));
        using (var gDoc = JsonDocument.Parse(geminiBody))
        {
            var contents = gDoc.RootElement.GetProperty("contents").EnumerateArray().ToArray();
            Assert.Equal(3, contents.Length);
            Assert.Equal("user", contents[0].GetProperty("role").GetString());
            Assert.Equal("model", contents[1].GetProperty("role").GetString());
            Assert.Equal("user", contents[2].GetProperty("role").GetString());
        }

        // 3. Anthropic: system top-level + alternating user/assistant messages
        string anthropicReply = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = """{"1":"تم"}""" } },
            usage = new { input_tokens = 30, output_tokens = 8 }
        });
        var (_, anthropicBody) = await TestHelpers.CallLoopback(200, anthropicReply, url =>
            new ApiProvider(new("anthropic", "claude-sonnet-4-6", "k", url))
                .RequestChatWithUsage("SYS_ANTH", history, """{"1":"Done"}""", default));
        using (var aDoc = JsonDocument.Parse(anthropicBody))
        {
            Assert.Equal("SYS_ANTH", aDoc.RootElement.GetProperty("system").GetString());
            var msgs = aDoc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(3, msgs.Length);
            Assert.Equal("assistant", msgs[1].GetProperty("role").GetString());
        }

        // 4. TeamRequest.InputCharacters accounts for multi-turn History
        var req = new TeamRequest("translator", "m", "12345", "67890", History: history);
        Assert.Equal(10 + history.Sum(h => h.Content.Length), req.InputCharacters);
    }

    [Fact]
    public void GameTermMinerExtractsSpeakersAndRecurringProperNounsWhileSkippingStopwordsAndManualGlossary()
    {
        var batches = new[]
        {
            new TeamBatch(1, "ch1.json",
            [
                new TeamRow("1", 1, "Elena: Welcome to Crimson Citadel, traveler.", "speaker=Elena"),
                new TeamRow("2", 1, "Elena: Seek the Iron Warden inside Crimson Citadel.", "speaker=Elena"),
                new TeamRow("3", 1, "Press Start to consult Elena near Crimson Citadel.", ""),
                new TeamRow("4", 1, "The Iron Warden guards the gate. Talk to Iron Warden.", "")
            ])
        };
        var manual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Start"] = "ابدأ"
        };

        var mined = GameTermMiner.ExtractRecurringTerms(batches, manual, maxTerms: 15);
        Assert.Contains(mined, t => t.Term == "Elena" && t.IsSpeaker);
        Assert.Contains(mined, t => t.Term == "Crimson Citadel" && t.Count >= 3);
        Assert.Contains(mined, t => t.Term == "Iron Warden" && t.Count >= 3);
        Assert.DoesNotContain(mined, t => t.Term.Equals("Start", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mined, t => t.Term.Equals("The", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SceneLaneBuilderKeepsContiguousSlicesInSmartModeAndAllowsSingleBatchShardsInFastMode()
    {
        var fourBatches = Enumerable.Range(1, 4)
            .Select(i => new TeamBatch(i, "dialogue.json", [new TeamRow(i.ToString(), 1, $"Line {i}", "")]))
            .ToArray();

        // Smart/Team mode: 4 batches across 4 requested workers -> at most 2 contiguous 2-batch lanes ([1,2], [3,4]) so scenes never become 1-batch islands!
        var smartLanes = SceneLaneBuilder.BuildLanes(fourBatches, workers: 4, fastMode: false);
        Assert.Equal(2, smartLanes.Count);
        Assert.Equal([1, 2], smartLanes[0].Batches.Select(b => b.Id).ToArray());
        Assert.Equal([3, 4], smartLanes[1].Batches.Select(b => b.Id).ToArray());

        // FastMode: 4 batches across 4 workers -> 4 lanes of 1 batch so all 4 workers run simultaneously!
        var fastLanes = SceneLaneBuilder.BuildLanes(fourBatches, workers: 4, fastMode: true);
        Assert.Equal(4, fastLanes.Count);
        Assert.All(fastLanes, lane => Assert.Single(lane.Batches));

        // 8 batches in Smart mode with 4 workers -> 4 contiguous 2-batch slices ([1,2], [3,4], [5,6], [7,8])!
        var eightBatches = Enumerable.Range(1, 8)
            .Select(i => new TeamBatch(i, "big_chapter.json", [new TeamRow(i.ToString(), 1, $"Line {i}", "")]))
            .ToArray();
        var chapterLanes = SceneLaneBuilder.BuildLanes(eightBatches, workers: 4, fastMode: false);
        Assert.Equal(4, chapterLanes.Count);
        Assert.Equal([5, 6], chapterLanes[2].Batches.Select(b => b.Id).ToArray());
    }

    [Fact]
    public void LiveGlossaryPrioritizesManualGlossaryMatchesRelevantTermsAndSmartQaEvaluatorRoutesCorrectly()
    {
        var manual = new Dictionary<string, string> { ["Iron Warden"] = "الآمر الحديدي" };
        var botSeed = new Dictionary<string, string>
        {
            ["Iron Warden"] = "الحارس الحديدي", // overridden by manual!
            ["Elena"] = "إيلينا",
            ["Unrelated Sword"] = "سيف بعيد"
        };

        var live = new LiveGlossary(manual, botSeed);
        live.TryAddBotTerms(new Dictionary<string, string>
        {
            ["Iron Warden"] = "تخمين آخر", // still overridden by manual!
            ["Crimson Citadel"] = "القلعة القرمزية"
        });

        var matched = live.MatchForTexts(["Elena entered the Crimson Citadel to meet the Iron Warden."]);
        Assert.Equal("الآمر الحديدي", matched["Iron Warden"]);
        Assert.Equal("إيلينا", matched["Elena"]);
        Assert.Equal("القلعة القرمزية", matched["Crimson Citadel"]);
        Assert.False(matched.ContainsKey("Unrelated Sword"));

        // SmartQaEvaluator:
        // 1) Clean short UI button -> skips external QA call (NeedsReview == false)
        Assert.False(SmartQaEvaluator.NeedsReview(new TeamRow("1", 1, "Save Game", ""), "حفظ اللعبة", new Dictionary<string, string>()));
        // 2) Narrative sentence with punctuation -> routes to QA Bot (NeedsReview == true)
        Assert.True(SmartQaEvaluator.NeedsReview(new TeamRow("2", 1, "Elena, where did you hide the key?", ""), "يا إيلينا، أين خبّأتِ المفتاح؟", matched));
        // 3) Untranslated acronym like "DLSS" -> routes to QA Bot (soft check, NOT hard failure!)
        Assert.True(SmartQaEvaluator.NeedsReview(new TeamRow("3", 1, "DLSS", ""), "DLSS", new Dictionary<string, string>()));
        // 4) Arabic prefix clitic ("لإيلينا" / "بالقلعة القرمزية") satisfies glossary stem check on short label!
        var elenaMatch = new Dictionary<string, string> { ["Elena"] = "إيلينا" };
        Assert.False(SmartQaEvaluator.NeedsReview(new TeamRow("4", 1, "Gift Elena", ""), "هدية لإيلينا", elenaMatch));
    }

    [Fact]
    public void SceneSessionStateBoundsMultiTurnHistoryAndMaintainsRollingContextTail()
    {
        var session = new SceneSessionState(maxTurns: 2, maxHistoryChars: 240, maxContextLines: 3);
        for (int b = 1; b <= 4; b++)
        {
            session.RecordCompletedTurn(
                userSummary: $"{{\"b\":{b},\"src\":\"Line {b}\"}}",
                assistantJson: $"{{\"1\":\"سطر {b}\"}}",
                completedLines: [new ContextLine($"Line {b}A", $"سطر {b}أ"), new ContextLine($"Line {b}B", $"سطر {b}ب")]);
        }

        var turns = session.GetHistorySnapshot();
        Assert.True(turns.Count <= 4); // 2 user+assistant pairs max
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("assistant", turns[^1].Role);
        Assert.True(turns.Sum(t => t.Content.Length) <= 240);

        var tail = session.GetContextTail();
        Assert.Equal(3, tail.Count);
        Assert.Equal("Line 4B", tail[^1].Source);
        Assert.Equal("سطر 4ب", tail[^1].Target);
    }

    [Fact]
    public async Task AutonomousBotsEndToEndLoreScoutStatefulMultiTurnRollingContextLiveGlossaryAndSmartQa()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("story.json");
        var script = new Dictionary<string, string>
        {
            ["s01"] = "Elena: Welcome to Crimson Citadel, traveler!",
            ["s02"] = "Elena: Keep the Aether Core safe inside Crimson Citadel.",
            ["s03"] = "Elena: Have you spoken with the guards of Crimson Citadel?",
            ["s04"] = "Elena: Wait for my signal at the gate.",
            ["s05"] = "Save",
            ["s06"] = "Elena: Bring the Aether Core to the altar now!",
            ["s07"] = "Options",
            ["s08"] = "Exit",
            ["s09"] = "DLSS",
            ["s10"] = "Audio",
            ["s11"] = "Graphics",
            ["s12"] = "Controls"
        };
        File.WriteAllText(source, JsonSerializer.Serialize(script));

        using var store = ProjectStore.Create(ws.PathOf("hive-proj"), "HiveGame", source);
        new ProjectService(store).Scan();
        var team = new TranslationTeam(store);
        var opts = TestHelpers.ScopedOptions with { BatchSize = 4, MaxRequests = 50, MaxCharacters = 200_000 };
        var settings = new TeamSettings(Style: "فصحى سردية", Workers: 1, BatchSize: 4, SmartReview: true);
        var plan = team.Capture(store.Units(), opts, settings);
        Assert.Equal(3, plan.Batches.Length);
        Assert.True(plan.UseSupervisor); // Triggered automatically when TotalUnits >= 12!

        bool sawLoreMinerTerms = false;
        bool batch2SawHistory = false;
        bool batch2SawPreviousLines = false;
        bool batch2SawUpcomingPreview = false;
        bool batch2SawLiveTermFromBatch1 = false;
        bool correctStageSawMultiTurnHistory = false;
        string? batch1SystemPrompt = null;
        string? batch2SystemPrompt = null;
        var reviewerSeenSources = new List<string>();

        var result = await team.Run(plan, opts, null, send: (req, _) =>
        {
            using var doc = JsonDocument.Parse(req.Data);
            var root = doc.RootElement;

            if (req.Role == "supervisor")
            {
                if (root.TryGetProperty("minedTerms", out var mt) && mt.GetRawText().Contains("Crimson Citadel", StringComparison.Ordinal))
                    sawLoreMinerTerms = true;
                var secIds = root.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList();
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    worldBrief = "عالم فانتازيا مظلم؛ إيلينا (Elena) قائدة أنثى تخاطب بصيغة المؤنث.",
                    glossary = new Dictionary<string, string>
                    {
                        ["Elena"] = "إيلينا",
                        ["Crimson Citadel"] = "القلعة القرمزية"
                    },
                    sections = secIds.ToDictionary(id => id, _ => new { worker = 1, guidance = "حافظ على نبرة إيلينا القيادية." })
                }));
            }

            var entries = root.GetProperty("entries").EnumerateArray().ToArray();
            string stage = root.GetProperty("stage").GetString()!;

            if (req.Role == "translator" && stage == "translate")
            {
                string firstSrc = entries[0].GetProperty("source").GetString()!;
                if (firstSrc.Contains("Welcome to Crimson Citadel", StringComparison.Ordinal))
                {
                    batch1SystemPrompt = req.Instructions;
                    var translations = entries.ToDictionary(
                        e => e.GetProperty("id").GetString()!,
                        e => e.GetProperty("source").GetString()! switch
                        {
                            var s when s.Contains("Welcome") => "إيلينا: مرحباً بك في القلعة القرمزية أيها المسافر!",
                            var s when s.Contains("Keep the") => "إيلينا: احفظ نواة الأثير داخل القلعة القرمزية.",
                            var s when s.Contains("spoken") => "إيلينا: هل تحدثت مع حراس القلعة القرمزية؟",
                            _ => "إيلينا: انتظر إشارتي عند البوابة."
                        });
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        translations,
                        newTerms = new Dictionary<string, string> { ["Aether Core"] = "نواة الأثير" }
                    }));
                }

                if (firstSrc == "Save")
                {
                    batch2SystemPrompt = req.Instructions;
                    batch2SawHistory = req.History is { Count: >= 2 };
                    batch2SawPreviousLines = root.TryGetProperty("previousLines", out var prev)
                        && prev.GetRawText().Contains("القلعة القرمزية", StringComparison.Ordinal);
                    batch2SawUpcomingPreview = root.TryGetProperty("upcomingLines", out var up)
                        && up.GetArrayLength() > 0;
                    batch2SawLiveTermFromBatch1 = root.TryGetProperty("glossary", out var gl)
                        && gl.GetRawText().Contains("نواة الأثير", StringComparison.Ordinal)
                        && gl.GetRawText().Contains("إيلينا", StringComparison.Ordinal);

                    var translations = entries.ToDictionary(
                        e => e.GetProperty("id").GetString()!,
                        e => e.GetProperty("source").GetString()! switch
                        {
                            "Save" => "حفظ",
                            "Options" => "خيارات",
                            "Exit" => "خروج",
                            _ => "إيلينا: أحضر نواة الأثير إلى المذبح الآن!"
                        });
                    return Task.FromResult(JsonSerializer.Serialize(translations));
                }

                // Batch 3: pure short UI + "DLSS" acronym
                return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(
                    e => e.GetProperty("id").GetString()!,
                    e => e.GetProperty("source").GetString()! switch
                    {
                        "DLSS" => "DLSS",
                        "Audio" => "الصوت",
                        "Graphics" => "الرسوميات",
                        _ => "التحكم"
                    })));
            }

            if (req.Role == "translator" && stage == "correct")
            {
                correctStageSawMultiTurnHistory = req.History is { Count: >= 2 };
                return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(
                    e => e.GetProperty("id").GetString()!,
                    _ => "إيلينا: أحضري نواة الأثير إلى المذبح حالاً!")));
            }

            // Reviewer (Smart QA Bot)
            foreach (var e in entries)
                reviewerSeenSources.Add(e.GetProperty("source").GetString()!);

            var verdicts = new Dictionary<string, object>();
            foreach (var e in entries)
            {
                string id = e.GetProperty("id").GetString()!;
                string src = e.GetProperty("source").GetString()!;
                if (src.Contains("Bring the Aether Core", StringComparison.Ordinal) && stage == "review")
                    verdicts[id] = new { ok = false, reason = "خاطب البطلة بصيغة المؤنث.", suggestion = "" };
                else
                    verdicts[id] = new { ok = true };
            }
            return Task.FromResult(JsonSerializer.Serialize(verdicts));
        }, approveReviewed: true);

        Assert.Equal(3, result.Completed);
        Assert.Equal(12, result.Saved);
        Assert.True(sawLoreMinerTerms);
        Assert.True(batch2SawHistory);
        Assert.True(batch2SawPreviousLines);
        Assert.True(batch2SawUpcomingPreview);
        Assert.True(batch2SawLiveTermFromBatch1);
        Assert.True(correctStageSawMultiTurnHistory);

        // Prompt-caching invariant: identical system instructions + worldBrief across batches of the run
        Assert.NotNull(batch1SystemPrompt);
        Assert.Contains("إيلينا", batch1SystemPrompt!);
        Assert.Equal(batch1SystemPrompt, batch2SystemPrompt);

        // Smart QA skipped clean short UI labels ("Save", "Options", "Exit", "Audio", "Graphics", "Controls")
        // while still reviewing narrative dialogue and the untranslated "DLSS" acronym!
        Assert.DoesNotContain("Save", reviewerSeenSources);
        Assert.DoesNotContain("Audio", reviewerSeenSources);
        Assert.Contains("DLSS", reviewerSeenSources);
        Assert.Equal("DLSS", store.Units().Single(u => u.Source == "DLSS").Target);
        Assert.Equal("approved", store.Units().Single(u => u.Source == "DLSS").Status);

        // Bot glossary persisted separately from manual glossary
        var botGlossary = store.BotGlossary();
        Assert.Equal("إيلينا", botGlossary["Elena"]);
        Assert.Equal("نواة الأثير", botGlossary["Aether Core"]);
        Assert.Empty(store.Glossary());
    }

    [Fact]
    public async Task PausedRunResumesWithPersistedWorldBriefBotGlossaryAndRollingContextTail()
    {
        using var ws = new TempWorkspace();
        string source = ws.PathOf("resume-story.json");
        var script = Enumerable.Range(1, 12).ToDictionary(
            i => $"k{i:00}",
            i => $"Elena: Line {i} inside Crimson Citadel.");
        File.WriteAllText(source, JsonSerializer.Serialize(script));

        string projPath = ws.PathOf("resume-hive");
        var opts = TestHelpers.ScopedOptions with { BatchSize = 6, MaxRequests = 2, MaxCharacters = 200_000 };

        using (var store = ProjectStore.Create(projPath, "ResumeHive", source))
        {
            new ProjectService(store).Scan();
            var team = new TranslationTeam(store);
            var plan = team.Capture(store.Units(), opts, new TeamSettings(Workers: 1, BatchSize: 6, FastMode: false, SmartReview: true));

            // Budget = 2 requests: Request #1 = Lore Bot, Request #2 = Batch #1 translate; pauses before Batch #1 QA / Batch #2!
            var partial = await team.Run(plan, opts with { MaxRequests = 3 }, null, send: (req, _) =>
            {
                using var doc = JsonDocument.Parse(req.Data);
                if (req.Role == "supervisor")
                {
                    var secIds = doc.RootElement.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList();
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        worldBrief = "بطاقة عالم محفوظة للاستئناف.",
                        glossary = new Dictionary<string, string> { ["Elena"] = "إيلينا", ["Crimson Citadel"] = "القلعة القرمزية" },
                        sections = secIds.ToDictionary(id => id, _ => new { worker = 1, guidance = "" })
                    }));
                }
                var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                if (req.Role == "reviewer")
                    return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(e => e.GetProperty("id").GetString()!, _ => new { ok = true })));
                return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(
                    e => e.GetProperty("id").GetString()!,
                    e => "إيلينا: ترجمة محفوظة في الدفعة الأولى داخل القلعة القرمزية.")));
            }, approveReviewed: true);

            Assert.Equal(1, partial.Completed); // Batch 1 finished (3 requests: Lore + Translate + QA), Batch 2 paused!
        }

        // Re-open ProjectStore cold from disk and resume Batch 2
        bool resumedSawWorldBrief = false;
        bool resumedSawPersistedTail = false;
        bool resumedSawBotGlossary = false;
        using (var reopened = new ProjectStore(projPath))
        {
            var team = new TranslationTeam(reopened);
            var latest = team.Latest!;
            var finished = await team.Run(latest, opts with { MaxRequests = 20 }, null, send: (req, _) =>
            {
                using var doc = JsonDocument.Parse(req.Data);
                var root = doc.RootElement;
                if (req.Role == "translator")
                {
                    resumedSawWorldBrief = req.Instructions.Contains("بطاقة عالم محفوظة للاستئناف", StringComparison.Ordinal);
                    resumedSawPersistedTail = root.TryGetProperty("previousLines", out var prev)
                        && prev.GetRawText().Contains("ترجمة محفوظة في الدفعة الأولى", StringComparison.Ordinal);
                    resumedSawBotGlossary = root.TryGetProperty("glossary", out var gl)
                        && gl.GetRawText().Contains("القلعة القرمزية", StringComparison.Ordinal);
                }
                var entries = root.GetProperty("entries").EnumerateArray().ToArray();
                if (req.Role == "reviewer")
                    return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(e => e.GetProperty("id").GetString()!, _ => new { ok = true })));
                return Task.FromResult(JsonSerializer.Serialize(entries.ToDictionary(
                    e => e.GetProperty("id").GetString()!,
                    _ => "إيلينا: ترجمة الدفعة الثانية داخل القلعة القرمزية.")));
            }, approveReviewed: true);

            Assert.Equal(2, finished.Completed);
            Assert.Equal(12, finished.Saved);
        }

        Assert.True(resumedSawWorldBrief);
        Assert.True(resumedSawPersistedTail);
        Assert.True(resumedSawBotGlossary);
    }
}
