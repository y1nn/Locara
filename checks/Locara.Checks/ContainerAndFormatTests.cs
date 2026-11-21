using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class ContainerAndFormatTests
{
    [Fact]
    public void AesZipWrongPasswordPckRoundtripAndCancellation()
    {
        using var ws = new TempWorkspace();
        const string input = "{\"a\":\"Back\",\"b\":\"Back\",\"greeting\":\"Welcome [player_name]\",\"number\":3}";
        var resources = new List<Resource>
        {
            new("text.json", Encoding.UTF8.GetBytes(input)),
            new("assets/untouched.bin", [0, 1, 255, 23])
        };

        foreach (IContainerAdapter adapter in new IContainerAdapter[] { new ZipContainer(), new PckContainer() })
        {
            string target = ws.PathOf(adapter.Name + (adapter is ZipContainer ? ".zip" : ".pck"));
            string? password = adapter is ZipContainer ? "fixture-password" : null;
            adapter.Write(target, resources, password, default);
            var reread = adapter.Read(target, password, default);
            Assert.Equal(resources.Count, reread.Count);
            Assert.All(reread, r => Assert.Equal(resources.Single(x => x.Path == r.Path).Bytes, r.Bytes));
            if (adapter is ZipContainer)
                Assert.ThrowsAny<Exception>(() => adapter.Read(target, "wrong", default));
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new PckContainer().Write(ws.PathOf("cancelled.pck"), resources, null, cancelled.Token));
    }

    [Fact]
    public void FormatAdaptersWithSampleFilesAndBilingualImports()
    {
        string fixtures = Path.Combine(TestHelpers.SamplesRoot, "resources");
        foreach (string path in Directory.GetFiles(fixtures))
        {
            var format = Formats.For(path)!;
            byte[] bytes = File.ReadAllBytes(path);
            var slots = format.Extract(bytes);
            Assert.NotEmpty(slots);
            var map = slots.ToDictionary(s => s.Locator, s => s.Source + " عربي");
            byte[] output = format.Apply(bytes, map);
            Assert.NotEmpty(output);
            Assert.NotEmpty(format.Extract(output));
        }

        (IFormatHandler Format, string Text)[] bilingual =
        [
            (new XmlFormat(), "<xliff trgLang='ar'><segment><source>Back</source><target>عودة</target></segment></xliff>"),
            (new PoFormat(), "msgid \"\"\nmsgstr \"\"\n\"Language: ar\\n\"\n\nmsgid \"Back\"\nmsgstr \"عودة\"\n"),
            (new RenpyFormat(), "translate arabic menu_1:\n    # guide \"Back\"\n    guide \"عودة\"\n"),
            (new RenpyFormat(), "translate arabic strings:\n    old \"Back\"\n    new \"عودة\"\n")
        ];
        foreach (var (format, text) in bilingual)
            Assert.Equal("عودة", format.Extract(Encoding.UTF8.GetBytes(text)).Single().ExistingTarget);

        using (var book = new XLWorkbook())
        {
            var sheet = book.Worksheets.Add("Menu");
            sheet.Cell("A1").Value = "en";
            sheet.Cell("B1").Value = "ar";
            sheet.Cell("A2").Value = "Back";
            sheet.Cell("B2").Value = "عودة";
            using var bytes = new MemoryStream();
            book.SaveAs(bytes);
            Assert.Equal("عودة", new SpreadsheetFormat().Extract(bytes.ToArray()).Single().ExistingTarget);
        }

        var csv = new DelimitedFormat(',');
        byte[] unity = Encoding.UTF8.GetBytes("Key,Id,English(en),English(en) Comments\nstart,612004152,Observe,Button label\n");
        var unitySlot = csv.Extract(unity).Single();
        byte[] unityOutput = csv.Apply(unity, new Dictionary<string, string> { [unitySlot.Locator] = "راقب" });
        string unityText = Encoding.UTF8.GetString(unityOutput);
        Assert.Equal("key:start", unitySlot.Locator);
        Assert.Contains("English(en) Comments,Arabic(ar)", unityText);
        Assert.Contains("start,612004152,Observe,Button label,راقب", unityText);
        Assert.Equal("راقب", csv.Extract(unityOutput).Single().ExistingTarget);
    }

    [Fact]
    public void PoFormatPluralHeaderAndLanguageIsolation()
    {
        var po = new PoFormat();
        byte[] withoutHeader = Encoding.UTF8.GetBytes("#, fuzzy\nmsgid \"Star\"\nmsgid_plural \"Stars\"\nmsgstr[0] \"\"\nmsgstr[1] \"\"\n");
        var forms = po.Extract(withoutHeader);
        byte[] localized = po.Apply(withoutHeader, forms.ToDictionary(slot => slot.Locator, _ => "نجوم"));
        string catalog = Encoding.UTF8.GetString(localized);
        Assert.Equal(6, forms.Count);
        Assert.Contains("Language: ar", catalog);
        Assert.Contains("Plural-Forms: nplurals=6", catalog);
        Assert.DoesNotContain("#, fuzzy", catalog);
        Assert.Equal(6, po.Extract(localized).Count(slot => slot.ExistingTarget == "نجوم"));

        byte[] frenchPo = Encoding.UTF8.GetBytes("""
            msgid ""
            msgstr "Language: fr\n"

            msgctxt "menu"
            msgid "Open"
            msgstr "Ouvrir"

            msgid "Close"
            msgstr "Fermer"

            msgid "Star"
            msgid_plural "Stars"
            msgstr[0] "Étoile"
            msgstr[1] "Étoiles"
            """);
        var frenchSlots = po.Extract(frenchPo);
        Assert.All(frenchSlots, slot => Assert.Null(slot.ExistingTarget));
        Assert.Equal(frenchPo, po.Apply(frenchPo, new Dictionary<string, string>()));

        var open = frenchSlots.Single(slot => slot.Source == "Open");
        var star = frenchSlots.Single(slot => slot.Source == "Star");
        var converted = po.Apply(frenchPo, new Dictionary<string, string> { [open.Locator] = "افتح", [star.Locator] = "نجمة" });
        var convertedSlots = po.Extract(converted);
        Assert.Equal("افتح", convertedSlots.Single(slot => slot.Locator == open.Locator).ExistingTarget);
        Assert.Equal("نجمة", convertedSlots.Single(slot => slot.Locator == star.Locator).ExistingTarget);
        Assert.All(convertedSlots.Where(slot => slot.Locator != open.Locator && slot.Locator != star.Locator),
            slot => Assert.Equal("", slot.ExistingTarget));

        var close = convertedSlots.Single(slot => slot.Source == "Close");
        var updated = po.Apply(converted, new Dictionary<string, string> { [close.Locator] = "أغلق" });
        Assert.Equal("افتح", po.Extract(updated).Single(slot => slot.Locator == open.Locator).ExistingTarget);
    }

    [Fact]
    public void PoFormatStateMachineHandlesObsoleteCommentsNoBlankLinesFlagsMultilineAndEscapes()
    {
        var po = new PoFormat();
        string raw = """
            # File header comment
            msgid ""
            msgstr ""
            "Language: ar\n"
            "Content-Type: text/plain; charset=UTF-8\n"

            #. Translator note
            #: ui.cpp:42
            #, fuzzy, c-format
            #| msgid "Old %s"
            msgctxt ""
            "hud"
            msgid ""
            "Line1\t"
            "Line2\101"
            msgstr "قديم"
            msgid "Tightly packed"
            msgstr ""

            #~ msgid "Obsolete entry"
            #~ msgstr "قديم مهمل"
            """;
        byte[] bomPo = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(raw)];

        var slots = po.Extract(bomPo);
        Assert.Equal(2, slots.Count);
        Assert.DoesNotContain(slots, s => s.Source == "Obsolete entry");
        Assert.DoesNotContain(slots, s => s.Source == "Old %s");

        var first = slots.Single(s => s.Context == "hud");
        Assert.Equal("Line1\tLine2A", first.Source);
        Assert.Equal("قديم", first.ExistingTarget);
        var packed = slots.Single(s => s.Source == "Tightly packed");
        Assert.Equal("", packed.ExistingTarget);

        byte[] output = po.Apply(bomPo, new Dictionary<string, string>
        {
            [first.Locator] = "سطر1\tسطر2",
            [packed.Locator] = "متلاصق"
        });
        Assert.True(output.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        string exported = Encoding.UTF8.GetString(output.AsSpan(3));
        Assert.Contains("#, c-format", exported);
        Assert.DoesNotContain("fuzzy", exported);
        Assert.Contains("#| msgid \"Old %s\"", exported);
        Assert.Contains("#~ msgid \"Obsolete entry\"", exported);
        Assert.Contains("#~ msgstr \"قديم مهمل\"", exported);

        var roundtrip = po.Extract(output);
        Assert.Equal("سطر1\tسطر2", roundtrip.Single(s => s.Locator == first.Locator).ExistingTarget);
        Assert.Equal("متلاصق", roundtrip.Single(s => s.Locator == packed.Locator).ExistingTarget);
    }

    [Fact]
    public void RenpyTemplatesAndRpgMakerEventSelection()
    {
        using var ws = new TempWorkspace();
        var renpy = new RenpyFormat();
        byte[] template = File.ReadAllBytes(Path.Combine(TestHelpers.SamplesRoot, "renpy", "arabic.rpy"));
        Assert.Contains(renpy.Extract(template), slot => slot.Source == "Welcome to the observatory.");

        const string generated = "translate arabic start_f0:\r\n    voice \"effects/voice.ogg\"\r\n    # guide happy \"Welcome [visitor].\" with dissolve\r\n    guide happy \"\" with dissolve # keep this\r\n";
        var generatedBytes = Encoding.UTF8.GetBytes(generated);
        var line = renpy.Extract(generatedBytes).Single();
        string translatedScript = Encoding.UTF8.GetString(renpy.Apply(generatedBytes,
            new Dictionary<string, string> { [line.Locator] = "مرحباً [visitor]." }));
        Assert.Equal("guide", line.Context);
        Assert.Equal("Welcome [visitor].", line.Source);
        Assert.Contains("guide happy \"مرحباً [visitor].\" with dissolve # keep this\r\n", translatedScript);
        Assert.Contains("voice \"effects/voice.ogg\"", translatedScript);

        const string mixedTemplate = """
            translate french start_mixed:
                # guide "Welcome."
                guide "Bonjour."

            translate french strings:
                old "Look  up"
                new "Regardez"

            translate arabic start_mixed:
                # guide -quiet @ happy "Look \ up."
                guide -quiet @ happy "" (what_color="#ffffff") with dissolve # keep
                # guide "Welcome."
                guide ""

            translate arabic strings:
                old "Look  up"
                new ""
            """;
        var mixedBytes = Encoding.UTF8.GetBytes(mixedTemplate);
        var arabicLines = renpy.Extract(mixedBytes);
        const string spacedTarget = "انظر  للأعلى";
        var mixedOutput = renpy.Apply(mixedBytes, arabicLines.ToDictionary(slot => slot.Locator, _ => spacedTarget));
        string mixedText = Encoding.UTF8.GetString(mixedOutput);
        Assert.Equal(3, arabicLines.Count);
        Assert.Equal("start_mixed/0", arabicLines.Single(slot => slot.Source == "Welcome.").Locator);
        Assert.Contains(arabicLines, slot => slot.Source == "Look  up.");
        Assert.Contains(arabicLines, slot => slot.Source == "Look  up");
        Assert.Contains("guide \"Bonjour.\"", mixedText);
        Assert.Contains("new \"Regardez\"", mixedText);
        Assert.Contains("guide -quiet @ happy \"انظر \\ للأعلى\" (what_color=\"#ffffff\") with dissolve # keep", mixedText);
        Assert.Contains("new \"انظر  للأعلى\"", mixedText);
        Assert.All(renpy.Extract(mixedOutput), slot => Assert.Equal(spacedTarget, slot.ExistingTarget));

        string foreignPath = ws.PathOf("french.rpy");
        File.WriteAllText(foreignPath, mixedTemplate[..mixedTemplate.IndexOf("translate arabic", StringComparison.Ordinal)]);
        using (var foreign = ProjectStore.Create(ws.PathOf("foreign-renpy"), "Foreign Ren'Py", foreignPath))
        {
            var service = new ProjectService(foreign);
            var scan = service.Scan();
            Assert.Equal(0, scan.Units);
            Assert.Contains(scan.Diagnostics, item => item.Message.Contains("game/tl/arabic"));
            Assert.Equal(scan.Diagnostics, service.Scan().Diagnostics);
        }

        using (var rpg = ProjectStore.Create(ws.PathOf("rpg-project"), "RPG profile", Path.Combine(TestHelpers.SamplesRoot, "rpgmaker")))
        {
            new ProjectService(rpg).Scan();
            var units = rpg.Units();
            Assert.Contains(units, unit => unit.Source == "Watch stars");
            Assert.Contains(units, unit => unit.Source == "Welcome, \\N[1]!");
            Assert.All(units, unit => Assert.DoesNotContain("internal", unit.Source, StringComparison.Ordinal));
        }

        string data = Path.Combine(TestHelpers.SamplesRoot, "rpgmaker", "data");
        foreach (string inputPath in new[] { data, Path.Combine(data, "Map001.json") })
        {
            using var rpg = ProjectStore.Create(ws.PathOf("rpg-" + Path.GetFileName(inputPath)), "RPG data", inputPath);
            var service = new ProjectService(rpg);
            service.Scan();
            var welcome = rpg.Units().Single(unit => unit.Source == "Welcome, \\N[1]!");
            Assert.Equal("rpgmaker", rpg.Get("scanned_engine"));
            Assert.All(rpg.Units(), unit => Assert.DoesNotContain("internal", unit.Source));
            welcome.Target = "مرحباً، \\N[1]!";
            rpg.Save(welcome, approve: true);
            rpg.Set("extraction_revision", "");
            Assert.Throws<InvalidOperationException>(() => service.Preview(final: true));
            service.Scan();
            Assert.Equal("approved", rpg.FindUnit(welcome.Id).Status);
            Assert.Equal(["Map001.json"], service.Preview(final: true).ChangedFiles);
        }
    }

    [Fact]
    public void StarTruckerArchiveStableIdsAmbiguousDuplicatesAndLocales()
    {
        using var ws = new TempWorkspace();
        string truckRoot = ws.PathOf("truck-fixture");
        const string truckPackage = "Star Trucker_Data/StreamingAssets/XML/Strings/en.xml";
        string truckArchive = Path.Combine(truckRoot, truckPackage);
        Directory.CreateDirectory(Path.GetDirectoryName(truckArchive)!);
        File.WriteAllText(Path.Combine(truckRoot, "unrelated.json"), "{\"name\":\"Ignore this file\"}");
        const string truckMenu = "<string id='STR_NEWGAME'>New Game</string>";
        const string truckDuplicate1 = "<string id='General'>General options</string>";
        const string truckDuplicate2 = "<string id='General'>General cargo</string>";

        void WriteTruckArchive(string elements) => new ZipContainer().Write(truckArchive,
            [new Resource("strings.xml", Encoding.UTF8.GetBytes("<data>" + elements + "</data>")),
                new Resource("untouched.bin", [1, 2, 3])], null, default);

        WriteTruckArchive(truckMenu + truckDuplicate1 + truckDuplicate2);
        using (var truck = ProjectStore.Create(ws.PathOf("truck-project"), "Truck fixture", truckRoot))
        {
            var service = new ProjectService(truck);
            Assert.Equal(truckPackage, service.DiscoverPackages().Single().Path);
            Assert.Equal(3, service.Scan().Units);
            Assert.Equal(truckPackage, service.SelectedPackage);

            var menu = truck.Units().Single(unit => unit.Locator == "id:STR_NEWGAME");
            menu.Target = "لعبة جديدة";
            truck.Save(menu, true);
            var duplicate = truck.Units().Single(unit => unit.Source == "General options");
            duplicate.Target = "خيارات عامة";
            truck.Save(duplicate, true);

            WriteTruckArchive(truckDuplicate2 + truckMenu + truckDuplicate1);
            service.Scan();
            Assert.Equal("approved", truck.FindUnit(menu.Id).Status);
            Assert.Equal("conflict", truck.FindUnit(duplicate.Id).Status);

            string output = service.Build(ws.PathOf("truck-output"), final: true).Directory;
            string written = Path.Combine(output, "archive", truckPackage);
            var writtenResources = new ZipContainer().Read(written, null, default);
            var truckCatalog = XDocument.Parse(Encoding.UTF8.GetString(writtenResources
                .Single(resource => resource.Path == "strings.xml").Bytes));
            Assert.Equal(["General cargo", "لعبة جديدة", "General options"],
                truckCatalog.Root!.Elements().Select(element => element.Value));
            Assert.Equal(new byte[] { 1, 2, 3 }, writtenResources.Single(resource => resource.Path == "untouched.bin").Bytes);

            new ZipContainer().Write(Path.Combine(Path.GetDirectoryName(truckArchive)!, "de.xml"),
                [new Resource("strings.xml", Encoding.UTF8.GetBytes("<data><string id='STR_NEWGAME'>Neues Spiel</string></data>"))],
                null, default);
            var languages = service.Languages();
            var german = languages.Single(language => language.Code == "de");
            Assert.Equal("New Game", languages.Single(language => language.Code == "en").Sample);
            Assert.Equal("Neues Spiel", german.Sample);

            service.SelectPackage(german.Package);
            Assert.Equal(1, service.Scan().Units);
            Assert.NotEqual(menu.Id, truck.Units().Single().Id);
            var caps = service.Capabilities();
            Assert.True(caps.CanRead && caps.CanBuild && caps.Launch == GameLaunch.None);

            service.SelectPackage(truckPackage);
            service.Scan();
            Assert.Equal("لعبة جديدة", truck.FindUnit(menu.Id).Target);

            // Simulate accidental loose selection and verify EnsureCatalogSelected / Scan restores en.xml and approved translations automatically
            service.SelectPackage(null);
            Assert.Equal("loose", service.SelectedPackage);
            service.Scan();
            Assert.Equal(truckPackage, truck.Get("scanned_package"));
            Assert.Equal(truckPackage, service.SelectedPackage);
            Assert.Equal("approved", truck.FindUnit(menu.Id).Status);
            Assert.Equal("لعبة جديدة", truck.FindUnit(menu.Id).Target);
        }

        // Verify StarTruckerDisplay supports all non-empty units (not just 20 menu keys) and shapes multiline & paired tags cleanly
        var dialogueUnit = new Unit
        {
            Id = "d1",
            Context = "STR_DIALOGUE_1",
            Source = "Remove <color=#ffea00>[Hold]</color>\nRepair for <color=#D43030>{1}?</color><br><size=75%>Line three",
            Target = "إزالة <color=#ffea00>[مطولاً]</color>\nهل تريد الصيانة مقابل <color=#D43030>{1}؟</color><br><size=75%>السطر الثالث"
        };
        Assert.True(StarTruckerDisplay.Supports(dialogueUnit));
        string shaped = StarTruckerDisplay.Shape(dialogueUnit.Target);
        Assert.Equal("", Tokens.Validate(dialogueUnit.Target, shaped));
        Assert.True(shaped.IndexOf("<color=#ffea00>", StringComparison.Ordinal) < shaped.IndexOf("</color>", StringComparison.Ordinal));
        Assert.Contains("<br><size=75%>", shaped, StringComparison.Ordinal);
        Assert.Equal(2, shaped.Split('\n').Length);
        string shapedPercent = StarTruckerDisplay.Shape("أنا شبه متأكدة بنسبة 87% من أنها ستعمل كما ينبغي!");
        Assert.NotEmpty(shapedPercent);

        using var truckFile = ProjectStore.Create(ws.PathOf("truck-file-project"), "Direct archive", truckArchive);
        Assert.Equal(3, new ProjectService(truckFile).Scan().Units);
    }

    [Fact]
    public void XmlDeclarationAndIniBomPreservation()
    {
        var xml = new XmlFormat();
        byte[] xmlInput = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<resources><string name=\"start\">Start</string></resources>");
        var xmlSlot = xml.Extract(xmlInput).Single();
        Assert.Equal(xmlInput, xml.Apply(xmlInput, new Dictionary<string, string> { [xmlSlot.Locator] = "Start" }));
        string xmlOut = Encoding.UTF8.GetString(xml.Apply(xmlInput, new Dictionary<string, string> { [xmlSlot.Locator] = "ابدأ" }));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xmlOut, StringComparison.Ordinal);
        Assert.Contains(">ابدأ</string>", xmlOut);

        var ini = new IniFormat();
        byte[] bomIni = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("[Menu]\nstart=Start\n")];
        var iniSlot = ini.Extract(bomIni).Single();
        Assert.Equal("[Menu]/start", iniSlot.Locator);
        byte[] bomOut = ini.Apply(bomIni, new Dictionary<string, string> { [iniSlot.Locator] = "ابدأ" });
        Assert.True(bomOut.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("[Menu]\nstart=ابدأ\n", Encoding.UTF8.GetString(bomOut.AsSpan(3)));
        Assert.Equal(bomIni, ini.Apply(bomIni, new Dictionary<string, string> { [iniSlot.Locator] = "Start" }));
    }

    [Fact]
    public void ArabicFontCatalogBuiltInAndCustomFontsValidateAndInspect()
    {
        Assert.Equal(7, ArabicFontCatalog.BuiltInFonts.Count);
        foreach (var font in ArabicFontCatalog.BuiltInFonts)
        {
            byte[] bytes = ArabicFontCatalog.LoadBytes(font);
            var compat = ArabicFontCatalog.Inspect(bytes);
            Assert.True(compat.IsValidFont, font.DisplayName);
            Assert.True(compat.FullyCompatible, $"{font.DisplayName}: {compat.Summary}");
            Assert.True(compat.PresentationFormsCount >= 139, font.DisplayName);
        }

        // Custom font directory discovery & resolution
        using var ws = new TempWorkspace();
        string customDir = ws.PathOf("custom-fonts");
        Directory.CreateDirectory(customDir);
        byte[] sampleBytes = ArabicFontCatalog.LoadBytes(ArabicFontCatalog.BuiltInFonts[2]); // IBM Plex
        File.WriteAllBytes(Path.Combine(customDir, "MyGameFont.ttf"), sampleBytes);

        var custom = ArabicFontCatalog.CustomFonts(customDir);
        Assert.Single(custom);
        Assert.Equal("custom:MyGameFont.ttf", custom[0].Id);
        var resolved = ArabicFontCatalog.Resolve("custom:MyGameFont.ttf", customDir);
        Assert.True(resolved.IsCustom);
        Assert.Equal(sampleBytes.Length, ArabicFontCatalog.LoadBytes(resolved).Length);

        // Invalid font file rejected cleanly
        var bad = ArabicFontCatalog.Inspect("not a font"u8);
        Assert.False(bad.IsValidFont);
    }

    [Fact]
    public void StarTruckerDisplayPrependsFallbackAndOptimizesAtlasMetrics()
    {
        // 1. AddFallback prepends Object 439 at index 0 ahead of existing fallbacks (444, 442, 443)
        byte[] fontObj = new byte[64];
        const int listOff = 8;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(fontObj.AsSpan(listOff), 3);
        long[] originalIds = [444, 442, 443];
        for (int i = 0; i < originalIds.Length; i++)
        {
            int entry = listOff + 4 + i * 12;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(fontObj.AsSpan(entry), 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(fontObj.AsSpan(entry + 4), originalIds[i]);
        }
        fontObj[listOff + 4 + 36] = 0xAB; // sentinel trailing byte

        byte[] patchedFont = StarTruckerDisplay.AddFallback(fontObj, listOff, fileId: 0, expectedCount: 3);
        Assert.Equal(fontObj.Length + 12, patchedFont.Length);
        Assert.Equal(4, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(patchedFont.AsSpan(listOff)));
        Assert.Equal(439L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(patchedFont.AsSpan(listOff + 8)));
        Assert.Equal(444L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(patchedFont.AsSpan(listOff + 20)));
        Assert.Equal(442L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(patchedFont.AsSpan(listOff + 32)));
        Assert.Equal(443L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(patchedFont.AsSpan(listOff + 44)));
        Assert.Equal(0xAB, patchedFont[listOff + 4 + 48]);

        // 2. ExpandFallbackAtlas scales 86pt/9pad/512 -> 48pt/5pad/1024 while keeping ratio fields (1.0f, 0.5f) intact
        byte[] atlasObj = new byte[540];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(184), 86);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(atlasObj.AsSpan(188), 1f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(atlasObj.AsSpan(196), 98.8916f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(atlasObj.AsSpan(224), 0.5f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(atlasObj.AsSpan(232), 0.5f);
        atlasObj[284] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(308), 511);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(312), 511);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(atlasObj.AsSpan(336), 86f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(atlasObj.AsSpan(340), 1f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(atlasObj.AsSpan(380), 0.5f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(atlasObj.AsSpan(404), 9f);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(428), 512);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(432), 512);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(436), 9);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(508), 86);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(512), 9);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(520), 512);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(atlasObj.AsSpan(524), 512);

        byte[] patchedAtlas = StarTruckerDisplay.ExpandFallbackAtlas(atlasObj);
        Assert.Equal(48, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(patchedAtlas.AsSpan(184)));
        Assert.Equal(1f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(patchedAtlas.AsSpan(188)));
        Assert.Equal(98.8916f * (48f / 86f), System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(patchedAtlas.AsSpan(196)), 3);
        Assert.Equal(0.5f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(patchedAtlas.AsSpan(224)));
        Assert.Equal(0.5f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(patchedAtlas.AsSpan(232)));
        Assert.Equal(1, patchedAtlas[284]);
        Assert.Equal(1023, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(patchedAtlas.AsSpan(308)));
        Assert.Equal(1024, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(patchedAtlas.AsSpan(428)));
        Assert.Equal(5, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(patchedAtlas.AsSpan(436)));
        Assert.Equal(48, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(patchedAtlas.AsSpan(508)));
        Assert.Equal(5, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(patchedAtlas.AsSpan(512)));
        Assert.Equal(1024, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(patchedAtlas.AsSpan(520)));
        Assert.Equal(0.5f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(patchedAtlas.AsSpan(380)));

        // 3. ExpandFallbackMaterial updates _GradientScale=6f and _TextureWidth/_TextureHeight=1024f
        byte[] matObj = new byte[1400];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(matObj.AsSpan(664), 10f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(matObj.AsSpan(1336), 512f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(matObj.AsSpan(1360), 512f);

        byte[] patchedMat = StarTruckerDisplay.ExpandFallbackMaterial(matObj);
        Assert.Equal(6f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(patchedMat.AsSpan(664)));
        Assert.Equal(1024f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(patchedMat.AsSpan(1336)));
        Assert.Equal(1024f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(patchedMat.AsSpan(1360)));

        // 4. End-to-end verification against live Star Trucker assets when LOCARA_STEAM_COMMON is configured locally
        string? liveGame = OptionalLocalSteamGame("Star Trucker");
        string liveArchive = liveGame != null ? Path.Combine(liveGame, StarTruckerCatalog.LanguageArchive) : "";
        if (liveGame != null && File.Exists(liveArchive) && StarTruckerDisplay.UnsupportedReason(liveGame, liveArchive, default) == null)
        {
            using var ws = new TempWorkspace();
            var changes = StarTruckerDisplay.WriteAssets(liveGame, ws.Root, default);
            Assert.Equal(2, changes.Count);
            Assert.True(File.Exists(Path.Combine(ws.Root, "Star Trucker_Data", "sharedassets0.assets")));
            Assert.True(File.Exists(Path.Combine(ws.Root, "Star Trucker_Data", "resources.assets")));
        }
    }

    [Fact]
    public void CompiledUnityMetadataFilesAreIgnoredWhileRealStreamingAssetsTextIsScanned()
    {
        using var ws = new TempWorkspace();
        string unityGame = Directory.CreateDirectory(ws.PathOf("unity-compiled")).FullName;
        string dataDir = Directory.CreateDirectory(Path.Combine(unityGame, "9Kings_Data")).FullName;
        File.WriteAllText(Path.Combine(dataDir, "RuntimeInitializeOnLoads.json"), """{"root":[{"assemblyName":"Sirenix.Serialization","methodName":"InitializeRuntime"}]}""");
        File.WriteAllText(Path.Combine(dataDir, "ScriptingAssemblies.json"), """{"names":["UnityEngine.dll","Assembly-CSharp.dll"]}""");
        string aaDir = Directory.CreateDirectory(Path.Combine(dataDir, "StreamingAssets", "aa")).FullName;
        File.WriteAllText(Path.Combine(aaDir, "settings.json"), """{"m_CatalogLocations":["catalog.bin"]}""");
        string linkDir = Directory.CreateDirectory(Path.Combine(aaDir, "AddressablesLink")).FullName;
        File.WriteAllText(Path.Combine(linkDir, "link.xml"), "<linker><assembly fullname=\"Assembly-CSharp\"/></linker>");
        string bundlesDir = Directory.CreateDirectory(Path.Combine(aaDir, "StandaloneWindows64")).FullName;
        File.WriteAllBytes(Path.Combine(bundlesDir, "localization-string-tables-english(en)_assets_all.bundle"), [0x55, 0x6E, 0x69, 0x74, 0x79]);

        using (var store = ProjectStore.Create(ws.PathOf("unity-project"), "9 Kings Demo", unityGame))
        {
            var service = new ProjectService(store);
            var cap = service.Capabilities();
            Assert.Equal("unity", cap.Engine);
            Assert.False(cap.CanRead);

            var scan = service.Scan();
            Assert.Equal(0, scan.Units);
            Assert.NotEmpty(scan.Diagnostics);
            Assert.Contains(".bundle", scan.Diagnostics[0].Message, StringComparison.Ordinal);
        }

        // Now add a real open localization file inside StreamingAssets — only THAT file should be extracted.
        string locDir = Directory.CreateDirectory(Path.Combine(dataDir, "StreamingAssets", "Localization")).FullName;
        File.WriteAllText(Path.Combine(locDir, "en.json"), """{"play":"Play Game","quit":"Quit"}""");
        using (var store = ProjectStore.Create(ws.PathOf("unity-open-project"), "Unity Open Loc", unityGame))
        {
            var service = new ProjectService(store);
            var scan = service.Scan();
            Assert.Equal(1, scan.Files);
            Assert.Equal(2, scan.Units);
            Assert.DoesNotContain(store.Units(), u => u.Source.Contains("Sirenix", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Lz4BlockCompressionAndDecompressionRoundtrip()
    {
        byte[][] payloads =
        [
            [],
            Encoding.UTF8.GetBytes("Short"),
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("King of Nothing — ملك العدم | ", 400))),
            Enumerable.Range(0, 4096).Select(i => (byte)((i * 37 + 13) & 0xFF)).ToArray()
        ];

        foreach (byte[] payload in payloads)
        {
            byte[] compressed = Lz4Block.Compress(payload);
            byte[] restored = Lz4Block.Decompress(compressed, payload.Length);
            Assert.Equal(payload, restored);
        }
    }

    [Fact]
    public void UnityAddressablesCatalogPatchesBinaryAndJsonCrcAndBundleSize()
    {
        uint oldCrc = 0x7A33170Bu, oldSize = 5114u;
        uint newCrc = 0x11223344u, newSize = 6200u;

        // 1. Binary catalog.bin patch
        byte[] bin = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bin.AsSpan(20), oldCrc);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bin.AsSpan(24), oldSize);

        var update = new UnityAddressablesCatalog.BundleUpdate("aa/StandaloneWindows64/en.bundle", oldCrc, oldSize, newCrc, newSize);
        byte[] patchedBin = UnityAddressablesCatalog.PatchBinaryCatalog(bin, [update], out int binCount);
        Assert.Equal(1, binCount);
        Assert.Equal(bin.Length, patchedBin.Length);
        Assert.Equal(newCrc, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(patchedBin.AsSpan(20)));
        Assert.Equal(newSize, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(patchedBin.AsSpan(24)));

        // 2. JSON catalog.json patch
        byte[] json = Encoding.UTF8.GetBytes("""{"m_Hash":"abc","m_Crc":2050168587,"m_BundleSize":5114,"m_Timeout":0}""");
        byte[] patchedJson = UnityAddressablesCatalog.PatchJsonCatalog(json, [update], out int jsonCount);
        Assert.Equal(1, jsonCount);
        string patchedJsonText = Encoding.UTF8.GetString(patchedJson);
        Assert.Contains($"\"m_Crc\":{newCrc}", patchedJsonText);
        Assert.Contains($"\"m_BundleSize\":{newSize}", patchedJsonText);
    }

    [Fact]
    public void NineKingsDemoRealUnityI2BundleScanTranslateFontPatchBuildAndCatalogSync()
    {
        string? gameDir = OptionalLocalSteamGame("9 Kings Demo");
        if (gameDir == null) return;
        string enBundle = Path.Combine(gameDir, "9Kings_Data", "StreamingAssets", "aa", "StandaloneWindows64",
            "localization-string-tables-english(en)_assets_all.bundle");
        string dataUnity3d = Path.Combine(gameDir, "9Kings_Data", "data.unity3d");
        if (!File.Exists(enBundle) || !File.Exists(dataUnity3d)) return;

        // Skip live-disk smoke test cleanly if the user currently has permanent Arabic installed on their live Steam copy.
        byte[]? liveRes = UnityFsStream.TryReadNode(dataUnity3d, "resources.assets");
        var liveSf = liveRes != null ? UnitySerializedFile.TryRead(liveRes) : null;
        if (liveSf == null || !UnityTmpArabicPatcher.TryPatch(liveSf, null, new Dictionary<long, byte[]>())) return;

        using var ws = new TempWorkspace();
        using var store = ProjectStore.Create(ws.PathOf("nine-kings-project"), "9 Kings Demo", gameDir);
        var service = new ProjectService(store);

        var cap = service.Capabilities();
        Assert.Equal("unity", cap.Engine);
        Assert.True(cap.CanRead);
        Assert.Equal(GameLaunch.Steam, cap.Launch);

        var scan = service.Scan();
        Assert.Equal(5, scan.Files); // data.unity3d (I2) + 4 StringTable bundles (en, zh, ja, pt)

        var scannedFiles = service.ScannedFiles();
        var i2File = scannedFiles.Single(f => f.Format == "unity-i2");
        var enFile = scannedFiles.Single(f => f.Path.Contains("english(en)", StringComparison.OrdinalIgnoreCase));
        Assert.True(i2File.Included);
        Assert.Equal(2083, i2File.TotalUnits);
        Assert.True(enFile.Included);
        Assert.Equal(100, enFile.TotalUnits);

        // Non-English bundles must be auto-excluded and labeled SecondaryLanguage
        var secondaryBundles = scannedFiles.Where(f => f.Format == "unity-bundle" && !ReferenceEquals(f, enFile)).ToList();
        Assert.Equal(3, secondaryBundles.Count);
        Assert.All(secondaryBundles, f =>
        {
            Assert.False(f.Included);
            Assert.Equal(FileQualityVerdict.SecondaryLanguage, f.Verdict);
        });

        // Translate real live I2Languages Options strings (visible in 9 Kings Demo OPTIONS screen!)
        var plotDamage = store.Units("MainMenu_Options_PlotDamageToggle").Single(u => u.Path == i2File.Path);
        var aspectRatio = store.Units("MainMenu_Options_ResolutionIndex").Single(u => u.Path == i2File.Path);
        plotDamage.Target = "ضرر الساحة";
        aspectRatio.Target = "نسبة العرض";
        store.Save(plotDamage, approve: true);
        store.Save(aspectRatio, approve: true);

        // Also translate one entry in en.bundle to verify Addressables catalog.bin sync alongside data.unity3d
        var bundleUnit = store.Units("", 0, 5000).First(u => u.Path == enFile.Path);
        bundleUnit.Target = "اختبار حزمة";
        store.Save(bundleUnit, approve: true);

        var preview = service.Preview(final: true);
        Assert.Equal(3, preview.ChangedFiles.Count);
        Assert.Contains(preview.ChangedFiles, p => p.EndsWith("data.unity3d", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.ChangedFiles, p => p.EndsWith("localization-string-tables-english(en)_assets_all.bundle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.ChangedFiles, p => p.EndsWith("catalog.bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("I2 Localization", preview.Notice, StringComparison.Ordinal);

        var build = service.Build(ws.PathOf("nine-kings-out"), final: true, reviewed: preview);
        Assert.Equal(3, build.Translations);
        Assert.Equal(3, build.Files); // Fast overlay-only export (data.unity3d + en.bundle + catalog.bin)

        // 1. Verify rebuilt data.unity3d: I2Languages has shaped Arabic + TMP Arabic fallback font injected!
        string rebuiltDataPath = Path.Combine(build.Directory, "resources", "9Kings_Data", "data.unity3d");
        Assert.True(File.Exists(rebuiltDataPath));
        var reExtractedI2 = UnityI2Format.ExtractFromFile(rebuiltDataPath);
        Assert.Equal(2083, reExtractedI2.Count);
        string expectedPlotDamage = StarTruckerDisplay.Shape("ضرر الساحة");
        string expectedAspectRatio = StarTruckerDisplay.Shape("نسبة العرض");
        Assert.Equal(expectedPlotDamage, reExtractedI2.Single(s => s.Locator == plotDamage.Locator).Source);
        Assert.Equal(expectedAspectRatio, reExtractedI2.Single(s => s.Locator == aspectRatio.Locator).Source);

        byte[] rebuiltRes = UnityFsStream.TryReadNode(rebuiltDataPath, "resources.assets")!;
        var sf = UnitySerializedFile.TryRead(rebuiltRes)!;
        var tmpSettingsObj = sf.Objects.Single(o => o.PathId == 14786);
        var tmpSettingsBytes = sf.Slice(tmpSettingsObj);
        // Verify fallback 14782 was prepended into TMP Settings m_fallbackFontAssets at offset 172 (after 2 aligned bools)
        Assert.True(tmpSettingsBytes.Length == 312, $"Expected TMP Settings to grow from 300 to 312 bytes, got {tmpSettingsBytes.Length}");
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[172..]));
        Assert.Equal(14782L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(tmpSettingsBytes[180..]));
        // Verify m_matchMaterialPreset (1) and m_defaultSpriteAsset (14784) stayed cleanly aligned right after!
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[188..]));
        Assert.Equal(14784L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(tmpSettingsBytes[200..]));
        // Verify runtime GPOS 'kern' was disabled to prevent reversed-order kerning glitches on visual-order Arabic (ر/ز)
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[56..]));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[64..]));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[84..]));
        // Verify LiberationSans SDF - Fallback m_FaceInfo.m_Scale was scaled down to 0.80f to prevent line-wrap overlap
        var fallbackObj = sf.Objects.Single(o => o.PathId == 14782);
        Assert.Equal(0.80f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(sf.Slice(fallbackObj)[116..]), 3);

        // 2. Verify rebuilt en.bundle + patched catalog.bin CRC32 & Size
        string rebuiltBundlePath = Path.Combine(build.Directory, "resources",
            "9Kings_Data", "StreamingAssets", "aa", "StandaloneWindows64",
            "localization-string-tables-english(en)_assets_all.bundle");
        string rebuiltCatalogPath = Path.Combine(build.Directory, "resources",
            "9Kings_Data", "StreamingAssets", "aa", "catalog.bin");
        byte[] rebuiltBundleBytes = File.ReadAllBytes(rebuiltBundlePath);
        var reExtractedBundle = new UnityBundleFormat().Extract(rebuiltBundleBytes);
        Assert.Equal(100, reExtractedBundle.Count);
        Assert.Contains(reExtractedBundle, s => s.Source == "اختبار حزمة");

        uint newCrc = UnityBundleFormat.ComputeUncompressedCrc32(rebuiltBundleBytes);
        uint newSize = checked((uint)rebuiltBundleBytes.Length);
        byte[] patchedCat = File.ReadAllBytes(rebuiltCatalogPath);
        bool foundNewPair = false;
        for (int i = 0; i <= patchedCat.Length - 8; i++)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(patchedCat.AsSpan(i)) == newCrc
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(patchedCat.AsSpan(i + 4)) == newSize)
            {
                foundNewPair = true;
                break;
            }
        }
        Assert.True(foundNewPair, "Patched catalog.bin did not contain the updated (m_Crc, m_BundleSize) pair.");
    }

    [Fact]
    public void UnityBundleFormatSyntheticTextAssetAndUnityFsRoundtrip()
    {
        // Build a valid synthetic SerializedFile v22 containing one TextAsset (classId = 49) inside a UnityFS v8 LZ4 bundle.
        byte[] textAssetObj = BuildSyntheticTextAsset("StoryIntro", "Welcome to the Citadel, traveler!");
        byte[] cab = BuildSyntheticSerializedFileV22(classId: 49, pathId: 1001, objectData: textAssetObj);
        var template = new UnityFsBundle
        {
            Version = 8,
            UnityVersion = "5.x.x",
            UnityRevision = "6000.3.8f1",
            Flags = 0x243u,
            DataHash = new byte[16]
        };
        byte[] bundleBytes = template.Write([new UnityFsNode("CAB-synthetic", 4u, cab)], out uint crcBefore);
        Assert.Equal(crcBefore, UnityBundleFormat.ComputeUncompressedCrc32(bundleBytes));

        var format = new UnityBundleFormat();
        var slots = format.Extract(bundleBytes);
        var slot = Assert.Single(slots);
        Assert.Equal("ta:0:1001", slot.Locator);
        Assert.Equal("Welcome to the Citadel, traveler!", slot.Source);
        Assert.Contains("StoryIntro", slot.Context, StringComparison.Ordinal);

        byte[] rebuilt = format.Apply(bundleBytes, new Dictionary<string, string>
        {
            [slot.Locator] = "مرحباً بك في القلعة أيها المسافر!"
        });
        Assert.NotEqual(crcBefore, UnityBundleFormat.ComputeUncompressedCrc32(rebuilt));

        var after = format.Extract(rebuilt);
        Assert.Single(after);
        Assert.Equal("مرحباً بك في القلعة أيها المسافر!", after[0].Source);
    }

    [Fact]
    public void StarTruckerDisplaySmartArabicWordWrapTopToBottomWithTagsAndNumbers()
    {
        // 1. Short UI labels stay single-line under AutoWrap.
        const string shortLabel = "ضرر الساحة";
        Assert.Equal(StarTruckerDisplay.Shape(shortLabel), StarTruckerDisplay.Shape(shortLabel, StarTruckerDisplay.AutoWrap));
        Assert.DoesNotContain('\n', StarTruckerDisplay.Shape(shortLabel, StarTruckerDisplay.AutoWrap));

        // 2. Long decree sentence (9 Kings Demo) must wrap top-to-bottom so Line 1 starts with "تكتسب" and last line ends with "الفولاذي."
        const string decree = "تكتسب جميع القوات +3 طبقات من الكساء الفولاذي.";
        Assert.DoesNotContain('\n', StarTruckerDisplay.Shape(decree, maxLineChars: 0));

        string autoShaped = StarTruckerDisplay.Shape(decree, StarTruckerDisplay.AutoWrap);
        string[] autoLines = autoShaped.Split('\n');
        Assert.True(autoLines.Length >= 2, $"Expected multi-line wrap, got: {autoShaped}");
        Assert.Contains(StarTruckerDisplay.Shape("تكتسب"), autoLines[0]);
        Assert.Contains("3", autoShaped);
        Assert.Contains("+", autoShaped);
        Assert.Contains(StarTruckerDisplay.Shape("الفولاذي."), autoLines[^1]);
        Assert.DoesNotContain(StarTruckerDisplay.Shape("تكتسب"), autoLines[^1]);

        // Narrow explicit width (20) wraps into balanced lines with top-to-bottom order.
        string narrowShaped = StarTruckerDisplay.Shape(decree, maxLineChars: 20);
        string[] narrowLines = narrowShaped.Split('\n');
        Assert.True(narrowLines.Length >= 3);
        Assert.Contains(StarTruckerDisplay.Shape("تكتسب"), narrowLines[0]);
        Assert.Contains(StarTruckerDisplay.Shape("الفولاذي."), narrowLines[^1]);

        // 3. Paired rich-text tags spanning a wrap boundary are cleanly closed on Line 1 and reopened on Line 2.
        const string colored = "تكتسب <color=#FFD700>جميع القوات +3 طبقات</color> من الكساء الفولاذي.";
        string coloredShaped = StarTruckerDisplay.Shape(colored, maxLineChars: 20);
        string[] coloredLines = coloredShaped.Split('\n');
        Assert.True(coloredLines.Length >= 2);
        Assert.DoesNotContain("<color=#FFD700></color>", coloredShaped);
        foreach (string line in coloredLines)
            Assert.True(Tokens.ValidTagNesting(line, line), $"Invalid tag nesting in wrapped line: {line}");
        Assert.Contains("<color=#FFD700>", coloredLines[0]);
        Assert.Contains("</color>", coloredLines[0]);
        Assert.Contains("<color=#FFD700>", coloredLines[1]);
        Assert.Contains("</color>", coloredLines[1]);

        // 4. Nested paired tags across wrapped lines preserve valid nesting on every line.
        const string nested = "<b><color=#FFAA00>تكتسب جميع القوات +3 طبقات من الكساء الفولاذي.</color></b>";
        string nestedShaped = StarTruckerDisplay.Shape(nested, maxLineChars: 20);
        string[] nestedLines = nestedShaped.Split('\n');
        Assert.True(nestedLines.Length >= 2);
        foreach (string line in nestedLines)
        {
            Assert.True(Tokens.ValidTagNesting(line, line), $"Invalid nested tags in line: {line}");
            Assert.Contains("<b>", line);
            Assert.Contains("</b>", line);
            Assert.Contains("<color=#FFAA00>", line);
            Assert.Contains("</color>", line);
        }

        // 5. Placeholders & inline void tags (<sprite=0>, {[UNIT]}, {0}) are never split across line breaks.
        const string withTokens = "تكتسب {[UNIT_NAME]} <sprite=0> +{0} دروع إضافية خلال هذه الجولة بالكامل.";
        string tokenShaped = StarTruckerDisplay.Shape(withTokens, maxLineChars: 22);
        Assert.Contains("{[UNIT_NAME]}", tokenShaped);
        Assert.Contains("<sprite=0>", tokenShaped);
        Assert.Contains("{0}", tokenShaped);
        Assert.True(tokenShaped.Split('\n').Length >= 2);

        // 6. Existing explicit line breaks (\n and <br>) are preserved while long paragraphs still wrap.
        const string multiPara = "عنوان قصير\nتكتسب جميع القوات +3 طبقات من الكساء الفولاذي.<br>سطر أخير";
        string multiShaped = StarTruckerDisplay.Shape(multiPara, maxLineChars: 22);
        Assert.Contains("<br>", multiShaped);
        string[] beforeBr = multiShaped.Split("<br>")[0].Split('\n');
        Assert.True(beforeBr.Length >= 3);
        Assert.Equal(StarTruckerDisplay.Shape("عنوان قصير"), beforeBr[0]);
        Assert.Contains(StarTruckerDisplay.Shape("تكتسب"), beforeBr[1]);
        Assert.Contains(StarTruckerDisplay.Shape("الفولاذي."), beforeBr[^1]);
    }

    [Fact]
    public void UnityI2FormatSyntheticDataUnity3dPatchFontScaleWrapAnd128KiBInvariant()
    {
        using var ws = new TempWorkspace();
        string gameDir = ws.PathOf("synthetic-unity-game");
        string dataDir = Path.Combine(gameDir, "Synth_Data");
        Directory.CreateDirectory(dataDir);

        const long fontPathId = 973;
        const long fallbackPathId = 14782;
        const long tmpSettingsPathId = 14786;
        const long i2PathId = 20001;

        byte[] resAssets = BuildSyntheticSerializedFileV22Multi([
            (128, fontPathId, BuildSyntheticLiberationSansFont()),
            (114, fallbackPathId, BuildSyntheticTmpFallbackAsset()),
            (114, tmpSettingsPathId, BuildSyntheticUnity6TmpSettings()),
            (114, i2PathId, BuildSyntheticI2LanguagesAsset([
                ("MainMenu_Options_PlotDamageToggle", "Dégâts", "Plot Damage"),
                ("Decree_SteelPlating", "Toutes les troupes", "All troops gain <color=#FFD700>+3 stacks</color> of Steel Plating.")
            ]))
        ]);

        // Add a 200 KiB trailing .resS node with realistic pseudo-random data so UnityFsStream.PatchNode must enforce the 128 KiB interior block invariant.
        byte[] trailingResS = new byte[200_000];
        new Random(42).NextBytes(trailingResS);

        var bundleTemplate = new UnityFsBundle
        {
            Version = 8,
            UnityVersion = "5.x.x",
            UnityRevision = "6000.3.8f1",
            Flags = 0x243u,
            DataHash = new byte[16]
        };
        byte[] dataUnity3dBytes = bundleTemplate.Write(
            [
                new UnityFsNode("resources.assets", 4u, resAssets),
                new UnityFsNode("resources.assets.resS", 0u, trailingResS)
            ], out _);
        string dataUnity3dPath = Path.Combine(dataDir, "data.unity3d");
        File.WriteAllBytes(dataUnity3dPath, dataUnity3dBytes);

        byte[]? readBackRes = UnityFsStream.TryReadNode(dataUnity3dPath, "resources.assets");
        Assert.NotNull(readBackRes);
        Assert.NotNull(UnitySerializedFile.TryRead(readBackRes));
        Assert.True(UnityI2Format.HasI2Languages(dataUnity3dPath), $"Extract count={UnityI2Format.ExtractFromResources(readBackRes).Count}");

        using var store = ProjectStore.Create(ws.PathOf("synth-project"), "Synthetic Unity I2", gameDir);
        var service = new ProjectService(store);

        var scan = service.Scan();
        Assert.Equal(1, scan.Files);
        Assert.Equal(2, scan.Units);

        var plotUnit = store.Units("MainMenu_Options_PlotDamageToggle").Single();
        var decreeUnit = store.Units("Decree_SteelPlating").Single();
        Assert.Equal("Plot Damage", plotUnit.Source); // Must pick English (index 1), not French (index 0)
        Assert.Equal("All troops gain <color=#FFD700>+3 stacks</color> of Steel Plating.", decreeUnit.Source);

        plotUnit.Target = "ضرر الساحة";
        decreeUnit.Target = "تكتسب <color=#FFD700>جميع القوات +3 طبقات</color> من الكساء الفولاذي.";
        store.Save(plotUnit, approve: true);
        store.Save(decreeUnit, approve: true);

        // Changing Unity Arabic wrap/scale settings must invalidate the preview fingerprint.
        var defaultPreview = service.Preview(final: true);
        store.Set("unity_arabic_wrap", "20");
        store.Set("unity_arabic_font_scale", "0.85");
        var customPreview = service.Preview(final: true);
        Assert.NotEqual(defaultPreview.Fingerprint, customPreview.Fingerprint);
        Assert.Contains("20 حرف", customPreview.Notice);
        Assert.Contains("85%", customPreview.Notice);

        var build = service.Build(ws.PathOf("synth-out"), final: true, reviewed: customPreview);
        Assert.Equal(2, build.Translations);
        Assert.Equal(1, build.Files);

        string rebuiltDataPath = Path.Combine(build.Directory, "resources", "Synth_Data", "data.unity3d");
        Assert.True(File.Exists(rebuiltDataPath));

        // Trailing node bytes must remain bit-for-bit intact after 128 KiB gap-padded patching.
        byte[]? rebuiltResS = UnityFsStream.TryReadNode(rebuiltDataPath, "resources.assets.resS");
        Assert.NotNull(rebuiltResS);
        Assert.Equal(trailingResS, rebuiltResS);

        // Re-extracted I2 strings: short label single-line shaped, long decree wrapped top-to-bottom with valid tags.
        var reExtracted = UnityI2Format.ExtractFromFile(rebuiltDataPath);
        Assert.Equal(2, reExtracted.Count);
        Assert.Equal(StarTruckerDisplay.Shape("ضرر الساحة"), reExtracted.Single(s => s.Locator == plotUnit.Locator).Source);
        string rebuiltDecree = reExtracted.Single(s => s.Locator == decreeUnit.Locator).Source;
        string[] decreeLines = rebuiltDecree.Split('\n');
        Assert.True(decreeLines.Length >= 3);
        Assert.Contains(StarTruckerDisplay.Shape("تكتسب"), decreeLines[0]);
        Assert.Contains(StarTruckerDisplay.Shape("الفولاذي."), decreeLines[^1]);
        foreach (string line in decreeLines)
            Assert.True(Tokens.ValidTagNesting(line, line));

        // Verify TMP Settings fallback prepending, alignment, GPOS 'kern' disable, and custom 0.85f fallback scale.
        byte[] rebuiltRes = UnityFsStream.TryReadNode(rebuiltDataPath, "resources.assets")!;
        var sf = UnitySerializedFile.TryRead(rebuiltRes)!;

        var tmpSettingsBytes = sf.Slice(sf.Objects.Single(o => o.PathId == tmpSettingsPathId));
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[176..]));
        Assert.Equal(fallbackPathId, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(tmpSettingsBytes[184..]));
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[192..]));
        Assert.Equal(14784L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(tmpSettingsBytes[200..]));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[56..]));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[64..]));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tmpSettingsBytes[84..]));

        var fallbackBytes = sf.Slice(sf.Objects.Single(o => o.PathId == fallbackPathId));
        Assert.Equal(0.85f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(fallbackBytes[116..]), 3);

        var fontBytes = sf.Slice(sf.Objects.Single(o => o.PathId == fontPathId));
        Assert.True(fontBytes.IndexOf("Noto Sans Arabic"u8) >= 0);
    }

    [Fact]
    public void UnrealLocresFormatAllVersionsV0ToV3ArabicUtf16Roundtrip()
    {
        foreach (byte version in new byte[] { 0, 1, 2, 3 })
        {
            var doc = new UnrealLocresFormat.LocresDocument(version, [
                new UnrealLocresFormat.LocresNamespace(0x11223344u, "UI:MainMenu", [
                    new UnrealLocresFormat.LocresEntry(0xAABBCC01u, "StartBtn", 0xDEADBEEFu, "Start Game"),
                    new UnrealLocresFormat.LocresEntry(0xAABBCC02u, "SharedYes", 0xCAFEBABEu, "Confirm"),
                    new UnrealLocresFormat.LocresEntry(0xAABBCC03u, "SharedYes2", 0x12345678u, "Confirm")
                ]),
                new UnrealLocresFormat.LocresNamespace(0x55667788u, "", [
                    new UnrealLocresFormat.LocresEntry(0x99887766u, "HeroLine", 0x0BADF00Du, "Welcome, {0}!")
                ])
            ]);

            byte[] original = UnrealLocresFormat.Write(doc);
            var extracted = UnrealLocresFormat.Instance.Extract(original);
            Assert.Equal(4, extracted.Count);
            Assert.Contains(extracted, s => s.Source == "Start Game" && s.Context == "UI:MainMenu / StartBtn");
            Assert.Contains(extracted, s => s.Source == "Welcome, {0}!" && s.Context == "HeroLine");

            var startSlot = extracted.Single(s => s.Source == "Start Game");
            var heroSlot = extracted.Single(s => s.Source == "Welcome, {0}!");
            var confirm1 = extracted.First(s => s.Source == "Confirm");

            byte[] updated = UnrealLocresFormat.Instance.Apply(original, new Dictionary<string, string>
            {
                [startSlot.Locator] = "ابدأ اللعبة",
                [heroSlot.Locator] = "مرحباً بك، {0}!",
                [confirm1.Locator] = "تأكيد"
            });

            Assert.True(UnrealLocresFormat.TryParse(updated, out var reparsed) && reparsed != null);
            Assert.Equal(version, reparsed.Version);
            Assert.Equal(2, reparsed.Namespaces.Count);
            if (version >= 2)
            {
                Assert.Equal(0x11223344u, reparsed.Namespaces[0].NameHash);
                Assert.Equal(0xAABBCC01u, reparsed.Namespaces[0].Entries[0].KeyHash);
            }
            Assert.Equal(0xDEADBEEFu, reparsed.Namespaces[0].Entries[0].SourceStringHash);
            Assert.Equal("ابدأ اللعبة", reparsed.Namespaces[0].Entries[0].Value);
            Assert.Equal("تأكيد", reparsed.Namespaces[0].Entries[1].Value);
            Assert.Equal("Confirm", reparsed.Namespaces[0].Entries[2].Value);
            Assert.Equal("مرحباً بك، {0}!", reparsed.Namespaces[1].Entries[0].Value);
        }
    }

    [Fact]
    public void UnrealPakV8AndV11DropInPatchPakBuildInPlaceInstallAndRestore()
    {
        foreach (int pakVersion in new[] { 8, 11, 12 })
        {
            using var ws = new TempWorkspace();
            string gameDir = ws.PathOf($"ue-game-v{pakVersion}");
            string paksDir = Path.Combine(gameDir, "MyGame", "Content", "Paks");
            string engineCfgDir = Path.Combine(gameDir, "Engine", "Config");
            Directory.CreateDirectory(paksDir);
            Directory.CreateDirectory(engineCfgDir);

            // Unrelated Chromium pak & UE staged build metadata must be ignored cleanly.
            File.WriteAllBytes(Path.Combine(gameDir, "chrome_100_percent.pak"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(engineCfgDir, "StagedBuild_MyGame.ini"), "[Staging]\nVersion=1\n");

            byte[] engineBoilerplateLocres = UnrealLocresFormat.Write(new UnrealLocresFormat.LocresDocument(3, [
                new UnrealLocresFormat.LocresNamespace(1u, "Online", [
                    new UnrealLocresFormat.LocresEntry(2u, "Err", 3u, "Access denied")
                ])
            ]));
            byte[] frenchLocres = UnrealLocresFormat.Write(new UnrealLocresFormat.LocresDocument(3, [
                new UnrealLocresFormat.LocresNamespace(10u, "Game", [
                    new UnrealLocresFormat.LocresEntry(20u, "Title", 30u, "Jouer")
                ])
            ]));
            byte[] englishLocres = UnrealLocresFormat.Write(new UnrealLocresFormat.LocresDocument(3, [
                new UnrealLocresFormat.LocresNamespace(10u, "Game", [
                    new UnrealLocresFormat.LocresEntry(20u, "Title", 30u, "Play Adventure"),
                    new UnrealLocresFormat.LocresEntry(21u, "Quit", 31u, "Exit Game")
                ])
            ]));

            byte[] dummyLatinTtf = [0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
            byte[] ufontPayload = pakVersion >= 10 ? UnrealPakFormat.WrapUe5Ufont(dummyLatinTtf) : dummyLatinTtf;

            string basePakPath = Path.Combine(paksDir, "MyGame-Windows.pak");
            UnrealPakCodec.WritePatchPak(basePakPath, "../../../", [
                new UnrealPakPatchFile("Engine/Plugins/Online/OnlineSubsystem/Content/Localization/OnlineSubsystem/en/OnlineSubsystem.locres", engineBoilerplateLocres),
                new UnrealPakPatchFile("MyGame/Content/Localization/Game/fr/Game.locres", frenchLocres),
                new UnrealPakPatchFile("MyGame/Content/Localization/Game/en/Game.locres", englishLocres),
                new UnrealPakPatchFile("MyGame/Content/Fonts/MainFont.ufont", ufontPayload),
                new UnrealPakPatchFile("MyGame/Content/Fonts/NotoSansJP-Regular.ufont", ufontPayload),
                new UnrealPakPatchFile("Engine/Content/EngineFonts/Faces/RobotoRegular.ufont", ufontPayload)
            ], targetVersion: pakVersion);
            string basePakHashBefore = PackageInstaller.FileHash(basePakPath);

            using var store = ProjectStore.Create(ws.PathOf($"ue-proj-v{pakVersion}"), $"UE v{pakVersion}", gameDir);
            var service = new ProjectService(store);

            var cap = service.Capabilities();
            Assert.Equal("unreal", cap.Engine);
            Assert.True(cap.CanRead);

            var scan = service.Scan();
            Assert.Equal(1, scan.Files);
            Assert.Equal(2, scan.Units); // Only English game .locres (no Engine boilerplate, no French!)

            var playUnit = store.Units("Play Adventure").Single();
            playUnit.Target = "ابدأ المغامرة";
            store.Save(playUnit, approve: true);

            var preview = service.Preview(final: true);
            string expectedModRel = "MyGame/Content/Paks/MyGame-Windows_Locara_P.pak";
            Assert.Equal([expectedModRel], preview.ChangedFiles);
            Assert.Contains("MyGame-Windows_Locara_P.pak", preview.Notice);

            // Direct In-Place Install deploys ONLY the drop-in *_Locara_P.pak without touching base pak!
            string outDir = ws.PathOf($"ue-out-v{pakVersion}");
            var (build, install, updatedExisting) = PackageInstaller.DeployInPlace(store, outDir, final: true);
            Assert.False(updatedExisting);
            Assert.Equal(1, build.Files);
            Assert.Equal(1, build.Translations);
            Assert.Equal(1, install.Files);
            Assert.Equal(basePakHashBefore, PackageInstaller.FileHash(basePakPath));

            string installedModPath = Path.Combine(gameDir, "MyGame", "Content", "Paks", "MyGame-Windows_Locara_P.pak");
            Assert.True(File.Exists(installedModPath));

            // Verify generated patch pak format version (v8/v11/v12), mount point, UTF-16LE Arabic .locres, AND bilingual Arabic .ufont injection!
            var modInfo = UnrealPakCodec.ReadIndex(installedModPath);
            Assert.Equal(pakVersion >= 12 ? 12 : pakVersion >= 10 ? 11 : 8, modInfo.Version);
            Assert.Equal("../../../", modInfo.MountPoint);
            Assert.Equal(3, modInfo.Entries.Count); // Game.locres + MainFont.ufont + RobotoRegular.ufont (CJK NotoSansJP skipped!)
            Assert.DoesNotContain(modInfo.Entries, e => e.Path.Contains("NotoSansJP", StringComparison.OrdinalIgnoreCase));

            var modEntry = modInfo.Entries.Single(e => e.Path == "MyGame/Content/Localization/Game/en/Game.locres");
            byte[] modLocresBytes = UnrealPakCodec.ReadEntryBytes(installedModPath, modEntry, modInfo.Version, modInfo.CompressionMethods);
            var modSlots = UnrealLocresFormat.Instance.Extract(modLocresBytes);
            Assert.Contains(modSlots, s => s.Source == "ابدأ المغامرة");
            Assert.Contains(modSlots, s => s.Source == "Exit Game");

            var mainFontEntry = modInfo.Entries.Single(e => e.Path == "MyGame/Content/Fonts/MainFont.ufont");
            byte[] mainFontBytes = UnrealPakCodec.ReadEntryBytes(installedModPath, mainFontEntry, modInfo.Version, modInfo.CompressionMethods);
            if (pakVersion >= 10)
            {
                Assert.True(UnrealPakFormat.IsUe5WrappedUfont(mainFontBytes));
                Assert.True(ArabicFontCatalog.SupportsLatinAndArabic(mainFontBytes.AsSpan(4, mainFontBytes.Length - 8)));

                // Verify v11 PathHashIndex blob includes BOTH PathHash map AND PrunedDirectoryIndex (required by UE5 LoadIndexInternal).
                byte[] pakRaw = File.ReadAllBytes(installedModPath);
                long idxOff = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(pakRaw.AsSpan(pakRaw.Length - 204 + 8));
                int p = checked((int)idxOff);
                Assert.True(UnrealLocresFormat.TryReadFString(pakRaw, ref p, out _)); // mountPoint
                p += 4 + 8 + 4; // entryCount + seed + bHasPathHash
                long phOff = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(pakRaw.AsSpan(p)); p += 8;
                long phSize = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(pakRaw.AsSpan(p));
                int phCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(pakRaw.AsSpan(checked((int)phOff)));
                Assert.Equal(3, phCount);
                Assert.True(phSize > 4 + phCount * 12, "UE5 PathHashIndex must include trailing PrunedDirectoryIndex.");
            }
            else
            {
                Assert.True(ArabicFontCatalog.SupportsLatinAndArabic(mainFontBytes));
            }

            // Status detects active in-place mod, and 1-click Restore removes Locara_Arabic_P.pak and empty ~mods folder.
            var state = PackageInstaller.GetInPlaceStatus(store);
            Assert.NotNull(state);
            Assert.False(state.ModifiedExternally);
            Assert.Equal(1, state.Files);

            int restored = PackageInstaller.RestoreInPlace(store);
            Assert.Equal(1, restored);
            Assert.False(File.Exists(installedModPath));
            Assert.False(Directory.Exists(Path.Combine(paksDir, "~mods")));
            Assert.Null(PackageInstaller.GetInPlaceStatus(store));
        }
    }

    [Fact]
    public void UnrealPakEncryptedIndexGivesClearDiagnostic()
    {
        using var ws = new TempWorkspace();
        string gameDir = ws.PathOf("ue-encrypted");
        string paksDir = Path.Combine(gameDir, "SecretGame", "Content", "Paks");
        Directory.CreateDirectory(paksDir);
        string pakPath = Path.Combine(paksDir, "SecretGame-Windows.pak");

        // Build minimal v11 footer with bEncryptedIndex = 1
        byte[] dummy = new byte[256];
        int magicPos = dummy.Length - 204;
        dummy[magicPos - 1] = 1; // bEncryptedIndex = true
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dummy.AsSpan(magicPos), UnrealPakCodec.PakMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dummy.AsSpan(magicPos + 4), 11);
        File.WriteAllBytes(pakPath, dummy);

        Assert.Throws<NotSupportedException>(() => UnrealPakCodec.ReadIndex(pakPath));

        using var store = ProjectStore.Create(ws.PathOf("ue-enc-proj"), "Encrypted UE", gameDir);
        var scan = new ProjectService(store).Scan();
        Assert.Equal(0, scan.Files);
        Assert.Contains(scan.Diagnostics, d => d.Message.Contains("AES", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnrealPakV12IoStoreDirectStreamPatchInstallAndRestore()
    {
        using var ws = new TempWorkspace();
        string gameDir = ws.PathOf("ue5-iostore");
        string paksDir = Path.Combine(gameDir, "MyGame", "Content", "Paks");
        Directory.CreateDirectory(paksDir);

        byte[] untouchedCfg = Encoding.UTF8.GetBytes("[Engine]\nKeepMeIntact=True\n");
        byte[] dummyLatinTtf = [0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] ufontPayload = UnrealPakFormat.WrapUe5Ufont(dummyLatinTtf);
        byte[] englishLocres = UnrealLocresFormat.Write(new UnrealLocresFormat.LocresDocument(3, [
            new UnrealLocresFormat.LocresNamespace(10u, "Game", [
                new UnrealLocresFormat.LocresEntry(20u, "Start", 30u, "Start Game"),
                new UnrealLocresFormat.LocresEntry(21u, "Quit", 31u, "Quit Game")
            ])
        ]));

        string basePakPath = Path.Combine(paksDir, "MyGame-Windows.pak");
        UnrealPakCodec.WritePatchPak(basePakPath, "../../../", [
            new UnrealPakPatchFile("Engine/Config/Base.ini", untouchedCfg),
            new UnrealPakPatchFile("MyGame/Content/Localization/Game/en/Game.locres", englishLocres),
            new UnrealPakPatchFile("MyGame/Content/UI/Fonts/MainFont.ufont", ufontPayload)
        ], targetVersion: 12, pathHashSeed: 0x9B56FB58ul);
        // Companion .utoc activates UE5 IoStore direct streaming pak patching
        File.WriteAllBytes(Path.Combine(paksDir, "MyGame-Windows.utoc"), Encoding.ASCII.GetBytes("-==--==--==--==-"));
        string origHash = PackageInstaller.FileHash(basePakPath);

        using var store = ProjectStore.Create(ws.PathOf("ue5-io-proj"), "UE5 IoStore", gameDir);
        var service = new ProjectService(store);
        var scan = service.Scan();
        Assert.Equal(1, scan.Files);
        Assert.Equal(2, scan.Units);

        var startUnit = store.Units("Start Game").Single();
        startUnit.Target = "ابدأ اللعب";
        store.Save(startUnit, approve: true);

        var preview = service.Preview(final: true);
        Assert.Equal(["MyGame/Content/Paks/MyGame-Windows.pak"], preview.ChangedFiles);
        Assert.Contains("IoStore", preview.Notice);

        var (build, install, _) = PackageInstaller.DeployInPlace(store, ws.PathOf("ue5-io-out"), final: true);
        Assert.Equal(1, build.Files);
        Assert.Equal(1, install.Files);
        Assert.NotEqual(origHash, PackageInstaller.FileHash(basePakPath));

        // Verify all 3 entries exist: untouched Base.ini is bit-for-bit intact, Game.locres has Arabic, MainFont.ufont has UE5-wrapped bilingual Arabic font!
        var patchedInfo = UnrealPakCodec.ReadIndex(basePakPath);
        Assert.Equal(12, patchedInfo.Version);
        Assert.Equal(3, patchedInfo.Entries.Count);

        var baseIniEntry = patchedInfo.Entries.Single(e => e.Path == "Engine/Config/Base.ini");
        Assert.Equal(untouchedCfg, UnrealPakCodec.ReadEntryBytes(basePakPath, baseIniEntry, patchedInfo.Version, patchedInfo.CompressionMethods));

        var locEntry = patchedInfo.Entries.Single(e => e.Path == "MyGame/Content/Localization/Game/en/Game.locres");
        var locSlots = UnrealLocresFormat.Instance.Extract(UnrealPakCodec.ReadEntryBytes(basePakPath, locEntry, patchedInfo.Version, patchedInfo.CompressionMethods));
        Assert.Contains(locSlots, s => s.Source == "ابدأ اللعب");
        Assert.Contains(locSlots, s => s.Source == "Quit Game");

        var fontEntry = patchedInfo.Entries.Single(e => e.Path == "MyGame/Content/UI/Fonts/MainFont.ufont");
        byte[] fontBytes = UnrealPakCodec.ReadEntryBytes(basePakPath, fontEntry, patchedInfo.Version, patchedInfo.CompressionMethods);
        Assert.True(UnrealPakFormat.IsUe5WrappedUfont(fontBytes));
        Assert.True(ArabicFontCatalog.SupportsLatinAndArabic(fontBytes.AsSpan(4, fontBytes.Length - 8)));

        // 1-click Restore brings back the exact original SHA-256!
        Assert.Equal(1, PackageInstaller.RestoreInPlace(store));
        Assert.Equal(origHash, PackageInstaller.FileHash(basePakPath));
        Assert.Null(PackageInstaller.GetInPlaceStatus(store));
    }

    [Fact]
    public void UnrealRealSteamGamesReadOnlyScanAndPatchBuildSmokeTest()
    {
        (string Name, string? Dir, bool IsIoStore)[] candidates =
        [
            ("SmartFactory", OptionalLocalSteamGame("Smart Factory Tycoon"), false),
            ("DumpsterGang", OptionalLocalSteamGame("Dumpster Gang Demo"), true)
        ];

        foreach (var (name, dir, isIoStore) in candidates)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            using var ws = new TempWorkspace();
            using var store = ProjectStore.Create(ws.PathOf(name), name, dir);
            var service = new ProjectService(store);

            var cap = service.Capabilities();
            Assert.Equal("unreal", cap.Engine);
            Assert.True(cap.CanRead);

            var scan = service.Scan();
            Assert.Equal(1, scan.Files);
            Assert.True(scan.Units > 100, $"Expected >100 game strings in {name}, got {scan.Units}");
            Assert.Empty(scan.Diagnostics);

            // Translate 1 unit and build into an isolated temp output (never touches Steam folder).
            var firstUnit = store.Units("", 0, 10).First();
            firstUnit.Target = "اختبار تعريب محرك أنريل";
            store.Save(firstUnit, approve: true);

            var preview = service.Preview(final: true);
            Assert.Single(preview.ChangedFiles);
            if (isIoStore)
                Assert.EndsWith(".pak", preview.ChangedFiles[0], StringComparison.OrdinalIgnoreCase);
            else
                Assert.EndsWith("_Locara_P.pak", preview.ChangedFiles[0], StringComparison.OrdinalIgnoreCase);

            var build = service.Build(ws.PathOf(name + "-out"), final: true, reviewed: preview);
            Assert.Equal(1, build.Files);
            Assert.Equal(1, build.Translations);

            string builtPakPath = Path.Combine(build.Directory, "resources", preview.ChangedFiles[0].Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(builtPakPath));
            var modInfo = UnrealPakCodec.ReadIndex(builtPakPath);
            Assert.Contains(modInfo.Entries, e => e.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(modInfo.Entries, e => e.Path.EndsWith(".ufont", StringComparison.OrdinalIgnoreCase));
            if (isIoStore)
            {
                var baseIni = modInfo.Entries.Single(e => e.Path == "Engine/Config/Base.ini");
                Assert.NotEmpty(UnrealPakCodec.ReadEntryBytes(builtPakPath, baseIni, modInfo.Version, modInfo.CompressionMethods));
            }
        }
    }

    [Fact]
    public void UnityAssetsFormatStructuredTextAssetQualityFilterGeneralizedTmpAndNativeRtlToggle()
    {
        using var ws = new TempWorkspace();
        string gameDir = ws.PathOf("unity-textassets-game");
        string dataDir = Path.Combine(gameDir, "MyUnity_Data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), new byte[64]);

        // sharedassets0.assets: 2 human structured TextAssets (JSON + CSV) + 2 technical/shader TextAssets that MUST be filtered out!
        byte[] shared0 = BuildSyntheticSerializedFileV22Multi([
            (49, 101, BuildSyntheticTextAsset("DialogueJson", """{"greeting":"Welcome, hero!","quest":"Find the ancient sword in the citadel."}""")),
            (49, 102, BuildSyntheticTextAsset("ItemsCsv", "key,source,target\nSWORD,Iron Sword,\n")),
            (49, 103, BuildSyntheticTextAsset("WaterShader", "Shader \"Custom/Water\" { SubShader { Pass { CGPROGRAM void frag() {} } } }")),
            (49, 104, BuildSyntheticTextAsset("TechGuids", """{"id":"550e8400-e29b-41d4-a716-446655440000","prefab":"Assets/Prefabs/Hero.prefab"}"""))
        ]);
        File.WriteAllBytes(Path.Combine(dataDir, "sharedassets0.assets"), shared0);

        // resources.assets: TMP host ONLY (no I2Languages, no TextAssets) — tests generalized UnityTmpArabicPatcher!
        const long fontPathId = 973;
        const long fallbackPathId = 14782;
        const long tmpSettingsPathId = 14786;
        byte[] resourcesAssets = BuildSyntheticSerializedFileV22Multi([
            (128, fontPathId, BuildSyntheticLiberationSansFont()),
            (114, fallbackPathId, BuildSyntheticTmpFallbackAsset()),
            (114, tmpSettingsPathId, BuildSyntheticUnity6TmpSettings())
        ]);
        File.WriteAllBytes(Path.Combine(dataDir, "resources.assets"), resourcesAssets);

        using var store = ProjectStore.Create(ws.PathOf("unity-ta-proj"), "Unity TextAssets", gameDir);
        var service = new ProjectService(store);

        var cap = service.Capabilities();
        Assert.Equal("unity", cap.Engine);
        Assert.True(cap.CanRead);

        var scan = service.Scan();
        Assert.Equal(1, scan.Files); // sharedassets0.assets only (resources.assets has no text units)
        Assert.Equal(3, scan.Units); // 2 JSON + 1 CSV (shader + GUID JSON automatically filtered out!)

        var greeting = store.Units("Welcome, hero!").Single();
        var sword = store.Units("Iron Sword").Single();
        greeting.Target = "مرحباً أيها البطل!";
        sword.Target = "سيف حديدي";
        store.Save(greeting, approve: true);
        store.Save(sword, approve: true);

        // 1. Default mode (ShapeArabic = true): shapes Arabic + automatically injects TMP Arabic font into resources.assets!
        var shapedPreview = service.Preview(final: true);
        Assert.Equal(2, shapedPreview.ChangedFiles.Count);
        Assert.Contains("MyUnity_Data/sharedassets0.assets", shapedPreview.ChangedFiles);
        Assert.Contains("MyUnity_Data/resources.assets", shapedPreview.ChangedFiles);

        var shapedBuild = service.Build(ws.PathOf("unity-ta-out-shaped"), final: true, reviewed: shapedPreview);
        Assert.Equal(2, shapedBuild.Files);
        string rebuiltShared0 = Path.Combine(shapedBuild.Directory, "resources", "MyUnity_Data", "sharedassets0.assets");
        string rebuiltRes = Path.Combine(shapedBuild.Directory, "resources", "MyUnity_Data", "resources.assets");
        Assert.True(File.Exists(rebuiltShared0));
        Assert.True(File.Exists(rebuiltRes));

        var reExtractedShaped = UnityAssetsFormat.ExtractFromFile(rebuiltShared0);
        Assert.Contains(reExtractedShaped, s => s.Source == StarTruckerDisplay.Shape("مرحباً أيها البطل!"));
        Assert.Contains(reExtractedShaped, s => s.ExistingTarget == StarTruckerDisplay.Shape("سيف حديدي"));

        var patchedResSf = UnitySerializedFile.TryRead(File.ReadAllBytes(rebuiltRes))!;
        Assert.True(patchedResSf.Slice(patchedResSf.Objects.Single(o => o.PathId == fontPathId)).IndexOf("Noto Sans Arabic"u8) >= 0);

        // 2. Native RTL mode (unity_arabic_shaping = "0"): fingerprint changes, writes logical Arabic, still patches TMP font!
        store.Set("unity_arabic_shaping", "0");
        var rtlPreview = service.Preview(final: true);
        Assert.NotEqual(shapedPreview.Fingerprint, rtlPreview.Fingerprint);
        Assert.Contains("RTL أصلي", rtlPreview.Notice);

        var rtlBuild = service.Build(ws.PathOf("unity-ta-out-rtl"), final: true, reviewed: rtlPreview);
        string rebuiltShared0Rtl = Path.Combine(rtlBuild.Directory, "resources", "MyUnity_Data", "sharedassets0.assets");
        var reExtractedRtl = UnityAssetsFormat.ExtractFromFile(rebuiltShared0Rtl);
        Assert.Contains(reExtractedRtl, s => s.Source == "مرحباً أيها البطل!");
        Assert.Contains(reExtractedRtl, s => s.ExistingTarget == "سيف حديدي");
    }

    [Fact]
    public void UnityNonI2BundleAutoTmpPatchAndNativeRtlToggle()
    {
        using var ws = new TempWorkspace();
        string gameDir = ws.PathOf("unity-bundle-only");
        string dataDir = Path.Combine(gameDir, "BundleGame_Data");
        string aaDir = Path.Combine(dataDir, "StreamingAssets", "aa", "StandaloneWindows64");
        Directory.CreateDirectory(aaDir);

        byte[] taObj = BuildSyntheticTextAsset("StoryIntro", "Welcome to the citadel!");
        byte[] cab = BuildSyntheticSerializedFileV22(classId: 49, pathId: 1001, objectData: taObj);
        var tpl = new UnityFsBundle { Version = 8, UnityVersion = "5.x.x", UnityRevision = "6000.3.8f1", Flags = 0x243u, DataHash = new byte[16] };
        File.WriteAllBytes(Path.Combine(aaDir, "localization-text-en.bundle"), tpl.Write([new UnityFsNode("CAB-story", 4u, cab)], out _));

        byte[] resourcesAssets = BuildSyntheticSerializedFileV22Multi([
            (128, 973, BuildSyntheticLiberationSansFont()),
            (114, 14782, BuildSyntheticTmpFallbackAsset()),
            (114, 14786, BuildSyntheticUnity6TmpSettings())
        ]);
        File.WriteAllBytes(Path.Combine(dataDir, "resources.assets"), resourcesAssets);

        using var store = ProjectStore.Create(ws.PathOf("unity-b-proj"), "Unity Bundle Only", gameDir);
        var service = new ProjectService(store);
        var scan = service.Scan();
        Assert.Equal(1, scan.Files);
        Assert.Equal(1, scan.Units);

        var unit = store.Units().Single();
        unit.Target = "أهلاً بك في القلعة!";
        store.Save(unit, approve: true);

        // Default shaping ON: bundle gets shaped Arabic + resources.assets gets TMP Arabic font injected!
        var preview = service.Preview(final: true);
        Assert.Equal(2, preview.ChangedFiles.Count);
        Assert.Contains(preview.ChangedFiles, p => p.EndsWith("resources.assets"));
        var build = service.Build(ws.PathOf("unity-b-out"), final: true, reviewed: preview);
        string rebuiltBundle = Path.Combine(build.Directory, "resources", "BundleGame_Data", "StreamingAssets", "aa", "StandaloneWindows64", "localization-text-en.bundle");
        var bundleSlots = new UnityBundleFormat().Extract(File.ReadAllBytes(rebuiltBundle));
        Assert.Equal(StarTruckerDisplay.Shape("أهلاً بك في القلعة!"), Assert.Single(bundleSlots).Source);

        // Native RTL mode (unity_arabic_shaping = "0"): bundle gets logical unshaped Arabic!
        store.Set("unity_arabic_shaping", "0");
        var rtlPreview = service.Preview(final: true);
        var rtlBuild = service.Build(ws.PathOf("unity-b-out-rtl"), final: true, reviewed: rtlPreview);
        string rebuiltBundleRtl = Path.Combine(rtlBuild.Directory, "resources", "BundleGame_Data", "StreamingAssets", "aa", "StandaloneWindows64", "localization-text-en.bundle");
        var rtlSlots = new UnityBundleFormat().Extract(File.ReadAllBytes(rebuiltBundleRtl));
        Assert.Equal("أهلاً بك في القلعة!", Assert.Single(rtlSlots).Source);
    }

    private static byte[] BuildSyntheticLiberationSansFont()
    {
        byte[] buf = new byte[2276];
        void WriteAlignedAt(int offset, string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), utf8.Length);
            Buffer.BlockCopy(utf8, 0, buf, offset + 4, utf8.Length);
        }
        WriteAlignedAt(0, "LiberationSans");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(2212), 16); // oldFontSize
        WriteAlignedAt(2248, "Liberation Sans");
        return buf;
    }

    private static byte[] BuildSyntheticTmpFallbackAsset()
    {
        byte[] buf = new byte[160];
        void WriteAlignedAt(int offset, string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), utf8.Length);
            Buffer.BlockCopy(utf8, 0, buf, offset + 4, utf8.Length);
        }
        WriteAlignedAt(28, "LiberationSans SDF - Fallback");
        WriteAlignedAt(80, "Liberation Sans");
        WriteAlignedAt(100, "Regular");
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(112), 86f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(116), 1.0f);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(120), 2048);
        return buf;
    }

    private static byte[] BuildSyntheticUnity6TmpSettings()
    {
        byte[] buf = new byte[232];
        void WriteAlignedAt(int offset, string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), utf8.Length);
            Buffer.BlockCopy(utf8, 0, buf, offset + 4, utf8.Length);
        }
        WriteAlignedAt(28, "TMP Settings");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(56), 1); // m_enableKerning
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(60), 1); // m_ActiveFontFeatures.size
        "nrek"u8.CopyTo(buf.AsSpan(64)); // 'kern' LE
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(84), 1); // m_GetFontFeaturesAtRuntime
        WriteAlignedAt(116, "Fonts & Materials/");
        float[] floats = [36f, 0.5f, 2f, 20f, 5f, 200f, 50f];
        for (int i = 0; i < floats.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(140 + i * 4), floats[i]);
        // 168..176: two 4B aligned bools (0, 0)
        // 176: m_fallbackFontAssets.size = 0
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(180), 1); // m_matchMaterialPreset
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(188), 14784L); // m_defaultSpriteAsset.pathId
        WriteAlignedAt(196, "Sprite Assets/");
        return buf;
    }

    private static byte[] BuildSyntheticI2LanguagesAsset(IReadOnlyList<(string Key, string French, string English)> terms)
    {
        using var ms = new MemoryStream();
        static void WriteI32(Stream s, int v)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, v);
            s.Write(b);
        }
        static void WriteAligned(Stream s, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            WriteI32(s, utf8.Length);
            s.Write(utf8);
            int pad = (4 - (utf8.Length % 4)) % 4;
            if (pad > 0) s.Write(new byte[pad]);
        }

        ms.Write(new byte[28]); // MonoBehaviour header
        WriteAligned(ms, "I2Languages");
        ms.Write(new byte[12]); // UserAgrees x2 + Assets.size = 0

        WriteI32(ms, terms.Count);
        foreach (var (key, fr, en) in terms)
        {
            WriteAligned(ms, key);
            WriteI32(ms, 0); // TermType = Text
            WriteI32(ms, 2); // 2 languages
            WriteAligned(ms, fr);
            WriteAligned(ms, en);
            WriteI32(ms, 0); // Flags.size
            WriteI32(ms, 0); // Languages_Touch.size
        }

        WriteI32(ms, 0); // CaseInsensitiveTerms
        WriteI32(ms, 1); // OnMissingTranslation
        WriteAligned(ms, ""); // mTerm_AppName
        WriteI32(ms, 2); // mLanguages.size
        WriteAligned(ms, "French"); WriteAligned(ms, "fr"); WriteI32(ms, 0);
        WriteAligned(ms, "English"); WriteAligned(ms, "en"); WriteI32(ms, 0);
        return ms.ToArray();
    }

    private static byte[] BuildSyntheticSerializedFileV22Multi(IReadOnlyList<(int ClassId, long PathId, byte[] Data)> objects)
    {
        var distinctTypes = objects.Select(o => o.ClassId).Distinct().ToList();
        using var meta = new MemoryStream();
        meta.Write(Encoding.UTF8.GetBytes("6000.3.8f1\0"));
        Span<byte> b4 = stackalloc byte[4];
        Span<byte> b8 = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, 19); meta.Write(b4);
        meta.WriteByte(0); // enableTypeTree = false
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, distinctTypes.Count); meta.Write(b4);
        foreach (int cid in distinctTypes)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, cid); meta.Write(b4);
            meta.WriteByte(0);
            meta.Write([0xFF, 0xFF]);
            if (cid == 114) meta.Write(new byte[16]); // m_ScriptID for MonoBehaviour
            meta.Write(new byte[16]); // m_OldTypeHash
        }

        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, objects.Count); meta.Write(b4);
        while (meta.Position % 4 != 0) meta.WriteByte(0);

        long relOffset = 0;
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = relOffset;
            int typeIndex = distinctTypes.IndexOf(objects[i].ClassId);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b8, objects[i].PathId); meta.Write(b8);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b8, relOffset); meta.Write(b8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)objects[i].Data.Length); meta.Write(b4);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, typeIndex); meta.Write(b4);
            relOffset = (relOffset + objects[i].Data.Length + 7) & ~7L;
        }
        byte[] metaBytes = meta.ToArray();

        int dataOffset = (48 + metaBytes.Length + 15) & ~15;
        int totalSize = checked(dataOffset + (int)relOffset);
        byte[] file = new byte[totalSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8), 22);
        file[16] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(20), metaBytes.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(24), totalSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(32), dataOffset);
        Buffer.BlockCopy(metaBytes, 0, file, 48, metaBytes.Length);
        for (int i = 0; i < objects.Count; i++)
            Buffer.BlockCopy(objects[i].Data, 0, file, checked(dataOffset + (int)offsets[i]), objects[i].Data.Length);
        return file;
    }

    private static byte[] BuildSyntheticTextAsset(string name, string text)
    {
        using var ms = new MemoryStream();
        static void WriteAligned(Stream s, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, utf8.Length);
            s.Write(len);
            s.Write(utf8);
            int pad = (4 - (utf8.Length % 4)) % 4;
            if (pad > 0) s.Write(new byte[pad]);
        }
        WriteAligned(ms, name);
        WriteAligned(ms, text);
        return ms.ToArray();
    }

    private static byte[] BuildSyntheticSerializedFileV22(int classId, long pathId, byte[] objectData)
    {
        using var meta = new MemoryStream();
        meta.Write(Encoding.UTF8.GetBytes("6000.3.8f1\0"));
        Span<byte> b4 = stackalloc byte[4];
        Span<byte> b8 = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, 19); meta.Write(b4); // targetPlatform
        meta.WriteByte(0); // enableTypeTree = false (classId=49 needs no TypeTree)
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, 1); meta.Write(b4); // typeCount = 1
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, classId); meta.Write(b4);
        meta.WriteByte(0); // stripped
        meta.Write([0xFF, 0xFF]); // scriptTypeIndex = -1
        meta.Write(new byte[16]); // oldTypeHash
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, 1); meta.Write(b4); // objectCount = 1
        while (meta.Position % 4 != 0) meta.WriteByte(0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b8, pathId); meta.Write(b8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b8, 0); meta.Write(b8); // byteStart = 0
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)objectData.Length); meta.Write(b4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b4, 0); meta.Write(b4); // typeIndex = 0
        byte[] metaBytes = meta.ToArray();

        int dataOffset = (48 + metaBytes.Length + 15) & ~15;
        int totalSize = dataOffset + objectData.Length;
        byte[] file = new byte[totalSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8), 22);
        file[16] = 0; // little-endian
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(20), metaBytes.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(24), totalSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(32), dataOffset);
        Buffer.BlockCopy(metaBytes, 0, file, 48, metaBytes.Length);
        Buffer.BlockCopy(objectData, 0, file, dataOffset, objectData.Length);
        return file;
    }

    [Fact]
    public void GodotPckV3RoundTripAndRealSlayTheSpire2ReadOnlySmoke()
    {
        using var ws = new TempWorkspace();
        string sourcePck = ws.PathOf("game_v3.pck");
        var pck = new PckContainer();

        // Build a synthetic Godot 4.5.1 PCK v3 (dirBase at end, PACK_REL_FILEBASE=2, bare paths without res://)
        var templateIndex = new ResourceIndex { GodotVersion = [4u, 5u, 1u], PckFormatVersion = 3u, PckFlags = 2u, PckUsesResPrefix = false };
        pck.WriteIndexed(sourcePck, [
            ResourceEntry.FromBytes("project.binary", Encoding.UTF8.GetBytes("ECFG")),
            ResourceEntry.FromBytes("localization/eng/ui.json", Encoding.UTF8.GetBytes("""{"play":"Play Game","quit":"Exit"}"""))
        ], null, CancellationToken.None, templateIndex);

        using (var fs = File.OpenRead(sourcePck))
        using (var br = new BinaryReader(fs))
        {
            Assert.Equal(0x43504447u, br.ReadUInt32());
            Assert.Equal(3u, br.ReadUInt32());
            Assert.Equal(4u, br.ReadUInt32());
            Assert.Equal(5u, br.ReadUInt32());
            Assert.Equal(1u, br.ReadUInt32());
            Assert.Equal(2u, br.ReadUInt32()); // PACK_REL_FILEBASE
            ulong fileBase = br.ReadUInt64();
            ulong dirBase = br.ReadUInt64();
            Assert.Equal(112ul, fileBase);
            Assert.True(dirBase > fileBase);
        }

        using var store = ProjectStore.Create(ws.PathOf("godot-v3-proj"), "Godot v3 Test", sourcePck);
        var service = new ProjectService(store);
        var scan = service.Scan();
        Assert.Equal("godot", store.Get("scanned_engine"));
        Assert.Equal(1, scan.Files);
        Assert.Equal(2, scan.Units);

        foreach (var unit in store.Units())
        {
            unit.Target = unit.Source == "Play Game" ? "ابدأ اللعبة" : "خروج";
            store.Save(unit, approve: true);
        }

        string outDir = ws.PathOf("godot-v3-out");
        var built = service.Build(outDir, final: true);
        Assert.Equal(2, built.Translations);

        string rebuiltPck = Path.Combine(outDir, "game_v3.pck");
        Assert.True(File.Exists(rebuiltPck));
        using (var fs = File.OpenRead(rebuiltPck))
        using (var br = new BinaryReader(fs))
        {
            Assert.Equal(0x43504447u, br.ReadUInt32());
            Assert.Equal(3u, br.ReadUInt32()); // preserves PCK v3!
        }

        using var readBack = pck.Index(rebuiltPck, null, CancellationToken.None);
        Assert.Equal(3u, readBack.PckFormatVersion);
        Assert.False(readBack.PckUsesResPrefix);
        string rebuiltJson = Encoding.UTF8.GetString(readBack.Entries.Single(e => e.Path == "localization/eng/ui.json").Read(CancellationToken.None));
        Assert.Contains("ابدأ اللعبة", rebuiltJson);
        Assert.Contains("خروج", rebuiltJson);

        // Also verify folder auto-detection picks root Godot .pck even when loose metadata files exist,
        // and auto-excludes secondary language folders (e.g. localization/deu/) when localization/eng/ exists!
        string folderGame = ws.PathOf("godot-folder-game");
        Directory.CreateDirectory(Path.Combine(folderGame, "data_game_windows_x86_64"));
        File.WriteAllText(Path.Combine(folderGame, "release_info.json"), """{"version":"1.0"}""");
        File.WriteAllText(Path.Combine(folderGame, "data_game_windows_x86_64", "lib.xml"), "<doc/>");
        string folderPck = Path.Combine(folderGame, "MyGodotGame.pck");
        pck.WriteIndexed(folderPck, [
            ResourceEntry.FromBytes("project.binary", Encoding.UTF8.GetBytes("ECFG")),
            ResourceEntry.FromBytes("localization/eng/ui.json", Encoding.UTF8.GetBytes("""{"play":"Play Game","quit":"Exit"}""")),
            ResourceEntry.FromBytes("localization/deu/ui.json", Encoding.UTF8.GetBytes("""{"play":"Spiel starten","quit":"Beenden"}"""))
        ], null, CancellationToken.None, templateIndex);

        using (var folderStore = ProjectStore.Create(ws.PathOf("godot-folder-proj"), "Godot Folder Auto", folderGame))
        {
            var folderSvc = new ProjectService(folderStore);
            var folderScan = folderSvc.Scan();
            Assert.Equal("MyGodotGame.pck", folderScan.Package);
            Assert.Equal("godot", folderStore.Get("scanned_engine"));
            Assert.Equal(2, folderScan.Files);
            Assert.Equal(2, folderScan.Units); // Only English active! German excluded as SecondaryLanguage
            var scannedFiles = folderSvc.ScannedFiles();
            var engFile = scannedFiles.Single(f => f.Path.Contains("localization/eng/ui.json"));
            var deuFile = scannedFiles.Single(f => f.Path.Contains("localization/deu/ui.json"));
            Assert.True(engFile.Included);
            Assert.False(deuFile.Included);
            Assert.Equal(FileQualityVerdict.SecondaryLanguage, deuFile.Verdict);
        }

        // Optional read-only smoke test on real Steam Godot 4.5.1 PCK v3 when LOCARA_STEAM_COMMON is set locally
        string? sts2Dir = OptionalLocalSteamGame("Slay the Spire 2");
        string sts2Pck = sts2Dir != null ? Path.Combine(sts2Dir, "SlayTheSpire2.pck") : "";
        if (sts2Pck.Length > 0 && File.Exists(sts2Pck))
        {
            using var realIdx = pck.Index(sts2Pck, null, CancellationToken.None);
            Assert.Equal(3u, realIdx.PckFormatVersion);
            Assert.Equal<uint>([4u, 5u, 1u], realIdx.GodotVersion!);
            Assert.True(realIdx.Entries.Count > 10_000);
            var menuJson = realIdx.Entries.FirstOrDefault(e => e.Path.Equals("localization/eng/main_menu_ui.json", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(menuJson);
            byte[] menuBytes = menuJson!.Read(CancellationToken.None);
            Assert.True(new JsonFormat().Extract(menuBytes).Count > 5);
        }
    }

    [Fact]
    public void GodotBinaryTranslationOptimizedAndPlainRoundTrip()
    {
        // 1. OptimizedTranslation (Godot 4 RSRC v5) with BOTH SMAZ-compressed and uncompressed entries
        // SMAZ codes: 72="The", 0=" ", 65="w", 6="o", 12="r", 22="l", 24="d" -> "The world" (9 bytes compressed to 7 bytes!)
        byte[] smazWorld = [72, 0, 65, 6, 12, 22, 24];
        byte[] rawHello = Encoding.UTF8.GetBytes("Hello Hero!");
        byte[] stringsPool = [.. smazWorld, .. rawHello];

        // Single bucket at offset 0 with 2 elements:
        // elem 0: key=0x11223344, str_offset=0, comp=7, uncomp=9 ("The world")
        // elem 1: key=0x55667788, str_offset=7, comp=11, uncomp=11 ("Hello Hero!")
        int[] hashTable = [-1, 0, -1];
        int[] bucketTable =
        [
            2, unchecked((int)0x01000193u),
            unchecked((int)0x11223344u), 0, smazWorld.Length, 9,
            unchecked((int)0x55667788u), smazWorld.Length, rawHello.Length, rawHello.Length
        ];

        byte[] optBytes = BuildSyntheticGodotOptimizedTranslation("en", hashTable, bucketTable, stringsPool);
        var fmt = GodotTranslationFormat.Instance;
        Assert.True(fmt.Supports("translations/ui.en.translation"));

        var slots = fmt.Extract(optBytes);
        Assert.Equal(2, slots.Count);
        Assert.Equal("The world", slots[0].Source);
        Assert.Equal("Hello Hero!", slots[1].Source);

        // Translate first entry to Arabic, leave second untouched
        byte[] patched = fmt.Apply(optBytes, new Dictionary<string, string>
        {
            [slots[0].Locator] = "العالم الجميل!"
        });
        Assert.NotEqual(optBytes, patched);

        var reExtracted = fmt.Extract(patched);
        Assert.Equal(2, reExtracted.Count);
        Assert.Equal("العالم الجميل!", reExtracted[0].Source);
        Assert.Equal(slots[0].Locator, reExtracted[0].Locator); // bucket offset & key preserved!
        Assert.Equal("Hello Hero!", reExtracted[1].Source);
        Assert.Equal(slots[1].Locator, reExtracted[1].Locator);

        // 2. Uncompressed Godot Translation (messages as PackedStringArray)
        byte[] plainBytes = BuildSyntheticGodotPlainTranslation("en", ["KEY_PLAY", "Start Adventure", "KEY_QUIT", "Quit"]);
        var plainSlots = fmt.Extract(plainBytes);
        Assert.Equal(2, plainSlots.Count);
        Assert.Equal("Start Adventure", plainSlots[0].Source);
        Assert.Contains("KEY_PLAY", plainSlots[0].Context);

        byte[] patchedPlain = fmt.Apply(plainBytes, new Dictionary<string, string>
        {
            [plainSlots[0].Locator] = "ابدأ المغامرة"
        });
        var rePlain = fmt.Extract(patchedPlain);
        Assert.Equal("ابدأ المغامرة", rePlain[0].Source);
        Assert.Equal("Quit", rePlain[1].Source);
    }

    [Fact]
    public void XUnityAutoTranslatorFormatAndExporterRoundTrip()
    {
        using var ws = new TempWorkspace();
        string gameDir = ws.PathOf("hardcoded-unity");
        string textDir = Path.Combine(gameDir, "BepInEx", "Translation", "ar", "Text");
        Directory.CreateDirectory(textDir);
        string autoFile = Path.Combine(textDir, "_AutoGeneratedTranslations.txt");
        File.WriteAllText(autoFile,
            "// Captured by XUnity.AutoTranslator\r\n" +
            "r:^HP: (\\d+)$=HP: $1\r\n" +
            "Start Game=Start Game\r\n" +
            "Level\\=1\\nReady=\r\n",
            new UTF8Encoding(false));

        using var store = ProjectStore.Create(ws.PathOf("xunity-proj"), "Hardcoded Unity", gameDir);
        var service = new ProjectService(store);
        var scan = service.Scan();
        Assert.Equal(1, scan.Files);
        Assert.Equal(2, scan.Units);

        foreach (var unit in store.Units())
        {
            unit.Target = unit.Source == "Start Game" ? "ابدأ اللعبة" : "المستوى=1\nجاهز";
            store.Save(unit, approve: true);
        }

        // 1. Standard build rewrites _AutoGeneratedTranslations.txt preserving comments, regexes, and escapes
        var built = service.Build(ws.PathOf("xunity-build"), final: true);
        string rebuiltTxt = File.ReadAllText(Path.Combine(built.Directory, "resources", "BepInEx", "Translation", "ar", "Text", "_AutoGeneratedTranslations.txt"));
        Assert.Contains("// Captured by XUnity.AutoTranslator", rebuiltTxt);
        Assert.Contains("r:^HP: (\\d+)$=HP: $1", rebuiltTxt);
        Assert.Contains("Start Game=ابدأ اللعبة", rebuiltTxt);
        Assert.Contains("Level\\=1\\nReady=المستوى\\=1\\nجاهز", rebuiltTxt);

        // 2. XUnityAutoTranslatorExporter with Arabic shaping ON vs OFF
        store.Set("unity_arabic_shaping", "1");
        var shapedExport = service.ExportXUnityAutoTranslator(ws.PathOf("xunity-export-shaped"), final: true);
        Assert.Equal(2, shapedExport.ExportedCount);
        Assert.True(shapedExport.ShapedArabic);
        Assert.True(File.Exists(shapedExport.TranslationFile));
        Assert.True(File.Exists(shapedExport.ConfigFile));
        string shapedContent = File.ReadAllText(shapedExport.TranslationFile);
        Assert.Contains($"Start Game={StarTruckerDisplay.Shape("ابدأ اللعبة")}", shapedContent);

        store.Set("unity_arabic_shaping", "0");
        var logicalExport = service.ExportXUnityAutoTranslator(ws.PathOf("xunity-export-logical"), final: true);
        Assert.False(logicalExport.ShapedArabic);
        string logicalContent = File.ReadAllText(logicalExport.TranslationFile);
        Assert.Contains("Start Game=ابدأ اللعبة", logicalContent);
        Assert.Contains("Language=ar", File.ReadAllText(logicalExport.ConfigFile));
    }

    private static byte[] BuildSyntheticGodotOptimizedTranslation(string locale, int[] hashTable, int[] bucketTable, byte[] stringsPool)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        static void WriteUString(BinaryWriter bw, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            bw.Write((uint)(b.Length + 1));
            bw.Write(b);
            bw.Write((byte)0);
        }
        static void WriteAligned(BinaryWriter bw, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            bw.Write((uint)b.Length);
            bw.Write(b);
            int pad = (4 - (b.Length % 4)) % 4;
            if (pad > 0) bw.Write(new byte[pad]);
        }

        w.Write("RSRC"u8);
        w.Write(0u); // little-endian
        w.Write(0u); // float32
        w.Write(4u); w.Write(5u); w.Write(5u); // Godot 4.5, formatVersion 5
        WriteUString(w, "OptimizedTranslation");
        w.Write(0ul); // importmd_ofs
        w.Write(2u); // FORMAT_FLAG_UIDS
        w.Write(123456789L); // uid
        w.Write(new byte[11 * 4]); // reserved

        string[] strTable = ["locale", "hash_table", "bucket_table", "strings"];
        w.Write((uint)strTable.Length);
        foreach (string s in strTable) WriteUString(w, s);

        w.Write(0u); // ext_resources
        w.Write(1u); // int_resources
        WriteUString(w, "local://0");
        long ofsPos = ms.Position;
        w.Write(0ul);

        long bodyStart = ms.Position;
        WriteUString(w, "OptimizedTranslation");
        w.Write(4u); // 4 properties

        w.Write(0u); w.Write(5u); WriteAligned(w, locale); // locale (VARIANT_STRING = 5)

        w.Write(1u); w.Write(32u); // hash_table (VARIANT_PACKED_INT32_ARRAY = 32)
        w.Write((uint)hashTable.Length);
        foreach (int h in hashTable) w.Write(h);

        w.Write(2u); w.Write(32u); // bucket_table
        w.Write((uint)bucketTable.Length);
        foreach (int b in bucketTable) w.Write(b);

        w.Write(3u); w.Write(31u); // strings (VARIANT_PACKED_BYTE_ARRAY = 31)
        w.Write((uint)stringsPool.Length);
        w.Write(stringsPool);
        int spPad = (4 - (stringsPool.Length % 4)) % 4;
        if (spPad > 0) w.Write(new byte[spPad]);

        w.Write("RSRC"u8);
        w.Flush();
        ms.Position = ofsPos;
        w.Write((ulong)bodyStart);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildSyntheticGodotPlainTranslation(string locale, string[] flatKeyValues)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        static void WriteUString(BinaryWriter bw, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            bw.Write((uint)(b.Length + 1));
            bw.Write(b);
            bw.Write((byte)0);
        }
        static void WriteAligned(BinaryWriter bw, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            bw.Write((uint)b.Length);
            bw.Write(b);
            int pad = (4 - (b.Length % 4)) % 4;
            if (pad > 0) bw.Write(new byte[pad]);
        }

        w.Write("RSRC"u8);
        w.Write(0u); w.Write(0u);
        w.Write(4u); w.Write(5u); w.Write(5u);
        WriteUString(w, "Translation");
        w.Write(0ul);
        w.Write(2u); w.Write(987654321L);
        w.Write(new byte[11 * 4]);

        w.Write(2u); WriteUString(w, "locale"); WriteUString(w, "messages");
        w.Write(0u);
        w.Write(1u); WriteUString(w, "local://0");
        long ofsPos = ms.Position;
        w.Write(0ul);

        long bodyStart = ms.Position;
        WriteUString(w, "Translation");
        w.Write(2u);

        w.Write(0u); w.Write(5u); WriteAligned(w, locale);
        w.Write(1u); w.Write(34u); // VARIANT_PACKED_STRING_ARRAY = 34
        w.Write((uint)flatKeyValues.Length);
        foreach (string s in flatKeyValues) WriteAligned(w, s);

        w.Write("RSRC"u8);
        w.Flush();
        ms.Position = ofsPos;
        w.Write((ulong)bodyStart);
        w.Flush();
        return ms.ToArray();
    }

    private static string? OptionalLocalSteamGame(string folderName)
    {
        string? steamCommon = Environment.GetEnvironmentVariable("LOCARA_STEAM_COMMON");
        if (string.IsNullOrWhiteSpace(steamCommon)) return null;
        string path = Path.Combine(steamCommon, folderName);
        return Directory.Exists(path) ? path : null;
    }
}
