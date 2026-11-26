using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class UnityClassicRtlReflowTests
{
    [Fact]
    public void PreShapedMultiLineReflowFlipsWrappedSubLinesTopToBottomPerParagraph()
    {
        // Logical sentence: "اضغط هنا لفتح الإعدادات"
        // Statically shaped single line puts the END ("الإعدادات") on the left (index 0) and START ("اضغط هنا") on the right!
        string logical = "اضغط هنا لفتح الإعدادات";
        string shapedSingleLine = ArabicTextEngine.Shape(logical);
        Assert.Equal(ClassicArabicTextKind.PreShaped, UnityClassicRtlReflow.DetectArabicKind(shapedSingleLine));

        // Single line (1 start index) -> untouched
        Assert.Equal(shapedSingleLine, UnityClassicRtlReflow.ReflowPreShapedLines(shapedSingleLine, [0]));

        // Simulate narrow UI box splitting at space between the two halves:
        // Index 0..mid got the END of the sentence; mid..end got the START of the sentence.
        int spaceIdx = shapedSingleLine.IndexOf(' ', shapedSingleLine.Length / 3);
        Assert.True(spaceIdx > 0);
        string bottomHalfOriginallyAtTop = shapedSingleLine[..spaceIdx];
        string topHalfOriginallyAtBottom = shapedSingleLine[(spaceIdx + 1)..];

        string reflowed = UnityClassicRtlReflow.ReflowPreShapedLines(shapedSingleLine, [0, spaceIdx + 1]);
        string[] lines = reflowed.Split('\n');
        Assert.Equal(2, lines.Length);
        // Line 0 (TOP) now has the START of the Arabic sentence; Line 1 (BOTTOM) has the END!
        Assert.Equal(topHalfOriginallyAtBottom, lines[0]);
        Assert.Equal(bottomHalfOriginallyAtTop, lines[1]);

        // Multi-paragraph test: P1\nP2 where each paragraph wraps into 2 sub-lines -> P1 stays above P2!
        string p1 = ArabicTextEngine.Shape("الفقرة الأولى الطويلة هنا");
        string p2 = ArabicTextEngine.Shape("الفقرة الثانية الطويلة هنا");
        int s1 = p1.IndexOf(' ', p1.Length / 3);
        int s2 = p2.IndexOf(' ', p2.Length / 3);
        string twoPara = p1 + "\n" + p2;
        int p2Offset = p1.Length + 1;

        string reflowedMultiPara = UnityClassicRtlReflow.ReflowPreShapedLines(
            twoPara,
            [0, s1 + 1, p2Offset, p2Offset + s2 + 1]);

        string[] multiLines = reflowedMultiPara.Split('\n');
        Assert.Equal(4, multiLines.Length);
        Assert.Equal(p1[(s1 + 1)..], multiLines[0]);
        Assert.Equal(p1[..s1], multiLines[1]);
        Assert.Equal(p2[(s2 + 1)..], multiLines[2]);
        Assert.Equal(p2[..s2], multiLines[3]);
    }

    [Fact]
    public void PreShapedReflowRepairsSplitHtmlAndNguiRichTextTags()
    {
        // Pre-shaped string with a <color> span split across two wrapped visual lines
        string partA = ArabicTextEngine.Shape("الكلمة الأخيرة");
        string partB = ArabicTextEngine.Shape("الكلمة الوسطى");
        string partC = ArabicTextEngine.Shape("الكلمة الأولى");
        string originalHtml = $"<color=#ffcc00>{partA} {partB}</color> {partC}";
        int breakPos = $"<color=#ffcc00>{partA} ".Length;

        string reflowedHtml = UnityClassicRtlReflow.ReflowPreShapedLines(originalHtml, [0, breakPos]);
        string[] htmlLines = reflowedHtml.Split('\n');
        Assert.Equal(2, htmlLines.Length);

        // Top line (was second slice) re-opens <color=#ffcc00> before </color>, and bottom line closes </color>!
        Assert.Equal($"<color=#ffcc00>{partB}</color> {partC}", htmlLines[0]);
        Assert.Equal($"<color=#ffcc00>{partA}</color>", htmlLines[1]);
        Assert.True(Tokens.ValidTagNesting("", htmlLines[0]));
        Assert.True(Tokens.ValidTagNesting("", htmlLines[1]));

        // NGUI WrapText overload with [ffcc00]...[-] color tag split across wrapped lines
        string originalNgui = $"[ffcc00]{partA} {partB}[-] {partC}";
        string nguiWrapped = $"[ffcc00]{partA}\n{partB}[-] {partC}";
        string reflowedNgui = UnityClassicRtlReflow.ReflowPreShapedWrappedText(originalNgui, nguiWrapped);
        string[] nguiLines = reflowedNgui.Split('\n');
        Assert.Equal(2, nguiLines.Length);
        Assert.Equal($"[ffcc00]{partB}[-] {partC}", nguiLines[0]);
        Assert.Equal($"[ffcc00]{partA}[-]", nguiLines[1]);
    }

    [Fact]
    public void PureManagedLogicalShaperHandlesContextualFormsLamAlefNumbersLatinAndTags()
    {
        string logical = "لا تنس 100% من <color=red>الماء</color> في Steam (الآن)";
        Assert.Equal(ClassicArabicTextKind.Logical, UnityClassicRtlReflow.DetectArabicKind(logical));

        string visual = UnityClassicRtlReflow.ShapeAndVisualReverseSingleLine(logical);
        Assert.Equal(ClassicArabicTextKind.PreShaped, UnityClassicRtlReflow.DetectArabicKind(visual));

        // 1. Lam-Alef ligature (لا -> \uFEFB or \uFEFC) is formed
        Assert.Contains('\uFEFB', visual);

        // 2. LTR numbers, percentages, and English tokens remain un-reversed
        Assert.Contains("100%", visual, StringComparison.Ordinal);
        Assert.Contains("Steam", visual, StringComparison.Ordinal);

        // 3. HTML rich-text tags stay properly paired (<color=red> precedes </color>)
        int openIdx = visual.IndexOf("<color=red>", StringComparison.Ordinal);
        int closeIdx = visual.IndexOf("</color>", StringComparison.Ordinal);
        Assert.True(openIdx >= 0 && closeIdx > openIdx);

        // 4. Multi-line logical reflow keeps Line 0 on top and Line 1 on bottom while shaping each line
        string logicalTwoLines = "السطر الأول هنا\nالسطر الثاني هناك";
        string shapedMeasurement = UnityClassicRtlReflow.ShapeLogicalForMeasurement(logicalTwoLines);
        int secondStart = shapedMeasurement.IndexOf('\n') + 1;
        string reflowedLogical = UnityClassicRtlReflow.ReflowLogicalLines(shapedMeasurement, [0, secondStart]);
        string[] outLines = reflowedLogical.Split('\n');
        Assert.Equal(2, outLines.Length);
        Assert.Equal(UnityClassicRtlReflow.ShapeAndVisualReverseSingleLine("السطر الأول هنا"), outLines[0]);
        Assert.Equal(UnityClassicRtlReflow.ShapeAndVisualReverseSingleLine("السطر الثاني هناك"), outLines[1]);
    }

    [Fact]
    public void EmbeddedUnityRtlFixDllIsValidNetStandardAssemblyWithBepInExPluginMetadata()
    {
        byte[] dllBytes = UnityClassicRtlPlugin.LoadPluginBytes();
        Assert.True(dllBytes.Length > 4096);
        Assert.Equal((byte)'M', dllBytes[0]);
        Assert.Equal((byte)'Z', dllBytes[1]);

        using var ms = new MemoryStream(dllBytes);
        using var pe = new PEReader(ms);
        Assert.True(pe.HasMetadata);
        MetadataReader md = pe.GetMetadataReader();

        string asmName = md.GetString(md.GetAssemblyDefinition().Name);
        Assert.Equal("Locara.UnityRtlFix", asmName);

        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tdHandle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdHandle);
            typeNames.Add(md.GetString(td.Namespace) + "." + md.GetString(td.Name));
        }
        Assert.Contains("Locara.UnityRtlFix.UnityRtlFixPlugin", typeNames);
        Assert.Contains("Locara.Core.UnityClassicRtlReflow", typeNames);

        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arHandle in md.AssemblyReferences)
        {
            var ar = md.GetAssemblyReference(arHandle);
            refs.Add(md.GetString(ar.Name));
        }
        Assert.Contains("BepInEx", refs);
        Assert.Contains("netstandard", refs);
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void PluginInspectionStandaloneInstallRestoreAndPackageBuildLifecycle()
    {
        using var ws = new TempWorkspace();

        // 1. IL2CPP Unity directory -> UnsupportedIl2Cpp
        string il2cppDir = Directory.CreateDirectory(ws.PathOf("Il2CppGame")).FullName;
        Directory.CreateDirectory(Path.Combine(il2cppDir, "Game_Data", "il2cpp_data"));
        File.WriteAllBytes(Path.Combine(il2cppDir, "GameAssembly.dll"), [0x4D, 0x5A]);
        var il2cppInspect = UnityClassicRtlPlugin.InspectDirectory(il2cppDir);
        Assert.Equal(UnityScriptingBackend.Il2Cpp, il2cppInspect.Backend);
        Assert.Equal(UnityRtlPluginStatus.UnsupportedIl2Cpp, il2cppInspect.Status);

        // 2. Classic Unity Mono directory (first without BepInEx, then with BepInEx 5)
        string monoGame = Directory.CreateDirectory(ws.PathOf("MonoGame")).FullName;
        string managedDir = Directory.CreateDirectory(Path.Combine(monoGame, "MyGame_Data", "Managed")).FullName;
        File.WriteAllBytes(Path.Combine(managedDir, "Assembly-CSharp.dll"), [0x4D, 0x5A]);
        Directory.CreateDirectory(Path.Combine(monoGame, "MyGame_Data", "StreamingAssets"));
        File.WriteAllText(Path.Combine(monoGame, "MyGame_Data", "StreamingAssets", "en.json"),
            """{"play":"Play","desc":"Press here to open advanced settings"}""", Encoding.UTF8);

        var missingBep = UnityClassicRtlPlugin.InspectDirectory(monoGame);
        Assert.Equal(UnityScriptingBackend.Mono, missingBep.Backend);
        Assert.Equal(UnityRtlPluginStatus.BepInExMissing, missingBep.Status);

        // Add BepInEx 5 markers + a pre-existing custom config to verify backup/restore fidelity
        Directory.CreateDirectory(Path.Combine(monoGame, "BepInEx", "core"));
        File.WriteAllBytes(Path.Combine(monoGame, "BepInEx", "core", "BepInEx.dll"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(monoGame, "winhttp.dll"), [0x4D, 0x5A]);
        string cfgInGame = Path.Combine(monoGame, "BepInEx", "config", "Locara.UnityRtlFix.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(cfgInGame)!);
        byte[] customOldCfg = Encoding.UTF8.GetBytes("# Old user config\r\nEnabled = false\r\n");
        File.WriteAllBytes(cfgInGame, customOldCfg);

        using var store = ProjectStore.Create(ws.PathOf("unity-mono.locara"), "MonoGame", monoGame, "unity");
        var svc = new ProjectService(store);
        svc.Scan();

        var ready = UnityClassicRtlPlugin.Inspect(store);
        Assert.Equal(UnityRtlPluginStatus.ReadyToInstall, ready.Status);
        Assert.True(ready.HasBepInEx);

        // 3. Standalone 1-click install -> verifies SHA-256, backs up pre-existing config
        var installRes = UnityClassicRtlPlugin.InstallStandalone(store);
        Assert.Equal(2, installRes.Files);
        string dllInGame = Path.Combine(monoGame, "BepInEx", "plugins", "Locara.UnityRtlFix", "Locara.UnityRtlFix.dll");
        Assert.True(File.Exists(dllInGame));
        Assert.Equal(UnityRtlPluginStatus.InstalledStandalone, UnityClassicRtlPlugin.Inspect(store).Status);

        // Simulate external tampering -> detected as ModifiedExternally
        byte[] validDllBytes = File.ReadAllBytes(dllInGame);
        File.WriteAllBytes(dllInGame, [0x00, 0x01, 0x02]);
        Assert.Equal(UnityRtlPluginStatus.ModifiedExternally, UnityClassicRtlPlugin.Inspect(store).Status);
        File.WriteAllBytes(dllInGame, validDllBytes);
        Assert.Equal(UnityRtlPluginStatus.InstalledStandalone, UnityClassicRtlPlugin.Inspect(store).Status);

        // 4. Standalone 1-click uninstall -> deletes added DLL, prunes folder, restores custom pre-existing .cfg!
        int restored = UnityClassicRtlPlugin.UninstallStandalone(store);
        Assert.Equal(2, restored);
        Assert.False(File.Exists(dllInGame));
        Assert.False(Directory.Exists(Path.GetDirectoryName(dllInGame)!));
        Assert.Equal(customOldCfg, File.ReadAllBytes(cfgInGame));
        File.Delete(cfgInGame);

        // 5. Unified package build + DeployInPlace + XUnity export bundling
        var units = store.Units(limit: -1);
        foreach (var u in units) u.Target = u.Source == "Play" ? "العب" : "اضغط هنا لفتح الإعدادات المتقدمة";
        store.SavePage(units.Select(u => (u, Approve: true)).ToList());

        new ProjectSettings(store).IncludeClassicUnityRtlPlugin = true;
        var deployed = PackageInstaller.DeployInPlace(store, ws.PathOf("deploy-out"), final: true);
        Assert.True(File.Exists(dllInGame));
        Assert.Equal(UnityRtlPluginStatus.InstalledWithPackage, UnityClassicRtlPlugin.Inspect(store).Status);

        PackageInstaller.RestoreInPlace(store);
        Assert.False(File.Exists(dllInGame));
        Assert.Equal(UnityRtlPluginStatus.ReadyToInstall, UnityClassicRtlPlugin.Inspect(store).Status);
        _ = deployed;

        // 6. XUnity export also bundles Locara.UnityRtlFix automatically
        var xunityOut = svc.ExportXUnityAutoTranslator(ws.PathOf("xunity-out"), final: true);
        Assert.True(File.Exists(Path.Combine(xunityOut.OutputDirectory, "BepInEx", "plugins", "Locara.UnityRtlFix", "Locara.UnityRtlFix.dll")));
        Assert.True(File.Exists(Path.Combine(xunityOut.OutputDirectory, "BepInEx", "config", "Locara.UnityRtlFix.cfg")));
    }
}
