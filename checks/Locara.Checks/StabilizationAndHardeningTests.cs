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
        var siblingInFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<HttpRequestException>(() => team.Run(plan, opts, null, send: async (req, ct) =>
        {
            int n = Interlocked.Increment(ref totalCalls);
            if (n == 1)
            {
                // Ensure the sibling worker is genuinely in-flight before raising the fatal 401
                await siblingInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                throw new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
            }
            siblingInFlight.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
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

        Assert.True(await siblingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
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

    [Fact]
    public void ScanSurvivesInvalidUtf8JsonAndExtractsDedicatedLocaleTranslations()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("bad-utf8-game")).FullName;
        Directory.CreateDirectory(Path.Combine(gameDir, "roadData"));
        Directory.CreateDirectory(Path.Combine(gameDir, "locales", "translations", "en-US"));
        Directory.CreateDirectory(Path.Combine(gameDir, "locales", "translations", "pl_PL"));
        Directory.CreateDirectory(Path.Combine(gameDir, "data"));

        // Invalid UTF-8 byte (0xA3 Latin-1 '£') inside a JSON string — System.Text.Json throws InvalidOperationException
        byte[] badJson = [.. System.Text.Encoding.ASCII.GetBytes("{\"note\":\"price "), 0xA3, .. System.Text.Encoding.ASCII.GetBytes("10\"}")];
        File.WriteAllBytes(Path.Combine(gameDir, "roadData", "broken_utf8.json"), badJson);

        // 25 pure English UI strings in locales/translations/en-US/
        var enEntries = Enumerable.Range(1, 25).Select(i => $"  \"ui.item.{i}\": \"Start mission number {i} now\"");
        string enJson = "{\n" + string.Join(",\n", enEntries) + "\n}\n";
        File.WriteAllText(Path.Combine(gameDir, "locales", "translations", "en-US", "main.translation.json"), enJson);

        // Secondary locale pl_PL (BCP-47 pair not manually hardcoded in KnownLocaleCodes)
        File.WriteAllText(Path.Combine(gameDir, "locales", "translations", "pl_PL", "main.translation.json"),
            """{"ui.item.1":"Rozpocznij misję"}""");

        // Unrelated human-looking sidecar JSON outside locales/
        File.WriteAllText(Path.Combine(gameDir, "data", "extra_info.json"),
            """{"title":"Vehicle Engine Spec","desc":"Standard turbocharged configuration"}""");

        // Direct JsonFormat check: invalid UTF-8 throws InvalidDataException (not unhandled InvalidOperationException)
        Assert.Throws<InvalidDataException>(() => new JsonFormat().Extract(badJson));

        // 1. Even when invalid UTF-8 JSON sits inside locales/ (Pass 1), Scan must survive and record diagnostic without crashing
        File.WriteAllBytes(Path.Combine(gameDir, "locales", "translations", "en-US", "broken_utf8.json"), badJson);

        using var store = ProjectStore.Create(ws.PathOf("bad-utf8-proj"), "BadUtf8Game", gameDir);
        var svc = new ProjectService(store);
        var scan = svc.Scan();

        // Scan must succeed (not throw) and extract all 25 English locale strings
        Assert.Equal(25, scan.Units);
        Assert.Contains(scan.Diagnostics, d => d.Path.Contains("broken_utf8.json", StringComparison.OrdinalIgnoreCase));

        var files = svc.ScannedFiles();
        var enFile = files.Single(f => f.Path.EndsWith("en-US/main.translation.json", StringComparison.OrdinalIgnoreCase));
        Assert.True(enFile.Included);
        Assert.Equal(25, enFile.IncludedUnits);

        var plFile = files.Single(f => f.Path.EndsWith("pl_PL/main.translation.json", StringComparison.OrdinalIgnoreCase));
        Assert.False(plFile.Included);
        Assert.Equal(FileQualityVerdict.SecondaryLanguage, plFile.Verdict);

        // Auxiliary sidecar outside locales/ is lazy-skipped (0 units initially) and extracts on demand when enabled!
        var sidecar = files.Single(f => f.Path.EndsWith("data/extra_info.json", StringComparison.OrdinalIgnoreCase));
        Assert.False(sidecar.Included);
        Assert.Equal(FileQualityVerdict.AuxiliaryData, sidecar.Verdict);
        Assert.Equal(0, sidecar.TotalUnits);

        svc.SetFileIncluded(sidecar.Path, included: true);
        var sidecarAfter = svc.ScannedFiles().Single(f => f.Path == sidecar.Path);
        Assert.True(sidecarAfter.Included);
        Assert.Equal(2, sidecarAfter.TotalUnits);
    }

    [Fact]
    public void LocalizationTextFormatExtractsAndRoundTripsKeyValueAndLineByLineTxtFiles()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("unity-txt-game")).FullName;
        string dataDir = Directory.CreateDirectory(Path.Combine(gameDir, "Sample_Data")).FullName;
        Directory.CreateDirectory(Path.Combine(dataDir, "Localisation Files"));
        Directory.CreateDirectory(Path.Combine(dataDir, "Mono", "etc", "mono"));
        File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), [0, 0, 0, 0]);

        // Mono runtime internal INI with duplicate keys must be ignored cleanly
        File.WriteAllText(Path.Combine(dataDir, "Mono", "etc", "mono", "browscap.ini"), "[sec]\na=1\na=2\n");
        // Non-localization .txt outside localization directory must be ignored
        File.WriteAllText(Path.Combine(dataDir, "CompletedLevels.txt"), "1|1|0|1\n");
        // README inside localization folder must be ignored
        File.WriteAllText(Path.Combine(dataDir, "Localisation Files", "README.txt"), "Instructions for translators\n");

        // Key-Value quoted .txt with BOM, // comments, and [Section] headers
        string kvContent =
            "// Language File\r\n" +
            "[Game Language File]\r\n" +
            "languageName = \"English\"\r\n" +
            "[Begin Translations]\r\n" +
            "// Play Button\r\n" +
            "1 = \"Start New Adventure\"\r\n" +
            "// Escaped quote and newline\r\n" +
            "2 = \"Press \\\"Enter\\\"\\nTo Continue\"\r\n";
        byte[] bomUtf8 = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(kvContent)];
        File.WriteAllBytes(Path.Combine(dataDir, "Localisation Files", "English.txt"), bomUtf8);
        File.WriteAllText(Path.Combine(dataDir, "Localisation Files", "ChineseSimplified.txt"), "1 = \"开始\"\r\n");

        using var store = ProjectStore.Create(ws.PathOf("unity-txt-proj"), "UnityTxtGame", gameDir);
        var svc = new ProjectService(store);
        var scan = svc.Scan();

        // Zero errors from Mono/etc/mono/browscap.ini; only English.txt active
        Assert.DoesNotContain(scan.Diagnostics, d => d.Path.Contains("browscap.ini", StringComparison.OrdinalIgnoreCase));
        var files = svc.ScannedFiles();
        Assert.DoesNotContain(files, f => f.Path.Contains("CompletedLevels.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Path.Contains("README.txt", StringComparison.OrdinalIgnoreCase));

        var enRow = files.Single(f => f.Path.EndsWith("English.txt", StringComparison.OrdinalIgnoreCase));
        Assert.True(enRow.Included);
        Assert.Equal("loc-txt", enRow.Format);

        var zhRow = files.Single(f => f.Path.EndsWith("ChineseSimplified.txt", StringComparison.OrdinalIgnoreCase));
        Assert.False(zhRow.Included);
        Assert.Equal(FileQualityVerdict.SecondaryLanguage, zhRow.Verdict);

        // Verify round-trip preserves BOM, comments, sections, quotes, and \r\n
        var slots = LocalizationTextFormat.Instance.Extract(bomUtf8);
        Assert.Contains(slots, s => s.Source == "Start New Adventure" && s.Context.Contains("Play Button"));
        var targetSlot = slots.Single(s => s.Source == "Start New Adventure");
        byte[] rewritten = LocalizationTextFormat.Instance.Apply(bomUtf8, new Dictionary<string, string>
        {
            [targetSlot.Locator] = "ابدأ مغامرة \"جديدة\""
        });
        Assert.Equal(0xEF, rewritten[0]);
        Assert.Equal(0xBB, rewritten[1]);
        Assert.Equal(0xBF, rewritten[2]);
        string rewrittenText = System.Text.Encoding.UTF8.GetString(rewritten, 3, rewritten.Length - 3);
        Assert.Contains("// Play Button\r\n1 = \"ابدأ مغامرة \\\"جديدة\\\"\"\r\n", rewrittenText);
        Assert.Contains("[Begin Translations]\r\n", rewrittenText);

        // Also test plain line-by-line Languages/sample.txt format
        byte[] lineBytes = System.Text.Encoding.UTF8.GetBytes("English\r\nContinue\r\n\r\nLoad Save?\r\n");
        var lineSlots = LocalizationTextFormat.Instance.Extract(lineBytes);
        Assert.Equal(3, lineSlots.Count);
        Assert.Equal("Continue", lineSlots[1].Source);
        byte[] lineRewritten = LocalizationTextFormat.Instance.Apply(lineBytes, new Dictionary<string, string>
        {
            [lineSlots[1].Locator] = "متابعة"
        });
        Assert.Equal("English\r\nمتابعة\r\n\r\nLoad Save?\r\n", System.Text.Encoding.UTF8.GetString(lineRewritten));
    }

    [Fact]
    public void ScanSurfacesEncryptedUnrealPakDiagnosticEvenWhenUnrelatedIniFails()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("ue-enc-game")).FullName;
        Directory.CreateDirectory(Path.Combine(gameDir, "MyGame", "Config"));
        Directory.CreateDirectory(Path.Combine(gameDir, "MyGame", "Content", "Paks"));

        // Unreal DefaultEngine.ini with repeated keys
        File.WriteAllText(Path.Combine(gameDir, "MyGame", "Config", "DefaultEngine.ini"),
            "[/Script/Engine.Engine]\n+ActiveGameNameRedirects=A\n+ActiveGameNameRedirects=B\n");

        // Minimal valid Unreal v11 .pak footer with bEncryptedIndex = 1
        // v11 footer layout (204 bytes): 16-byte EncryptionKeyGuid + 1-byte bEncryptedIndex + 4-byte Magic + 4-byte Version + 8-byte IndexOffset + 8-byte IndexSize + 20-byte IndexHash + 1-byte Frozen + 160-byte CompressionMethods (5x32)
        // Wait: standard v11 footer size is 221 bytes (16 + 1 + 4 + 4 + 8 + 8 + 20 + 160)
        byte[] fakePak = new byte[256];
        int footerStart = fakePak.Length - 221;
        fakePak[footerStart + 16] = 1; // bEncryptedIndex = true
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fakePak.AsSpan(footerStart + 17), 0x5A6F12E1u);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(fakePak.AsSpan(footerStart + 21), 11);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(fakePak.AsSpan(footerStart + 25), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(fakePak.AsSpan(footerStart + 33), 32);
        File.WriteAllBytes(Path.Combine(gameDir, "MyGame", "Content", "Paks", "pakchunk0-WindowsNoEditor.pak"), fakePak);

        using var store = ProjectStore.Create(ws.PathOf("ue-enc-proj"), "UeEncGame", gameDir);
        var scan = new ProjectService(store).Scan();

        Assert.Equal(0, scan.Units);
        Assert.Contains(scan.Diagnostics, d => d.Message.Contains("AES", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClassicUnityLocalizationTextShapesArabicWithoutTmpAndProtectsTechnicalMetadataKeys()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("classic-unity-game")).FullName;
        string dataDir = Directory.CreateDirectory(Path.Combine(gameDir, "ClassicGame_Data")).FullName;
        string locDir = Directory.CreateDirectory(Path.Combine(dataDir, "Localisation Files")).FullName;
        File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), [0, 0, 0, 0]);

        string content =
            "[Game Language File]\r\n" +
            "systemLanguage = \"English\"\r\n" +
            "lang-code = \"en\"\r\n" +
            "languageName = \"English\"\r\n" +
            "[Begin Translations]\r\n" +
            "Culture = \"Culture\"\r\n" +
            "// Options Header\r\n" +
            "1 = \"O P T I O N S\"\r\n" +
            "// VSync Row\r\n" +
            "2 = \"Vertical Sync (VSYNC)\"\r\n" +
            "// Long Crossplay Notice\r\n" +
            "3 = \"Could not join match on this platform! Please ask the host to turn the crossplay setting off in options.\"\r\n";
        File.WriteAllText(Path.Combine(locDir, "English.txt"), content, System.Text.Encoding.UTF8);

        // Also include a loose INI localization file and a LineByLine .txt file to verify single-line shaping safety
        File.WriteAllText(Path.Combine(locDir, "Tooltips.ini"),
            "[UI]\nlong_tip=This tooltip explains how mechanical joints react under heavy structural load during storms.\n",
            System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(locDir, "Credits.txt"),
            "Lead Engineering Team\nSpecial thanks to everyone who tested physics stability across every island region.\n",
            System.Text.Encoding.UTF8);
        // Note: ExcludedDocNames excludes "credits", so name the line-by-line localization file "Dialogs.txt"
        File.Delete(Path.Combine(locDir, "Credits.txt"));
        File.WriteAllText(Path.Combine(locDir, "Dialogs.txt"),
            "Welcome Traveler\nSpecial thanks to everyone who tested physics stability across every island region.\n",
            System.Text.Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("classic-unity-proj"), "ClassicUnity", gameDir);
        var svc = new ProjectService(store);
        svc.Scan();

        // 1. Technical header keys (systemLanguage, lang-code) must never be extracted, while gameplay "Culture" & UI "roomCode" stay safe
        var scannedUnits = store.Units();
        Assert.DoesNotContain(scannedUnits, u => u.Locator.Contains("systemLanguage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(scannedUnits, u => u.Locator.Contains("lang-code", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(scannedUnits, u => u.Source == "Culture");
        Assert.Equal(FieldSemanticHint.LikelyTechnical, FieldSemantics.ClassifyFieldKey("[UI/Menu]systemLanguage#2"));
        Assert.Equal(FieldSemanticHint.LikelyTechnical, FieldSemantics.ClassifyFieldKey("localeCode"));
        Assert.Equal(FieldSemanticHint.Neutral, FieldSemantics.ClassifyFieldKey("[Begin Translations]3"));
        Assert.Equal(FieldSemanticHint.Neutral, FieldSemantics.ClassifyFieldKey("roomCode"));
        Assert.Equal(FieldSemanticHint.Neutral, FieldSemantics.ClassifyFieldKey("decode"));

        // Simulate legacy project DB that already stored an approved translation for [Game Language File]systemLanguage before this fix
        store.Execute("""
            INSERT INTO units(id,path,locator,source,target,context,status,warning,revision,imported,active)
            VALUES('legacy-syslang','ClassicGame_Data/Localisation Files/English.txt','[Game Language File]systemLanguage','English','Arabic','header','approved','',1,'',1)
            """);

        var opt = store.Units("O P T I O N S").Single();
        opt.Target = "الخيارات";
        store.Save(opt, approve: true);

        var vsync = store.Units("Vertical Sync (VSYNC)").Single();
        vsync.Target = "المزامنة الرأسية (VSYNC)";
        store.Save(vsync, approve: true);

        var longMsg = store.Units().Single(u => u.Locator == "[Begin Translations]3");
        longMsg.Target = "تعذّر الانضمام للمباراة على هذا النظام! يرجى الطلب من المضيف إيقاف خيار اللعب المشترك من الإعدادات.";
        store.Save(longMsg, approve: true);

        const string longArabicSingleLine = "يوضح هذا التلميح كيف تتفاعل المفاصل الميكانيكية تحت الأحمال الثقيلة أثناء العواصف القوية جداً.";
        var iniUnit = store.Units().Single(u => u.Path.EndsWith("Tooltips.ini", StringComparison.Ordinal));
        iniUnit.Target = longArabicSingleLine;
        store.Save(iniUnit, approve: true);

        var lineByLineUnit = store.Units().Single(u => u.Path.EndsWith("Dialogs.txt", StringComparison.Ordinal) && u.Locator == "line:2");
        lineByLineUnit.Target = longArabicSingleLine;
        store.Save(lineByLineUnit, approve: true);

        // 2. Default ShapeArabic = true must shape Arabic into Presentation Forms-B even without TMP or loose .ttf
        var preview = svc.Preview(final: true);
        Assert.Equal(3, preview.ChangedFiles.Count);
        var built = svc.Build(ws.PathOf("classic-unity-out"), final: true, reviewed: preview);
        string rebuilt = File.ReadAllText(
            Path.Combine(built.Directory, "resources/ClassicGame_Data/Localisation Files/English.txt"),
            System.Text.Encoding.UTF8);

        // systemLanguage must stay "English" even when legacy DB had an approved "Arabic" row
        Assert.Contains("systemLanguage = \"English\"", rebuilt);
        Assert.DoesNotContain("systemLanguage = \"Arabic\"", rebuilt);

        // Arabic text must be visually shaped (Presentation Forms-B), NOT raw logical U+06xx
        Assert.Contains($"1 = \"{ArabicTextEngine.Shape("الخيارات")}\"", rebuilt);
        // 24-char single-line UI menu label must NOT have a forced \n line break injected!
        Assert.Contains($"2 = \"{ArabicTextEngine.Shape("المزامنة الرأسية (VSYNC)")}\"", rebuilt);
        // >75-char multi-sentence quoted message wraps cleanly with escaped \n inside the quoted value
        Assert.Contains("\\n", rebuilt.Split('\n').Single(l => l.StartsWith("3 = ", StringComparison.Ordinal)));

        // INI and LineByLine .txt must keep exact single-line shaping without crashing or reversing clause order!
        string rebuiltIni = File.ReadAllText(
            Path.Combine(built.Directory, "resources/ClassicGame_Data/Localisation Files/Tooltips.ini"),
            System.Text.Encoding.UTF8);
        Assert.Contains(ArabicTextEngine.Shape(longArabicSingleLine), rebuiltIni);
        string rebuiltDialogs = File.ReadAllText(
            Path.Combine(built.Directory, "resources/ClassicGame_Data/Localisation Files/Dialogs.txt"),
            System.Text.Encoding.UTF8);
        Assert.Contains(ArabicTextEngine.Shape(longArabicSingleLine), rebuiltDialogs);

        // 3. Disabling shaping (unity_arabic_shaping = 0) writes logical UTF-8 Arabic and updates fingerprint
        store.Set("unity_arabic_shaping", "0");
        var rtlPreview = svc.Preview(final: true);
        Assert.NotEqual(preview.Fingerprint, rtlPreview.Fingerprint);
        var rtlBuilt = svc.Build(ws.PathOf("classic-unity-rtl-out"), final: true, reviewed: rtlPreview);
        string rtlRebuilt = File.ReadAllText(
            Path.Combine(rtlBuilt.Directory, "resources/ClassicGame_Data/Localisation Files/English.txt"),
            System.Text.Encoding.UTF8);
        Assert.Contains("1 = \"الخيارات\"", rtlRebuilt);
        Assert.Contains("systemLanguage = \"English\"", rtlRebuilt);
    }

    [Fact]
    public void MutatedOrDeletedUntranslatedSidecarConfigDoesNotBlockBuildWhileTranslatedFileStillGuardsHash()
    {
        using var ws = new TempWorkspace();
        string gameDir = Directory.CreateDirectory(ws.PathOf("sidecar-config-game")).FullName;
        string dataDir = Directory.CreateDirectory(Path.Combine(gameDir, "ClassicGame_Data")).FullName;
        string locDir = Directory.CreateDirectory(Path.Combine(dataDir, "Localisation Files")).FullName;
        File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), [0, 0, 0, 0]);

        string enPath = Path.Combine(locDir, "English.txt");
        string frPath = Path.Combine(locDir, "French.txt");
        string configXml = Path.Combine(dataDir, "Config.xml");
        string controlsXml = Path.Combine(dataDir, "Controls.xml");

        string enOriginal = string.Join("\r\n", Enumerable.Range(1, 10).Select(i => $"{i} = \"Menu Option Number {i}\"")) + "\r\n";
        File.WriteAllText(enPath, enOriginal, System.Text.Encoding.UTF8);
        File.WriteAllText(frPath, "1 = \"Option 1\"\r\n", System.Text.Encoding.UTF8);
        File.WriteAllText(configXml, "<Config><Language>English</Language><Volume>100</Volume></Config>", System.Text.Encoding.UTF8);
        File.WriteAllText(controlsXml, "<Controls><Jump>Space</Jump></Controls>", System.Text.Encoding.UTF8);

        using var store = ProjectStore.Create(ws.PathOf("sidecar-proj"), "SidecarGame", gameDir);
        var svc = new ProjectService(store);
        svc.Scan();

        var first = store.Units().Single(u => u.Source == "Menu Option Number 1");
        first.Target = "خيار القائمة الأول";
        store.Save(first, approve: true);

        // Simulate launching the game: game rewrites Config.xml and removes temp Controls.xml
        const string updatedConfig = "<Config><Language>Custom</Language><Volume>55</Volume></Config>";
        File.WriteAllText(configXml, updatedConfig, System.Text.Encoding.UTF8);
        File.Delete(controlsXml);

        // 1. Preview, full-tree Build (overlayOnly: false), and DeployInPlace must all succeed without false hash error!
        var preview = svc.Preview(final: true);
        Assert.Equal(["ClassicGame_Data/Localisation Files/English.txt"], preview.ChangedFiles);

        var fullBuild = svc.Build(ws.PathOf("sidecar-full-out"), final: true, reviewed: preview, overlayOnly: false);
        Assert.True(File.Exists(Path.Combine(fullBuild.Directory, "resources/ClassicGame_Data/Config.xml")));

        var deployed = PackageInstaller.DeployInPlace(store, ws.PathOf("sidecar-deploy-out"), final: true);
        Assert.Equal(1, deployed.Install.Files);
        Assert.Equal(updatedConfig, File.ReadAllText(configXml, System.Text.Encoding.UTF8));

        PackageInstaller.RestoreInPlace(store);
        Assert.Equal(enOriginal, File.ReadAllText(enPath, System.Text.Encoding.UTF8));
        Assert.Equal(updatedConfig, File.ReadAllText(configXml, System.Text.Encoding.UTF8));

        // 2. Modifying the ACTUALLY translated localization file on disk MUST still throw hash mismatch!
        File.WriteAllText(enPath, enOriginal + "11 = \"External Patch Line\"\r\n", System.Text.Encoding.UTF8);
        var mismatch = Assert.Throws<InvalidOperationException>(() => svc.Preview(final: true));
        Assert.Contains("تغير المورد منذ الفحص", mismatch.Message);
        Assert.Contains("English.txt", mismatch.Message);
    }
}
