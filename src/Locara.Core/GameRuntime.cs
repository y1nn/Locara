using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Locara.Core;

public static partial class GameRuntime
{
    private const string ArabicTemplates = "game/tl/arabic";

    internal static bool HasArabicTemplates(string root)
    {
        string directory = Path.Combine(root, ArabicTemplates);
        if (!Directory.Exists(directory)) return false;
        Identity.RequireNoLinks(directory);
        return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
        }).Any(file => Formats.For(file) is RenpyFormat);
    }
    internal static bool IsArabicTemplate(string resource) => resource.StartsWith(ArabicTemplates + "/", StringComparison.OrdinalIgnoreCase)
        && Formats.For(resource) is RenpyFormat;

    internal static string RpgMakerVersion(string root)
    {
        if (File.Exists(Path.Combine(root, "js/rmmz_core.js")) || File.Exists(Path.Combine(root, "www/js/rmmz_core.js"))) return "MZ";
        if (File.Exists(Path.Combine(root, "js/rpg_core.js")) || File.Exists(Path.Combine(root, "www/js/rpg_core.js"))) return "MV";
        return "";
    }

    public static async Task RequireGodot(string executable, string version, CancellationToken cancellation = default)
    {
        executable = Path.GetFullPath(executable);
        Identity.RequireNoLinks(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("اختر برنامج Godot المطابق.", executable);
        string console = Path.Combine(Path.GetDirectoryName(executable)!, Path.GetFileNameWithoutExtension(executable) + "_console.exe");
        var info = ProcessInfo(File.Exists(console) ? console : executable, Path.GetDirectoryName(executable)!, "--version");
        string reported = await RunCommand(info, cancellation);
        var wanted = GodotSeries().Match(version);
        var installed = GodotSeries().Match(reported.Trim());
        if (!wanted.Success || !installed.Success || wanted.Value != installed.Value)
            throw new InvalidOperationException($"المحرك المحدد {reported.Trim()} والحزمة تتطلب سلسلة Godot {wanted.Value}.");
    }

    public static string LaunchGodot(string executable, string build, CancellationToken cancellation = default)
    {
        var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(Path.Combine(build, "install.json")))
            ?? throw new InvalidDataException("بيان حزمة Godot غير صالح.");
        var change = manifest.Changes.Single();
        string relative = change.Path;
        string archive = Path.Combine(build, manifest.Payload == "." ? "" : Identity.SafePath(manifest.Payload), Identity.SafePath(relative));
        Identity.RequireNoLinks(archive);
        if (PackageInstaller.FileHash(archive, cancellation) != change.LocalizedHash)
            throw new IOException("تغيّرت حزمة Godot بعد البناء.");
        using var resources = new PckContainer().Index(archive, null, cancellation);
        if (!resources.Entries.Any(item => item.Path is "project.godot" or "project.binary"))
            throw new InvalidOperationException("تحتاج هذه الحزمة إلى مشروع Godot يحمّلها.");
        cancellation.ThrowIfCancellationRequested();
        Start(ProcessInfo(executable, Path.GetDirectoryName(archive)!, "--main-pack", archive));
        return archive;
    }

    public static void RequireRenpySdk(string sdk)
    {
        _ = RenpyProcess(sdk, sdk, "run");
    }

    public static async Task GenerateRenpyTemplates(string source, string destination, string sdk, CancellationToken cancellation = default)
    {
        destination = Path.GetFullPath(destination);
        using var sourceFiles = new FolderContainer().Index(source, null, cancellation);
        if (!sourceFiles.Entries.Any(entry => IsRenpyGameFile(entry.Path) && !entry.Path.StartsWith("game/tl/", StringComparison.OrdinalIgnoreCase)
            && Formats.For(entry.Path) is RenpyFormat))
            throw new InvalidOperationException("اختر مجلد مشروع Ren’Py الذي يحتوي ملفات السكربت الأصلية.");
        if (HasArabicTemplates(source))
            throw new InvalidOperationException("قوالب العربية موجودة. افتح المشروع وافحصها.");
        await InRenpyCopy(source, destination, sourceFiles, async () =>
        {
            string log = await RunCommand(RenpyProcess(sdk, destination, "translate", "arabic", "--empty"), cancellation);
            if (!HasArabicTemplates(destination))
                throw new InvalidDataException("لم يولّد SDK قوالب العربية. " + log);
        }, cancellation);
    }

    public static async Task LaunchRenpy(ProjectStore project, string package, string copy, string sdk,
        CancellationToken cancellation = default)
    {
        copy = Path.GetFullPath(copy);
        string source = project.Info.Source;
        using var sourceFiles = new FolderContainer().Index(source, null, cancellation);
        await InRenpyCopy(source, copy, sourceFiles, () =>
        {
            PackageInstaller.Install(project, package, copy, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var process = RenpyProcess(sdk, copy, "run");
            process.Environment["RENPY_LANGUAGE"] = "arabic";
            Start(process);
            return Task.CompletedTask;
        }, cancellation);
    }

    private static async Task InRenpyCopy(string source, string copy, ResourceIndex index, Func<Task> work, CancellationToken cancellation)
    {
        copy = Path.GetFullPath(copy);
        Identity.RequireNoLinks(copy);
        if (File.Exists(copy) || Directory.Exists(copy) || Identity.Within(copy, source))
            throw new IOException("اختر مسار نسخة جديداً خارج مجلد اللعبة.");
        Directory.CreateDirectory(copy);
        try
        {
            new FolderContainer().WriteIndexed(copy, index.Entries.Where(entry => IsRenpyGameFile(entry.Path)).ToArray(),
                null, cancellation);
            await work();
        }
        catch
        {
            Directory.Delete(copy, recursive: true);
            throw;
        }
    }

    private static bool IsRenpyGameFile(string path) => path.StartsWith("game/", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("game/saves/", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("game/cache/", StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo RenpyProcess(string sdk, string project, params string[] arguments)
    {
        sdk = Path.GetFullPath(sdk);
        string python = Path.Combine(sdk, "lib/py3-windows-x86_64/python.exe");
        string script = Path.Combine(sdk, "renpy.py");
        Identity.RequireNoLinks(python);
        Identity.RequireNoLinks(script);
        if (!File.Exists(python) || !File.Exists(script))
            throw new FileNotFoundException("اختر مجلد Ren’Py SDK المطابق الذي يحتوي على renpy.py.");
        var process = ProcessInfo(python, sdk, [script, project, .. arguments]);
        process.CreateNoWindow = true;
        process.Environment["RENPY_PATH_TO_SAVES"] = Path.Combine(project, "locara-saves");
        process.Environment["RENPY_MULTIPERSISTENT"] = Path.Combine(project, "locara-multipersistent");
        return process;
    }

    private static ProcessStartInfo ProcessInfo(string file, string directory, params string[] arguments)
    {
        var result = new ProcessStartInfo(file) { WorkingDirectory = directory, UseShellExecute = false };
        foreach (string argument in arguments) result.ArgumentList.Add(argument);
        return result;
    }

    private static void Start(ProcessStartInfo info)
    {
        using var process = Process.Start(info) ?? throw new IOException("تعذّر تشغيل المحرك.");
    }

    private static async Task<string> RunCommand(ProcessStartInfo info, CancellationToken cancellation)
    {
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new IOException("تعذّر بدء أداة المحرك.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        string log = (await output).Trim();
        string diagnostic = (await errors).Trim();
        if (process.ExitCode != 0)
        {
            string message = diagnostic.Length > 0 ? diagnostic : log;
            throw new InvalidOperationException($"أعاد المحرك الرمز {process.ExitCode}. {message}");
        }
        return log;
    }

    [GeneratedRegex(@"^\d+\.\d+")]
    private static partial Regex GodotSeries();
}
