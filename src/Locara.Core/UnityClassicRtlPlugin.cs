using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Locara.Core;

public enum UnityScriptingBackend
{
    Unknown,
    Mono,
    Il2Cpp
}

public enum UnityRtlPluginStatus
{
    NotUnity,
    UnsupportedIl2Cpp,
    BepInExMissing,
    ReadyToInstall,
    InstalledStandalone,
    InstalledWithPackage,
    ModifiedExternally
}

public sealed record UnityRtlPluginInspection(
    UnityScriptingBackend Backend,
    UnityRtlPluginStatus Status,
    bool HasBepInEx,
    string GameDirectory,
    string PluginRelativePath,
    string ConfigRelativePath,
    string Summary,
    string Guidance);

/// <summary>
/// Manages inspection, embedded payload loading, and SHA-256 verified 1-click standalone
/// install/uninstall of Locara's lightweight BepInEx 5 runtime RTL reflow plugin for classic Unity Mono games.
/// </summary>
public static class UnityClassicRtlPlugin
{
    public const string PluginRelativePath = "BepInEx/plugins/Locara.UnityRtlFix/Locara.UnityRtlFix.dll";
    public const string ConfigRelativePath = "BepInEx/config/Locara.UnityRtlFix.cfg";
    private const string EmbeddedResourceName = "Locara.UnityRtlFix.dll";
    private const string ReceiptFolderName = "rtl-plugin-install";
    private const string ReceiptFileName = "receipt.json";

    public static byte[] LoadPluginBytes()
    {
        using var stream = typeof(UnityClassicRtlPlugin).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException("لم يُعثر على ملحق Locara.UnityRtlFix.dll المدمج داخل الحزمة.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[] BuildDefaultConfigBytes(bool fixPreShaped = true, bool fixLogical = true)
    {
        string cfg = $"""
            # Locara Unity Classic RTL Fix — Runtime Configuration
            # Fixes bottom-to-top Arabic line wrapping inside narrow UI.Text, TextMesh, and NGUI boxes.

            [General]
            Enabled = true
            FixUGUI = true
            FixNGUI = true
            FixPreShapedArabic = {(fixPreShaped ? "true" : "false")}
            FixLogicalArabic = {(fixLogical ? "true" : "false")}
            """;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(cfg.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");
    }

    public static UnityScriptingBackend DetectBackend(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            return UnityScriptingBackend.Unknown;

        if (File.Exists(Path.Combine(gameDirectory, "GameAssembly.dll")))
            return UnityScriptingBackend.Il2Cpp;

        try
        {
            foreach (string dataDir in Directory.EnumerateDirectories(gameDirectory, "*_Data", SearchOption.TopDirectoryOnly))
            {
                if (Directory.Exists(Path.Combine(dataDir, "il2cpp_data")))
                    return UnityScriptingBackend.Il2Cpp;
                if (Directory.Exists(Path.Combine(dataDir, "Managed")))
                    return UnityScriptingBackend.Mono;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (Directory.Exists(Path.Combine(gameDirectory, "MonoBleedingEdge"))
            || Directory.Exists(Path.Combine(gameDirectory, "Mono")))
            return UnityScriptingBackend.Mono;

        return UnityScriptingBackend.Unknown;
    }

    public static bool DetectBepInEx5(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            return false;

        bool hasCore = File.Exists(Path.Combine(gameDirectory, "BepInEx", "core", "BepInEx.dll"));
        bool hasLoader = File.Exists(Path.Combine(gameDirectory, "winhttp.dll"))
            || File.Exists(Path.Combine(gameDirectory, "version.dll"))
            || File.Exists(Path.Combine(gameDirectory, "doorstop_config.ini"));
        return hasCore && hasLoader;
    }

    public static string ReceiptDirectory(ProjectStore project) =>
        Path.Combine(project.DirectoryPath, ReceiptFolderName);

    public static UnityRtlPluginInspection Inspect(ProjectStore project, CancellationToken cancellation = default)
    {
        var projSettings = new ProjectSettings(project);
        string engine = projSettings.ScannedEngine;
        if (!engine.Equals("unity", StringComparison.OrdinalIgnoreCase))
        {
            return new(UnityScriptingBackend.Unknown, UnityRtlPluginStatus.NotUnity, false, "",
                PluginRelativePath, ConfigRelativePath, "", "");
        }

        string gameDir;
        try { gameDir = PackageInstaller.ResolveInstallDestination(project); }
        catch
        {
            return new(UnityScriptingBackend.Unknown, UnityRtlPluginStatus.NotUnity, false, "",
                PluginRelativePath, ConfigRelativePath, "اختر مجلد اللعبة المصدر لفحص دعم ملحق الالتفاف.", "");
        }

        return InspectDirectory(gameDir, project, cancellation);
    }

    public static UnityRtlPluginInspection InspectDirectory(
        string gameDirectory,
        ProjectStore? project = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return new(UnityScriptingBackend.Unknown, UnityRtlPluginStatus.NotUnity, false, gameDirectory ?? "",
                PluginRelativePath, ConfigRelativePath, "مجلد اللعبة غير موجود.", "");
        }

        var backend = DetectBackend(gameDirectory);
        if (backend == UnityScriptingBackend.Il2Cpp)
        {
            return new(
                backend,
                UnityRtlPluginStatus.UnsupportedIl2Cpp,
                HasBepInEx: false,
                gameDirectory,
                PluginRelativePath,
                ConfigRelativePath,
                "محرك IL2CPP (غير متوافق مع ملحق Mono)",
                "هذه اللعبة مبنية بنظام IL2CPP وليس Mono؛ استخدم خيارات التفاف الأسطر الثابت وحقن خط TextMeshPro أعلاه.");
        }

        bool hasBepInEx = DetectBepInEx5(gameDirectory);

        // Check standalone receipt first
        if (project != null)
        {
            string standaloneReceiptPath = Path.Combine(ReceiptDirectory(project), ReceiptFileName);
            if (File.Exists(standaloneReceiptPath))
            {
                var receiptState = CheckReceiptState(standaloneReceiptPath, cancellation);
                if (receiptState == ReceiptMatchState.Localized)
                {
                    string bepWarn = hasBepInEx
                        ? "BepInEx 5 مكتشف وجاهز — يعمل الملحق تلقائياً عند تشغيل اللعبة لإصلاح ترتيب الأسطر الملتفّة من الأعلى للأسفل."
                        : "تم تثبيت ملفات الملحق داخل مجلد BepInEx/plugins — ثبّت BepInEx 5 (x64/x86) في مجلد اللعبة لتفعيله أثناء التشغيل.";
                    return new(backend, UnityRtlPluginStatus.InstalledStandalone, hasBepInEx, gameDirectory,
                        PluginRelativePath, ConfigRelativePath, "مثبّت داخل اللعبة ✓", bepWarn);
                }
                if (receiptState == ReceiptMatchState.ModifiedExternally)
                {
                    return new(backend, UnityRtlPluginStatus.ModifiedExternally, hasBepInEx, gameDirectory,
                        PluginRelativePath, ConfigRelativePath, "تغيّر ملف الملحق خارجياً",
                        "تغيّر ملف الملحق بعد التثبيت — يمكنك إعادة تثبيته لتحديثه أو مراجعة التعديل اليدوي.");
                }
            }

            // Check if installed as part of unified in-place package install
            var inPlace = PackageInstaller.GetInPlaceStatus(project, cancellation);
            if (inPlace != null && !inPlace.ModifiedExternally
                && File.Exists(Path.Combine(gameDirectory, PluginRelativePath)))
            {
                string liveReceiptPath = Path.Combine(inPlace.ReceiptDirectory, ReceiptFileName);
                if (File.Exists(liveReceiptPath) && ReceiptContainsPlugin(liveReceiptPath))
                {
                    return new(backend, UnityRtlPluginStatus.InstalledWithPackage, hasBepInEx, gameDirectory,
                        PluginRelativePath, ConfigRelativePath, "مثبّت ضمن حزمة التعريب ✓",
                        hasBepInEx
                            ? "الملحق مثبّت ومرتبط بحزمة التعريب الدائمة (يُزال تلقائياً عند استعادة الأصل)."
                            : "الملحق مرفق مع الحزمة — ثبّت BepInEx 5 في مجلد اللعبة لتفعيله وقت التشغيل.");
                }
            }
        }

        if (!hasBepInEx)
        {
            return new(
                backend,
                UnityRtlPluginStatus.BepInExMissing,
                HasBepInEx: false,
                gameDirectory,
                PluginRelativePath,
                ConfigRelativePath,
                "يتطلب BepInEx 5 (Mono) للتفعيل",
                "ملحق إصلاح الالتفاف اللحظي يعيد ترتيب الأسطر العربية من الأعلى للأسفل داخل الصناديق الضيقة في ألعاب UI.Text وNGUI. فك ضغط BepInEx 5 في مجلد اللعبة أولاً (أو ثبّت الملحق الآن ليجهّز داخل BepInEx/plugins).");
        }

        return new(
            backend,
            UnityRtlPluginStatus.ReadyToInstall,
            HasBepInEx: true,
            gameDirectory,
            PluginRelativePath,
            ConfigRelativePath,
            "Unity Mono + BepInEx 5 جاهز",
            "جاهز للتثبيت بنقرة واحدة مع نسخة احتياطية كاملة لإصلاح التفاف الأسطر العربية داخل الصناديق الضيقة.");
    }

    public static InstallResult InstallStandalone(ProjectStore project, CancellationToken cancellation = default)
    {
        string destination = PackageInstaller.ResolveInstallDestination(project);
        string? running = PackageInstaller.FindRunningExecutableIn(destination);
        if (running != null)
            throw new InvalidOperationException($"أغلق اللعبة ({running}) أولاً قبل تثبيت الملحق.");

        if (DetectBackend(destination) == UnityScriptingBackend.Il2Cpp)
            throw new InvalidOperationException("هذه اللعبة تستخدم محرك IL2CPP ولا تدعم ملحق Unity Mono الكلاسيكي.");

        Identity.RequireNoLinks(destination);

        // If already standalone-installed cleanly, restore first so re-install updates idempotently
        string receiptDir = ReceiptDirectory(project);
        string receiptFile = Path.Combine(receiptDir, ReceiptFileName);
        if (File.Exists(receiptFile))
        {
            if (CheckReceiptState(receiptFile, cancellation) == ReceiptMatchState.Localized)
                UninstallStandalone(project, cancellation);
            else
                Directory.Delete(receiptDir, recursive: true);
        }

        string stagingDir = Path.Combine(project.DirectoryPath, "outputs", "rtl-plugin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stagingDir);
        try
        {
            byte[] dllBytes = LoadPluginBytes();
            byte[] cfgBytes = BuildDefaultConfigBytes();

            var changes = new List<PackageChange>();
            foreach (var (relPath, payloadBytes) in new[]
            {
                (PluginRelativePath, dllBytes),
                (ConfigRelativePath, cfgBytes)
            })
            {
                string targetInGame = Path.Combine(destination, relPath.Replace('/', Path.DirectorySeparatorChar));
                string origHash = File.Exists(targetInGame) ? PackageInstaller.FileHash(targetInGame, cancellation) : "";
                string newHash = Identity.Hash(payloadBytes);

                string stagedFile = Path.Combine(stagingDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
                File.WriteAllBytes(stagedFile, payloadBytes);

                if (origHash != newHash)
                    changes.Add(new PackageChange(relPath, origHash, newHash));
            }

            if (changes.Count == 0)
                return new InstallResult(receiptDir, 0);

            PackageInstaller.WriteManifest(stagingDir, new InstallManifest(".", changes));
            return PackageInstaller.InstallToReceipt(stagingDir, destination, receiptDir, cancellation);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }
        }
    }

    public static int UninstallStandalone(ProjectStore project, CancellationToken cancellation = default)
    {
        string receiptDir = ReceiptDirectory(project);
        string receiptFile = Path.Combine(receiptDir, ReceiptFileName);
        if (!File.Exists(receiptFile))
            return 0;

        string destination = PackageInstaller.ResolveInstallDestination(project);
        string? running = PackageInstaller.FindRunningExecutableIn(destination);
        if (running != null)
            throw new InvalidOperationException($"أغلق اللعبة ({running}) أولاً قبل إزالة الملحق.");

        int restored = PackageInstaller.Restore(receiptDir, cancellation);
        if (Directory.Exists(receiptDir))
            Directory.Delete(receiptDir, recursive: true);

        TryPruneEmptyDirectory(Path.Combine(destination, "BepInEx", "plugins", "Locara.UnityRtlFix"));
        return restored;
    }

    private static void TryPruneEmptyDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    private enum ReceiptMatchState { Original, Localized, ModifiedExternally }

    private static ReceiptMatchState CheckReceiptState(string receiptPath, CancellationToken cancellation)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(receiptPath));
            if (receipt == null || receipt.Changes.Count == 0)
                return ReceiptMatchState.Original;

            int localized = 0;
            int external = 0;
            foreach (var change in receipt.Changes)
            {
                string target = Path.Combine(receipt.Destination, change.Path.Replace('/', Path.DirectorySeparatorChar));
                bool isAdded = change.OriginalHash.Length == 0;
                if (!File.Exists(target))
                {
                    if (!isAdded) external++;
                    continue;
                }
                string hash = PackageInstaller.FileHash(target, cancellation);
                if (hash == change.LocalizedHash) localized++;
                else if (isAdded || hash != change.OriginalHash) external++;
            }

            if (external > 0) return ReceiptMatchState.ModifiedExternally;
            return localized > 0 ? ReceiptMatchState.Localized : ReceiptMatchState.Original;
        }
        catch
        {
            return ReceiptMatchState.Original;
        }
    }

    private static bool ReceiptContainsPlugin(string receiptPath)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(receiptPath));
            return receipt?.Changes.Any(c =>
                c.Path.Replace('\\', '/').Equals(PluginRelativePath, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }
}
