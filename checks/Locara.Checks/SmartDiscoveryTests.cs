using System.IO.Compression;
using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class SmartDiscoveryTests
{
    [Fact]
    public void FieldSemanticsClassifiesKeysAndNormalizesPatternsAcrossFormats()
    {
        Assert.Equal(FieldSemanticHint.LikelyHumanText, FieldSemantics.ClassifyFieldKey("/0/items/4/description"));
        Assert.Equal(FieldSemanticHint.LikelyHumanText, FieldSemantics.ClassifyFieldKey("0/quests/2/questTitle"));
        Assert.Equal(FieldSemanticHint.LikelyTechnical, FieldSemantics.ClassifyFieldKey("0/layout"));
        Assert.Equal(FieldSemanticHint.LikelyTechnical, FieldSemantics.ClassifyFieldKey("/items/0/prefabName"));
        Assert.Equal(FieldSemanticHint.LikelyTechnical, FieldSemantics.ClassifyFieldKey("/0/element"));
        Assert.Equal(FieldSemanticHint.LikelyTechnical, FieldSemantics.ClassifyFieldKey("/0/style"));

        Assert.Equal("/*/items/*/description", FieldSemantics.NormalizeLocatorPattern("/0/items/12/description"));
        Assert.Equal("*/elements/*/name", FieldSemantics.NormalizeLocatorPattern("0/elements/5/name"));
        Assert.Equal("root[*]/entry[*]/text[*]", FieldSemantics.NormalizeLocatorPattern("root[0]/entry[4]/text[0]"));

        Assert.True(FieldSemantics.MatchesPattern("/7/items/99/description", "/*/items/*/description"));
        Assert.True(FieldSemantics.MatchesPattern("/7/items/99/description", "description"));
        Assert.False(FieldSemantics.MatchesPattern("/7/items/99/prefab", "/*/items/*/description"));

        // Value + Key combined classification on open data formats vs dedicated localization formats
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyEntry("0/layout", "Vertical", "yaml"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyEntry("0/style", "Title", "yaml"));
        Assert.Equal(StringQuality.Technical, TextQualityClassifier.ClassifyEntry("0/element", "Oxygen", "yaml"));
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyEntry("0/description", "Vertical shaft built for rapid descent.", "yaml"));
        // Even under a technical key, an unmistakable multi-word sentence is preserved as Human
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyEntry("0/mode", "Press E to switch camera mode.", "json"));
        // Dedicated localization formats (like PO) must NOT reject entries based on locator key names
        Assert.Equal(StringQuality.Human, TextQualityClassifier.ClassifyEntry("style", "Title", "po"));
    }

    [Fact]
    public async Task SchemaClustererAndAiScoutValidateAntiHallucinationAndApplyFieldPatternsAcrossCluster()
    {
        using var ws = new TempWorkspace();
        string game = Directory.CreateDirectory(ws.PathOf("scout-game")).FullName;
        Directory.CreateDirectory(Path.Combine(game, "quests"));
        Directory.CreateDirectory(Path.Combine(game, "worldgen"));

        for (int i = 1; i <= 3; i++)
        {
            File.WriteAllText(Path.Combine(game, "quests", $"quest_{i}.json"), $$"""
                [
                  {
                    "id": "QUEST_0{{i}}",
                    "prefab": "Assets/Quests/Q{{i}}.prefab",
                    "layout": "Vertical",
                    "style": "Title",
                    "title": "Journey Beyond Sector {{i}}",
                    "summary": "Explore the ancient ruins and report back to the outpost."
                  }
                ]
                """);
        }
        File.WriteAllText(Path.Combine(game, "worldgen", "biomes.yaml"),
            "element: Oxygen\nlayout: Vertical\nstyle: Solid\nbiome: IgneousRock\n");

        using var store = ProjectStore.Create(ws.PathOf("scout-project"), "ScoutGame", game);
        var service = new ProjectService(store);
        var initialScan = service.Scan();
        // Both quests/*.json (Mixed: 4 technical, 2 human) and worldgen/biomes.yaml (Technical) are auto-excluded initially
        Assert.Equal(0, initialScan.Units);

        var clusters = service.BuildSchemaClusters();
        var questCluster = clusters.Single(c => c.DirectoryGroup == "quests" && c.Format == "json");
        Assert.Equal(3, questCluster.FileCount);
        Assert.Equal(18, questCluster.TotalUnits);
        Assert.Contains(questCluster.Fields, f => f.Pattern == "/*/title" && f.SemanticHint == FieldSemanticHint.LikelyHumanText);
        Assert.Contains(questCluster.Fields, f => f.Pattern == "/*/layout" && f.SemanticHint == FieldSemanticHint.LikelyTechnical);

        var worldCluster = clusters.Single(c => c.DirectoryGroup == "worldgen" && c.Format == "yaml");
        Assert.Equal(FileQualityVerdict.Technical, worldCluster.DominantVerdict);

        // Mock AI Scout returns valid recommendations + deliberately injected hallucinated cluster & field pattern
        string mockResponse = $$"""
            ```json
            {
              "clusters": [
                {
                  "clusterId": "{{questCluster.ClusterId}}",
                  "action": "include_fields",
                  "fieldPatterns": ["/*/title", "/*/summary", "/*/hallucinated_field"],
                  "confidence": "high",
                  "reason": "ملفات مهام تحتوي عنواناً وملخصاً للاعب بجانب حقول إعدادات."
                },
                {
                  "clusterId": "{{worldCluster.ClusterId}}",
                  "action": "exclude",
                  "fieldPatterns": [],
                  "confidence": "high",
                  "reason": "إعدادات توليد عالم داخلية."
                },
                {
                  "clusterId": "json:fake/hallucinated#00000000",
                  "action": "include_all",
                  "fieldPatterns": [],
                  "confidence": "high",
                  "reason": "يجب تجاهله بالكامل."
                }
              ]
            }
            ```
            """;

        var opts = new ProviderOptions("gemini", "gemini-2.5-flash", "test-key");
        var plan = await LocalizationScout.AnalyzeAsync(store, opts, CancellationToken.None, includeTranslatable: true,
            send: (_, data, _) =>
            {
                // Payload must stay compact and include normalized schema fields
                Assert.Contains(questCluster.ClusterId, data);
                Assert.Contains("/*/title", data);
                return Task.FromResult(new StructuredResponse(mockResponse, new TokenUsage(420, 110, 530)));
            });

        // Anti-hallucination: fake cluster rejected, fake fieldPattern stripped
        Assert.DoesNotContain(plan.Recommendations, r => r.ClusterId.Contains("fake"));
        var questRec = plan.Recommendations.Single(r => r.ClusterId == questCluster.ClusterId);
        Assert.Equal("include_fields", questRec.Action);
        Assert.Equal(["/*/title", "/*/summary"], questRec.FieldPatterns);

        // Token usage recorded in usage ledger
        var ledger = store.ProjectUsage();
        Assert.Equal(530, ledger.Sum(e => e.TotalTokens));

        // Apply plan -> extracts ONLY title & summary across all 3 quest files (6 units total), zero technical IDs!
        var applied = service.ApplyScoutPlan(plan);
        Assert.Equal(2, applied.AppliedClusters);
        Assert.Equal(3, applied.ConfiguredRuleFiles);
        Assert.Equal(6, applied.ActiveUnitsAfterScan);

        var activeUnits = store.Units();
        Assert.Equal(6, activeUnits.Count);
        Assert.All(activeUnits, u => Assert.True(u.Locator.EndsWith("/title") || u.Locator.EndsWith("/summary")));
        Assert.DoesNotContain(activeUnits, u => u.Source.Contains("QUEST_0") || u.Source == "Vertical" || u.Source.Contains(".prefab"));
    }

    [Fact]
    public void DeploymentAdvisorSurfacesGettextPotAndUnityXUnityGuidance()
    {
        var cap = new SourceCapability("unity", "", true, true, GameLaunch.Steam, "", "");
        var potOnlyFiles = new List<FileQualitySummary>
        {
            new("strings/strings_template.pot", "po", 100, 100, 0, 0, 100, 0, 0, FileQualityVerdict.Translatable, "", [], Included: true, IncludedUnits: 100),
            new("strings/strings_preinstalled_ko.po", "po", 100, 100, 0, 0, 100, 0, 0, FileQualityVerdict.SecondaryLanguage, "", [], Included: false, IncludedUnits: 0)
        };
        var potAdvice = DeploymentAdvisor.Analyze(cap, potOnlyFiles);
        Assert.True(potAdvice.HasGettextPotOnly);
        Assert.Equal("gettext-pot-to-po", potAdvice.Strategy);
        Assert.Single(potAdvice.SiblingPoPaths);

        var emptyUnityAdvice = DeploymentAdvisor.Analyze(cap, []);
        Assert.True(emptyUnityAdvice.SuggestXUnity);
        Assert.Equal("unity-xunity-overlay", emptyUnityAdvice.Strategy);
    }

    [Theory]
    [InlineData(0u)] // FastLZ
    [InlineData(1u)] // Zlib / Deflate
    [InlineData(2u)] // Zstd (Godot 4 default)
    [InlineData(3u)] // Gzip
    public void GodotTranslationFormatReadsAndRewritesCompressedRsccResources(uint compressionMode)
    {
        byte[] rawRsrc = BuildPlainGodotTranslationRsrc("en", [
            "PLAY", "Play Game — Adventure Awaits in the Citadel! Adventure Awaits in the Citadel!",
            "QUIT", "Quit to Desktop"
        ]);
        byte[] rscc = CompressToGodotRscc(rawRsrc, compressionMode, blockSize: 128);

        var format = GodotTranslationFormat.Instance;
        var slots = format.Extract(rscc);
        Assert.Equal(2, slots.Count);
        Assert.Equal("Play Game — Adventure Awaits in the Citadel! Adventure Awaits in the Citadel!", slots.Single(s => s.Locator == "msg:0").Source);
        Assert.Equal("Quit to Desktop", slots.Single(s => s.Locator == "msg:1").Source);

        byte[] rewritten = format.Apply(rscc, new Dictionary<string, string>
        {
            ["msg:0"] = "ابدأ اللعب — المغامرة بانتظارك في القلعة!",
            ["msg:1"] = "الخروج إلى سطح المكتب"
        });
        Assert.True(rewritten.AsSpan(0, 4).SequenceEqual("RSRC"u8));

        var reExtracted = format.Extract(rewritten);
        Assert.Equal("ابدأ اللعب — المغامرة بانتظارك في القلعة!", reExtracted.Single(s => s.Locator == "msg:0").Source);
        Assert.Equal("الخروج إلى سطح المكتب", reExtracted.Single(s => s.Locator == "msg:1").Source);
    }

    [Fact]
    public async Task WhitespaceOnlyPoEntryIsSkippedOnExtractAndNeverPoisonsLegacyTeamBatch()
    {
        // 1. PoFormat.Extract skips whitespace-only msgid (" ") cleanly
        const string po = """
            msgid ""
            msgstr ""

            msgctxt "STRINGS.WORKIVERSARY.SUBTITLE"
            msgid " "
            msgstr ""

            msgctxt "STRINGS.WORKIVERSARY.TITLE"
            msgid "Anatomy of a Hatch"
            msgstr ""
            """;
        var extracted = new PoFormat().Extract(Encoding.UTF8.GetBytes(po));
        Assert.Single(extracted);
        Assert.Equal("Anatomy of a Hatch", extracted[0].Source);

        // 2. Even if an older project DB already contains a whitespace-only unit alongside real units,
        //    TranslationTeam auto-preserves it verbatim and translates the rest cleanly without failing the batch.
        using var ws = new TempWorkspace();
        string game = Directory.CreateDirectory(ws.PathOf("ws-game")).FullName;
        File.WriteAllText(Path.Combine(game, "ui.json"), """{"title":"Start Game","sub":"Continue"}""");
        using var store = ProjectStore.Create(ws.PathOf("ws-project"), "WsTest", game);
        new ProjectService(store).Scan();
        // Inject a legacy whitespace-only pending unit into project.db
        store.Execute("""
            INSERT INTO units(id,path,locator,source,target,context,status,warning,active,revision,imported)
            VALUES('ws_legacy_id','ui.json','/blank',' ','','','pending','',1,0,'')
            """);

        var opts = new ProviderOptions("gemini", "gemini-2.5-flash", "test-key", BatchSize: 10);
        var team = new TranslationTeam(store);
        var plan = team.Capture(store.Units(), opts, new TeamSettings(Workers: 1, BatchSize: 10, FastMode: true));
        Assert.Equal(3, plan.Batches.Sum(b => b.Rows.Length));

        var sentIds = new List<string>();
        var summary = await team.Run(plan, opts, progress: null, cancellation: CancellationToken.None, send: (req, _) =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(req.Data);
            var map = new Dictionary<string, string>();
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                string id = entry.GetProperty("id").GetString()!;
                string src = entry.GetProperty("source").GetString()!;
                sentIds.Add(src);
                map[id] = src == "Start Game" ? "ابدأ اللعبة" : "متابعة";
            }
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(map));
        }, approveReviewed: true);

        // Whitespace unit was NOT sent to AI, and all 3 units completed & approved cleanly
        Assert.DoesNotContain(" ", sentIds);
        Assert.Equal(3, summary.Saved);
        Assert.DoesNotContain(store.Units(), u => u.Status == "pending");
        Assert.Equal(" ", store.Units().Single(u => u.Id == "ws_legacy_id").Target);
        Assert.Equal("approved", store.Units().Single(u => u.Id == "ws_legacy_id").Status);
    }

    [Fact]
    public void MalformedUnrelatedSidecarYamlDoesNotBlockPreviewOrBuildOfValidLocalization()
    {
        using var ws = new TempWorkspace();
        string game = Directory.CreateDirectory(ws.PathOf("sidecar-error-game")).FullName;
        Directory.CreateDirectory(Path.Combine(game, "strings"));
        Directory.CreateDirectory(Path.Combine(game, "worldgen"));

        File.WriteAllText(Path.Combine(game, "strings", "strings_template.pot"), """
            msgid ""
            msgstr ""

            msgctxt "STRINGS.UI.PLAY"
            msgid "Start New Colony"
            msgstr ""
            """);
        // Malformed sidecar YAML with duplicate key (common in shipped game data)
        File.WriteAllText(Path.Combine(game, "worldgen", "broken_cluster.yaml"),
            "menuOrder: 1\nmenuOrder: 2\n");

        using var store = ProjectStore.Create(ws.PathOf("sidecar-error-project"), "SidecarErr", game);
        var service = new ProjectService(store);
        var scan = service.Scan();
        Assert.Equal(1, scan.Units);
        Assert.Contains(scan.Diagnostics, d => d.Path.EndsWith("broken_cluster.yaml"));

        // Non-blocking: broken_cluster.yaml is not an active translated file
        Assert.False(service.HasBlockingScanErrors());
        Assert.True(service.Capabilities().CanBuild);

        var unit = store.Units().Single();
        unit.Target = "ابدأ مستعمرة جديدة";
        store.Save(unit, approve: true);

        var preview = service.Preview(final: true);
        Assert.Single(preview.ChangedFiles);
        var built = service.Build(ws.PathOf("sidecar-out"), final: true, reviewed: preview);
        Assert.Equal(1, built.Translations);
        Assert.Contains("ابدأ مستعمرة جديدة", File.ReadAllText(Path.Combine(built.Directory, "resources/strings/strings_template.pot")));
    }

    [Fact]
    public void GettextPotDeliversToSiblingPoPreservingHeaderAndPatchesLooseUnityFontWithRestore()
    {
        using var ws = new TempWorkspace();
        string game = Directory.CreateDirectory(ws.PathOf("gettext-unity-game")).FullName;
        string dataDir = Directory.CreateDirectory(Path.Combine(game, "Colony_Data")).FullName;
        string stringsDir = Directory.CreateDirectory(Path.Combine(dataDir, "StreamingAssets", "strings")).FullName;
        string fontsDir = Directory.CreateDirectory(Path.Combine(dataDir, "StreamingAssets", "fonts")).FullName;
        File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), new byte[64]);

        // Loose UI TTF font (copy a minimal valid stub; original bytes will be backed up and restored)
        byte[] origFont = Encoding.ASCII.GetBytes("ORIGINAL_LATIN_ONLY_FONT_STUB_DATA_1234567890");
        string fontPath = Path.Combine(fontsDir, "NotoSansUI-Regular.ttf");
        File.WriteAllBytes(fontPath, origFont);

        const string pot = """
            msgid ""
            msgstr ""
            "Application: Colony\n"

            msgctxt "STRINGS.UI.START"
            msgid "Start New Colony"
            msgstr ""
            """;
        const string koPo = """
            msgid ""
            msgstr ""
            "Language: ko\n"
            "Content-Type: text/plain; charset=UTF-8\n"

            msgctxt "STRINGS.UI.START"
            msgid "Start New Colony"
            msgstr "새 식민지 시작"
            """;
        File.WriteAllText(Path.Combine(stringsDir, "strings_template.pot"), pot);
        File.WriteAllText(Path.Combine(stringsDir, "strings_preinstalled_ko.po"), koPo);

        using var store = ProjectStore.Create(ws.PathOf("gettext-unity-project"), "Colony", game);
        var service = new ProjectService(store);
        var scan = service.Scan();
        Assert.Equal(1, scan.Units);

        var unit = store.Units().Single();
        unit.Target = "ابدأ مستعمرة جديدة";
        store.Save(unit, approve: true);

        // Mode A: redirect .pot translations into sibling ko.po (preserving Language: ko) + bilingual loose TTF font patch
        string siblingRel = "Colony_Data/StreamingAssets/strings/strings_preinstalled_ko.po";
        store.Set(ProjectService.GettextPoTargetSetting, siblingRel);
        store.Set("unity_arabic_shaping", "0"); // test logical UTF-8 Arabic cleanly

        var preview = service.Preview(final: true);
        Assert.Contains(siblingRel, preview.ChangedFiles);
        Assert.Contains("Colony_Data/StreamingAssets/fonts/NotoSansUI-Regular.ttf", preview.ChangedFiles);
        Assert.DoesNotContain("Colony_Data/StreamingAssets/strings/strings_template.pot", preview.ChangedFiles);

        var deployed = PackageInstaller.DeployInPlace(store, ws.PathOf("out-ko"), final: true);
        Assert.Equal(2, deployed.Install.Files);

        // Verify sibling .po has Arabic translation AND preserved Language: ko header!
        string liveKo = File.ReadAllText(Path.Combine(stringsDir, "strings_preinstalled_ko.po"));
        Assert.Contains("Language: ko", liveKo);
        Assert.Contains("ابدأ مستعمرة جديدة", liveKo);
        // Verify loose TTF font was patched with bilingual Arabic+Latin TTF!
        byte[] liveFont = File.ReadAllBytes(fontPath);
        Assert.True(ArabicFontCatalog.SupportsLatinAndArabic(liveFont));

        // One-click restore must restore BOTH original ko.po AND original font bit-for-bit
        PackageInstaller.RestoreInPlace(store);
        Assert.Equal(origFont, File.ReadAllBytes(fontPath));
        Assert.Contains("새 식민지 시작", File.ReadAllText(Path.Combine(stringsDir, "strings_preinstalled_ko.po")));

        // Mode B: export-ar emits standalone strings_template_ar.po with Language: ar
        store.Set(ProjectService.GettextPoTargetSetting, "export-ar");
        var previewAr = service.Preview(final: true);
        Assert.Contains("Colony_Data/StreamingAssets/strings/strings_template_ar.po", previewAr.ChangedFiles);
        var builtAr = service.Build(ws.PathOf("out-ar"), final: true, reviewed: previewAr);
        string exportedAr = File.ReadAllText(Path.Combine(builtAr.Directory, "resources/Colony_Data/StreamingAssets/strings/strings_template_ar.po"));
        Assert.Contains("Language: ar", exportedAr);
        Assert.Contains("ابدأ مستعمرة جديدة", exportedAr);
    }

    [Fact]
    public void ArabicShaperPreservesMultiLineAndNestedRichTextTagsWithoutCrashing()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 1. Multi-line tag spanning \n with nested inner tag on the closing line
        const string multiLine = "<size=11>رئيس قسم النزاهة\nمعهد فيرتكس\n<i>حيث تلتقي التكنولوجيا بالإنسانية</i></size>";
        string shapedMulti = StarTruckerDisplay.Shape(multiLine, StarTruckerDisplay.AutoWrap);
        Assert.True(Tokens.ValidTagNesting(multiLine, shapedMulti));
        Assert.StartsWith("<size=11>", shapedMulti);
        Assert.EndsWith("</size>", shapedMulti);

        // 2. Nested rich-text tags with Arabic text between outer and inner tags
        const string nested = "<b>فرصة البقاء: <color=#ff0000>عالية جداً</color></b>";
        string shapedNested = StarTruckerDisplay.Shape(nested, StarTruckerDisplay.AutoWrap);
        Assert.True(Tokens.ValidTagNesting(nested, shapedNested));

        // 3. Nested tags around placeholder
        const string nestedPlaceholder = "فرصة البقاء: <b><color=#{1}>{0}</color></b>";
        string shapedPlaceholder = StarTruckerDisplay.Shape(nestedPlaceholder, StarTruckerDisplay.AutoWrap);
        Assert.True(Tokens.ValidTagNesting(nestedPlaceholder, shapedPlaceholder));

        // 4. Clickable <link> spans must NEVER be split/duplicated across wrapped lines (strict link-count engines reject split <link> tags)
        const string withLinks = "يحول <link=\"PLASTIFIABLELIQUID\">مونومرات البلاستيك</link> إلى <link=\"POLYPROPYLENE\">بلاستيك</link> خام.";
        string shapedLinks = StarTruckerDisplay.Shape(withLinks, StarTruckerDisplay.AutoWrap);
        Assert.True(Tokens.ValidTagNesting(withLinks, shapedLinks));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(shapedLinks, @"<link=").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(shapedLinks, @"</link>").Count);
    }

    private static byte[] BuildPlainGodotTranslationRsrc(string locale, string[] packedMessages)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        static void WriteUstr(BinaryWriter bw, string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            bw.Write((uint)(utf8.Length + 1));
            bw.Write(utf8);
            bw.Write((byte)0);
        }

        static void WriteLenStr(BinaryWriter bw, string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            bw.Write((uint)utf8.Length);
            bw.Write(utf8);
            int pad = (4 - (utf8.Length % 4)) % 4;
            for (int i = 0; i < pad; i++) bw.Write((byte)0);
        }

        w.Write("RSRC"u8);
        w.Write(0u); // little-endian
        w.Write(0u); // real32
        w.Write(4u); // verMajor
        w.Write(3u); // verMinor
        w.Write(5u); // formatVersion
        WriteUstr(w, "Translation");
        w.Write(0ul); // importMdOfs
        w.Write(0u);  // flags
        w.Write(0L);  // uid
        w.Write(new byte[11 * 4]);

        // String table: ["locale", "messages"]
        w.Write(2u);
        WriteUstr(w, "locale");
        WriteUstr(w, "messages");

        w.Write(0u); // ext resources
        w.Write(1u); // internal resources
        WriteUstr(w, "local://0");
        long ofsPos = ms.Position;
        w.Write(0ul);

        long objPos = ms.Position;
        WriteUstr(w, "Translation");
        w.Write(2u); // 2 properties

        // Property 0: locale (VariantString = 5)
        w.Write(0u);
        w.Write(5u);
        WriteLenStr(w, locale);

        // Property 1: messages (VariantPackedStringArray = 34)
        w.Write(1u);
        w.Write(34u);
        w.Write((uint)packedMessages.Length);
        foreach (string s in packedMessages) WriteLenStr(w, s);

        w.Write("RSRC"u8);
        ms.Position = ofsPos;
        w.Write((ulong)objPos);
        return ms.ToArray();
    }

    private static byte[] CompressToGodotRscc(byte[] rsrcBytes, uint mode, uint blockSize)
    {
        // Godot RSCC compresses payload AFTER the 4-byte "RSRC" magic
        byte[] body = rsrcBytes[4..];
        int blockCount = (int)((body.Length + blockSize - 1) / blockSize);
        var chunks = new List<byte[]>(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            int start = (int)(i * blockSize);
            int len = Math.Min((int)blockSize, body.Length - start);
            ReadOnlySpan<byte> slice = body.AsSpan(start, len);
            if (mode == 2u)
            {
                using var zstd = new ZstdSharp.Compressor();
                chunks.Add(zstd.Wrap(slice).ToArray());
            }
            else if (mode == 0u)
            {
                chunks.Add(CompressFastLzLevel1ForTest(slice));
            }
            else
            {
                using var compMs = new MemoryStream();
                using (Stream comp = mode == 3u
                    ? new GZipStream(compMs, CompressionLevel.Fastest, leaveOpen: true)
                    : new ZLibStream(compMs, CompressionLevel.Fastest, leaveOpen: true))
                {
                    comp.Write(slice);
                }
                chunks.Add(compMs.ToArray());
            }
        }

        using var outMs = new MemoryStream();
        using var w = new BinaryWriter(outMs, Encoding.UTF8, leaveOpen: true);
        w.Write("RSCC"u8);
        w.Write(mode);
        w.Write(blockSize);
        w.Write((uint)body.Length);
        foreach (var c in chunks) w.Write((uint)c.Length);
        foreach (var c in chunks) w.Write(c);
        return outMs.ToArray();
    }

    private static byte[] CompressFastLzLevel1ForTest(ReadOnlySpan<byte> input)
    {
        // Minimal FastLZ Level 1 encoder that emits both literal runs and LZ back-references
        using var ms = new MemoryStream();
        int pos = 0;
        var lit = new List<byte>();

        void FlushLiterals()
        {
            int idx = 0;
            while (idx < lit.Count)
            {
                int take = Math.Min(32, lit.Count - idx);
                ms.WriteByte((byte)(take - 1)); // ctrl 0..31 => literal run of (ctrl + 1)
                for (int k = 0; k < take; k++) ms.WriteByte(lit[idx + k]);
                idx += take;
            }
            lit.Clear();
        }

        // First instruction in FastLZ Level 1 must be a literal run (upper 3 bits == 0)
        if (input.Length > 0)
        {
            lit.Add(input[0]);
            pos = 1;
        }

        while (pos < input.Length)
        {
            int bestLen = 0;
            int bestDist = 0;
            int searchStart = Math.Max(0, pos - 8192);
            for (int refPos = searchStart; refPos < pos; refPos++)
            {
                int matchLen = 0;
                while (pos + matchLen < input.Length && matchLen < 264 && input[refPos + matchLen] == input[pos + matchLen])
                    matchLen++;
                if (matchLen >= 3 && matchLen > bestLen)
                {
                    bestLen = matchLen;
                    bestDist = pos - refPos - 1;
                }
            }

            if (bestLen >= 3)
            {
                FlushLiterals();
                int lenCode = bestLen - 2; // 1..6 short, >=7 extended
                int ofsHigh = (bestDist >> 8) & 31;
                int ofsLow = bestDist & 0xFF;
                if (lenCode < 7)
                {
                    ms.WriteByte((byte)((lenCode << 5) | ofsHigh));
                }
                else
                {
                    ms.WriteByte((byte)((7 << 5) | ofsHigh));
                    ms.WriteByte((byte)(lenCode - 7));
                }
                ms.WriteByte((byte)ofsLow);
                pos += bestLen;
            }
            else
            {
                lit.Add(input[pos++]);
            }
        }
        FlushLiterals();
        return ms.ToArray();
    }
}
