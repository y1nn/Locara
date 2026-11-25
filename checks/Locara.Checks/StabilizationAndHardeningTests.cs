using System.Net;
using System.Text;
using System.Text.Json;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class StabilizationAndHardeningTests
{
    [Fact]
    public async Task CleanJsonPayloadAndProvidersHandleThinkTagsFencesMultiPartAndUnescapedQuotes()
    {
        // 1. Reasoning <think> block containing brackets/braces before the actual JSON object
        const string withThink = "<think>Check [tag] and {meta} before answering.</think>\n{\"1\":\"ابدأ\"}";
        Assert.Equal("{\"1\":\"ابدأ\"}", ApiProvider.CleanJsonPayload(withThink));

        // 2. Prose + uppercase JSON fence + trailing comma
        const string fencedWithProse = "Here is the translated JSON:\n```JSON\n{\n  \"1\": \"ابدأ\",\n}\n```\nAll done.";
        var parsedFenced = ApiProvider.ReadTranslations(fencedWithProse, ["1"]);
        Assert.Equal("ابدأ", parsedFenced["1"]);

        // 3. Unescaped inner double quotes inside Arabic JSON string values (common LLM failure in real logs)
        const string unescapedQuotes = "{\"1\":\"اضغط على \"ابدأ\" للمتابعة\",\"2\":\"إلغاء\"}";
        var parsedQuotes = ApiProvider.ReadTranslations(unescapedQuotes, ["1", "2"]);
        Assert.Equal("اضغط على \"ابدأ\" للمتابعة", parsedQuotes["1"]);
        Assert.Equal("إلغاء", parsedQuotes["2"]);

        // 4. Concatenated NDJSON objects ({"1":"..."}\n{"2":"..."})
        const string ndjson = "{\"1\":\"ابدأ\"}\n{\"2\":\"خروج\"}";
        var parsedNdjson = ApiProvider.ReadTranslations(ndjson, ["1", "2"]);
        Assert.Equal("ابدأ", parsedNdjson["1"]);
        Assert.Equal("خروج", parsedNdjson["2"]);

        // 5. Gemini multi-part text response (JSON split across two non-thought parts)
        var probeUnit = new Unit { Id = "u1", Source = "Start" };
        string multiPartGemini = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new object[]
                        {
                            new { thought = true, text = "Thinking about {u1}..." },
                            new { text = "{\"u1\":" },
                            new { text = "\"ابدأ\"}" }
                        }
                    }
                }
            }
        });
        var (geminiRes, _) = await TestHelpers.CallLoopback(200, multiPartGemini, url =>
            new ApiProvider(new("gemini", "gemini-2.5-flash", "k", url)).Translate([probeUnit], new Dictionary<string, string>(), default));
        Assert.Equal("ابدأ", geminiRes["u1"]);

        // 6. Gemini safety-blocked candidate returns clear Arabic InvalidDataException (not KeyNotFoundException)
        string safetyBlockedGemini = JsonSerializer.Serialize(new
        {
            candidates = new[] { new { finishReason = "SAFETY" } }
        });
        var blockedErr = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TestHelpers.CallLoopback(200, safetyBlockedGemini, url =>
                new ApiProvider(new("gemini", "gemini-2.5-flash", "k", url)).Translate([probeUnit], new Dictionary<string, string>(), default)));
        Assert.Contains("SAFETY", blockedErr.Message);

        // 7. LocalizationScout strips <think> blocks with braces cleanly
        var sampleCluster = new FileSchemaCluster(
            "json:quests#12345678", "json", "quests", 1, 2, 1, 1, FileQualityVerdict.Mixed, false,
            ["quests/q1.json"],
            [new FieldClusterStat("/*/title", "title", FieldSemanticHint.LikelyHumanText, 1, 1, 0, ["Quest"])],
            ["/*/title"]);
        string scoutRaw = "<think>Analyze {cluster} [fields]</think>\n{\"clusters\":[{\"clusterId\":\"json:quests#12345678\",\"action\":\"include_fields\",\"fieldPatterns\":[\"/*/title\"],\"confidence\":\"high\",\"reason\":\"عنوان المهمة\"}]}";
        var recs = LocalizationScout.ParseAndValidateRecommendations(scoutRaw, [sampleCluster]);
        Assert.Single(recs);
        Assert.Equal("include_fields", recs[0].Action);
    }

    [Fact]
    public async Task ReadTranslationsAndTeamSalvagePartialBatchesNumericIdsAndAltWrappers()
    {
        // 1. Numeric IDs in array + extra "source" field + alternative wrapper "translations"/"results"
        const string numericArray = """{"results":[{"id":1,"source":"Start","text":"ابدأ"},{"id":2,"translation":"خروج"}]}""";
        var fromNumArray = ApiProvider.ReadTranslations(numericArray, ["1", "2"]);
        Assert.Equal("ابدأ", fromNumArray["1"]);
        Assert.Equal("خروج", fromNumArray["2"]);

        // 2. Object-of-objects format + "translations" wrapper
        const string nestedObjects = """{"translations":{"1":{"translation":"ابدأ"},"2":{"target":"خروج"}}}""";
        var fromNested = ApiProvider.ReadTranslations(nestedObjects, ["1", "2"]);
        Assert.Equal("ابدأ", fromNested["1"]);
        Assert.Equal("خروج", fromNested["2"]);

        // 3. Partial-batch salvage in TranslationTeam:
        //    First translator response returns 2 of 3 items (+ an extra ignored commentary key).
        //    Team MUST salvage those 2 items and ONLY request the 1 missing item on the next call!
        //    Reviewer returns numeric IDs + string "true" under "reviews" wrapper.
        using var ws = new TempWorkspace();
        string source = ws.PathOf("partial.json");
        File.WriteAllText(source, """{"a":"One","b":"Two","c":"Three"}""");
        using var store = ProjectStore.Create(ws.PathOf("partial-proj"), "Partial", source);
        new ProjectService(store).Scan();

        var team = new TranslationTeam(store);
        var opts = TestHelpers.ScopedOptions with { BatchSize = 10, MaxRequests = 20 };
        var plan = team.Capture(store.Units(), opts, new TeamSettings(Workers: 1, BatchSize: 10));

        var translateRequestCounts = new List<int>();
        var outcome = await team.Run(plan, opts, null, send: (req, _) =>
        {
            using var doc = JsonDocument.Parse(req.Data);
            var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            if (req.Role == "translator")
            {
                translateRequestCounts.Add(entries.Length);
                if (translateRequestCounts.Count == 1)
                {
                    // Return only 2 of 3 + an extra commentary key
                    return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["1"] = "واحد",
                        ["2"] = "اثنان",
                        ["_note"] = "translated"
                    }));
                }
                // Second call should only ask for the single missing row!
                return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["1"] = "ثلاثة"
                }));
            }
            // Reviewer uses "reviews" wrapper, numeric id, and string boolean "true"
            var reviewItems = entries.Select(e => new { id = int.Parse(e.GetProperty("id").GetString()!), ok = "true" }).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new { reviews = reviewItems }));
        }, approveReviewed: true);

        Assert.Equal([3, 1], translateRequestCounts);
        Assert.Equal(3, outcome.Saved);
        Assert.All(store.Units(), u => Assert.Equal("approved", u.Status));
    }

    [Fact]
    public async Task TeamFatalWorkerErrorCancelsSiblingWorkersAndDropsCorruptedReviewerSuggestion()
    {
        // Part A: Fatal non-retryable error (HTTP 401) in one worker cancels sibling workers immediately
        using var ws = new TempWorkspace();
        string source = ws.PathOf("fatal.json");
        var entries = Enumerable.Range(1, 10).ToDictionary(i => $"k{i}", i => $"Line {i}");
        File.WriteAllText(source, JsonSerializer.Serialize(entries));

        using var store = ProjectStore.Create(ws.PathOf("fatal-proj"), "Fatal", source);
        new ProjectService(store).Scan();
        var team = new TranslationTeam(store);
        var opts = TestHelpers.ScopedOptions with { BatchSize = 1, MaxRequests = 100 };
        var plan = team.Capture(store.Units(), opts, new TeamSettings(Workers: 2, BatchSize: 1, FastMode: true));

        int totalCalls = 0;
        var siblingCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<HttpRequestException>(() => team.Run(plan, opts, null, send: async (req, ct) =>
        {
            int n = Interlocked.Increment(ref totalCalls);
            if (n == 1)
                throw new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                using var doc = JsonDocument.Parse(req.Data);
                string id = doc.RootElement.GetProperty("entries")[0].GetProperty("id").GetString()!;
                return JsonSerializer.Serialize(new Dictionary<string, string> { [id] = "سطر" });
            }
            catch (OperationCanceledException)
            {
                siblingCancelled.TrySetResult(true);
                throw;
            }
        }));

        Assert.True(await siblingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(totalCalls <= 2, $"Expected sibling worker to stop immediately, but saw {totalCalls} calls.");

        // Part B: Corrupted reviewer suggestion (drops protected token {0}) is NOT forwarded to translator
        string suggSource = ws.PathOf("sugg.json");
        File.WriteAllText(suggSource, """{"a":"Deal {0} damage"}""");
        using var suggStore = ProjectStore.Create(ws.PathOf("sugg-proj"), "Sugg", suggSource);
        new ProjectService(suggStore).Scan();
        var suggTeam = new TranslationTeam(suggStore);
        var suggPlan = suggTeam.Capture(suggStore.Units(), opts, new TeamSettings(Workers: 1, BatchSize: 5));

        string? receivedSuggestedOnCorrect = "UNSET";
        await suggTeam.Run(suggPlan, opts, null, send: (req, _) =>
        {
            using var doc = JsonDocument.Parse(req.Data);
            string stage = doc.RootElement.GetProperty("stage").GetString()!;
            var row = doc.RootElement.GetProperty("entries")[0];
            string id = row.GetProperty("id").GetString()!;
            if (stage == "translate")
                return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, string> { [id] = "ألحق __LOCARA_0__ ضرراً" }));
            if (stage == "review")
                return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [id] = new { ok = false, reason = "حسّن الصياغة", suggestion = "ألحق ضرراً كبيراً" } // Missing {0}!
                }));
            if (stage == "correct")
            {
                receivedSuggestedOnCorrect = row.TryGetProperty("suggested", out var sg) ? sg.GetString() : null;
                return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, string> { [id] = "يلحق __LOCARA_0__ ضرراً" }));
            }
            return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { ok = true } }));
        }, approveReviewed: true);

        Assert.Null(receivedSuggestedOnCorrect);
    }

    [Fact]
    public void TokensAndArabicShaperPreserveBbcodeTagsAlphaNestingAndPrintfSpecifiers()
    {
        // 1. Extended printf specifiers (%u, %x, %ld, %lld, %1$u)
        Assert.NotEmpty(Tokens.Validate("HP: %u / %ld", "الصحة: %u"));
        Assert.Empty(Tokens.Validate("HP: %u / %ld (0x%X)", "الصحة: %u / %ld (0x%X)"));

        // 2. <alpha=...> / </size> misnesting detection (observed in real user logs!)
        const string validAlpha = "<alpha=#FF>تم التفعيل</alpha>";
        const string brokenAlpha = "<alpha=#FF>تم التفعيل</size>";
        Assert.Empty(Tokens.Validate(validAlpha, validAlpha));
        Assert.NotEmpty(Tokens.Validate(validAlpha, brokenAlpha));

        if (!OperatingSystem.IsWindows()) return;

        // 3. BBCode tag pairs must NOT be reversed by ICU BiDi ([gold]...[/gold], NOT [/gold]...[gold])
        const string bbcode = "[gold]يلحق 15 ضرراً للأعداء[/gold]";
        string shapedBb = StarTruckerDisplay.Shape(bbcode);
        Assert.Equal("", Tokens.Validate(bbcode, shapedBb));
        Assert.True(shapedBb.IndexOf("[gold]", StringComparison.Ordinal) < shapedBb.IndexOf("[/gold]", StringComparison.Ordinal),
            $"BBCode tags were reversed: {shapedBb}");

        // 4. Long Arabic paragraph inside BBCode wraps cleanly, closing and reopening [gold] on each wrapped line
        const string longBbcode = "[gold]يلحق هذا الهجوم السريع خمسة عشر نقطة ضرر مباشرة بجميع الأعداء القريبين[/gold]";
        var wrappedLines = StarTruckerDisplay.WrapArabicParagraph(longBbcode, maxLineChars: 24);
        Assert.True(wrappedLines.Count >= 2);
        Assert.All(wrappedLines, line =>
        {
            Assert.StartsWith("[gold]", line);
            Assert.EndsWith("[/gold]", line);
        });
    }

    [Fact]
    public void JsonAndYamlFormatsHandleCommentsTrailingCommasBomAndNoOpPreservation()
    {
        // 1. JSON with comments, trailing commas, and UTF-8 BOM
        var json = new JsonFormat();
        const string jsonc = """
            {
              // Main menu strings
              "play": "Start Game",
              /* Exit button */
              "quit": "Exit",
            }
            """;
        byte[] bomJsonc = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(jsonc)];
        var jsonSlots = json.Extract(bomJsonc);
        Assert.Equal(2, jsonSlots.Count);
        Assert.Equal("Start Game", jsonSlots.Single(s => s.Locator == "/play").Source);

        byte[] updatedJson = json.Apply(bomJsonc, new Dictionary<string, string> { ["/play"] = "ابدأ اللعبة" });
        Assert.True(updatedJson.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("ابدأ اللعبة", json.Extract(updatedJson).Single(s => s.Locator == "/play").Source);

        // 2. YAML no-op preservation, BOM preservation, and non-empty Context
        var yaml = new YamlFormat();
        byte[] bomYaml = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("ui:\n  play: Start Game\n")];
        var yamlSlots = yaml.Extract(bomYaml);
        Assert.Single(yamlSlots);
        Assert.Equal("0/ui/play", yamlSlots[0].Context);
        Assert.Equal(bomYaml, yaml.Apply(bomYaml, new Dictionary<string, string>()));
        Assert.Equal(bomYaml, yaml.Apply(bomYaml, new Dictionary<string, string> { [yamlSlots[0].Locator] = "Start Game" }));

        byte[] updatedYaml = yaml.Apply(bomYaml, new Dictionary<string, string> { [yamlSlots[0].Locator] = "ابدأ اللعبة" });
        Assert.True(updatedYaml.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("ابدأ اللعبة", yaml.Extract(updatedYaml).Single().Source);
    }

    [Fact]
    public void DeploymentAdvisorNormalizesCapitalizedEngineNamesFromCapabilities()
    {
        var godotCap = new SourceCapability("Godot", "4.5.0", true, true, GameLaunch.Steam, "", "");
        var godotFiles = new List<FileQualitySummary>
        {
            new("game.pck | localization/en.json", "json", 50, 50, 0, 0, 50, 0, 0, FileQualityVerdict.Translatable, "", [], Included: true, IncludedUnits: 50)
        };
        var godotAdvice = DeploymentAdvisor.Analyze(godotCap, godotFiles);
        Assert.Equal("godot-pck-or-loose", godotAdvice.Strategy);
        Assert.Contains(godotAdvice.Notes, n => n.Contains("Godot"));

        var rpgCap = new SourceCapability("RPG Maker", "MZ", true, true, GameLaunch.None, "", "");
        var rpgFiles = new List<FileQualitySummary>
        {
            new("data/System.json", "json", 20, 20, 0, 0, 20, 0, 0, FileQualityVerdict.Translatable, "", [], Included: true, IncludedUnits: 20)
        };
        Assert.Equal("rpgmaker-data-json", DeploymentAdvisor.Analyze(rpgCap, rpgFiles).Strategy);

        var renpyCap = new SourceCapability("Ren’Py", "", true, true, GameLaunch.RenpyCopy, "", "");
        var renpyFiles = new List<FileQualitySummary>
        {
            new("game/tl/arabic/script.rpy", "renpy-template", 20, 20, 0, 0, 20, 0, 0, FileQualityVerdict.Translatable, "", [], Included: true, IncludedUnits: 20)
        };
        Assert.Equal("renpy-tl-arabic", DeploymentAdvisor.Analyze(renpyCap, renpyFiles).Strategy);
    }

    [Fact]
    public void PackageInstallerHandlesReadOnlyGameFilesAndReportsClearArabicLockedFileError()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("ro-game")).FullName;
        string targetFile = Path.Combine(gameDir, "ui.json");
        const string original = """{"play":"Play"}""";
        File.WriteAllText(targetFile, original);
        File.SetAttributes(targetFile, FileAttributes.ReadOnly);

        try
        {
            using var store = ProjectStore.Create(ws.PathOf("ro-proj"), "ReadOnlyGame", gameDir);
            var svc = new ProjectService(store);
            svc.Scan();
            var unit = store.Units().Single();
            unit.Target = "ابدأ";
            store.Save(unit, approve: true);

            // Deploy & Restore must both succeed even when the original game file has ReadOnly attribute
            var deployed = PackageInstaller.DeployInPlace(store, ws.PathOf("ro-out"), final: true);
            Assert.Equal(1, deployed.Install.Files);
            Assert.Contains("ابدأ", File.ReadAllText(targetFile));

            // Make localized file read-only too and verify restore succeeds cleanly
            File.SetAttributes(targetFile, FileAttributes.ReadOnly);
            Assert.Equal(1, PackageInstaller.RestoreInPlace(store));
            Assert.Equal(original, File.ReadAllText(targetFile));

            // Locked file error must be clear Arabic
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(targetFile, FileAttributes.Normal);
                using var lockStream = new FileStream(targetFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var lockErr = Assert.Throws<IOException>(() =>
                    PackageInstaller.DeployInPlace(store, ws.PathOf("ro-out-2"), final: true));
                Assert.Contains("قيد الاستخدام", lockErr.Message);
            }
        }
        finally
        {
            if (File.Exists(targetFile))
                File.SetAttributes(targetFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void ProjectServiceGetGettextPoStatusMatchesActivePotAndSiblingPoFiles()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("po-game")).FullName;
        Directory.CreateDirectory(Path.Combine(gameDir, "locale"));
        File.WriteAllText(Path.Combine(gameDir, "locale", "messages.pot"), """
            msgid ""
            msgstr ""

            msgid "Start Game"
            msgstr ""
            """);
        File.WriteAllText(Path.Combine(gameDir, "locale", "fr.po"), """
            msgid ""
            msgstr ""

            msgid "Start Game"
            msgstr "Commencer"
            """);
        File.WriteAllText(Path.Combine(gameDir, "locale", "de.po"), """
            msgid ""
            msgstr ""

            msgid "Start Game"
            msgstr "Spiel starten"
            """);

        using var store = ProjectStore.Create(ws.PathOf("po-proj"), "PoGame", gameDir);
        var svc = new ProjectService(store);
        svc.Scan();

        var (hasActivePot, siblings) = svc.GetGettextPoStatus();
        Assert.True(hasActivePot);
        Assert.Equal(2, siblings.Count);
        Assert.Contains(siblings, p => p.EndsWith("fr.po", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(siblings, p => p.EndsWith("de.po", StringComparison.OrdinalIgnoreCase));

        // Disabling the .pot file must immediately report HasActivePot = false
        svc.SetFileIncluded("locale/messages.pot", included: false);
        var (afterDisable, siblingsAfterDisable) = svc.GetGettextPoStatus();
        Assert.False(afterDisable);
        Assert.Empty(siblingsAfterDisable);
    }
}
