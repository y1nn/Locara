using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Locara.Core;

public static class StarTruckerTrial
{
    private const string AppId = "2380050";
    private const string Executable = "Star Trucker.exe";
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ExitGracePollInterval = TimeSpan.FromMilliseconds(1000);
    private static string Journals => Path.Combine(LocaraPaths.UserDataDirectory, "Trials");
    public static string ConfirmDestination(ProjectStore project, PackagePreview preview)
    {
        if (!preview.ChangedFiles.Contains(StarTruckerCatalog.LanguageArchive, StringComparer.Ordinal)
            || !StarTruckerDisplay.AssetPaths.All(preview.ChangedFiles.Contains))
            throw new InvalidOperationException(preview.Notice.Length > 0 ? preview.Notice
                : "احفظ ترجمة لعبارة من قائمة Star Trucker ثم جرّبها داخل اللعبة.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("تشغيل تجربة Steam متاح على Windows.");

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Info.Source));
        Identity.RequireNoLinks(root);
        Identity.RequireNoLinks(Path.Combine(root, Executable));
        if (!File.Exists(Path.Combine(root, Executable)) || !SteamInstalledAt(root))
            throw new InvalidOperationException("اختر تثبيت Star Trucker الفعلي المسجّل في مكتبة Steam.");
        return root;
    }

    private static bool SteamInstalledAt(string root)
    {
        var candidates = SteamLibrary.Discover().Where(game => game.AppId == AppId
            && File.Exists(Path.Combine(game.Directory, Executable))).ToArray();
        return candidates.Length == 1 && StringComparer.OrdinalIgnoreCase.Equals(candidates[0].Directory, root);
    }

    public static Task<BuildResult> RunAsync(string projectDirectory, string target, string output,
        PackagePreview preview, string? password, IProgress<string>? progress, CancellationToken cancellation) =>
        RunCoreAsync(projectDirectory, target, output, preview, password, progress, cancellation, LaunchTimeout, StartSteam);

    private static void StartSteam()
    {
        using var launcher = Process.Start(new ProcessStartInfo($"steam://rungameid/{AppId}") { UseShellExecute = true });
    }

    internal static async Task<BuildResult> RunCoreAsync(string projectDirectory, string target, string output,
        PackagePreview preview, string? password, IProgress<string>? progress, CancellationToken cancellation,
        TimeSpan startupWindow, Action launch)
    {
        BuildResult build;
        using var journal = LockJournal(JournalFor(target));
        string directory = Path.GetDirectoryName(journal.Name)!;
        if (PackageInstaller.ReadTrial(directory) != null)
            throw new InvalidOperationException("توجد تجربة سابقة تنتظر الاستعادة.");
        string launchRecord = Path.Combine(directory, "launch.json");
        File.Delete(launchRecord);

        using (var project = new ProjectStore(projectDirectory))
        {
            if (ConfirmDestination(project, preview) != target)
                throw new InvalidOperationException("تغيّرت وجهة التجربة بعد الموافقة.");
            RequireClosed(target);
            progress?.Report("جارٍ بناء حزمة التجربة");
            build = new ProjectService(project).Build(output, password: password, cancellation: cancellation, reviewed: preview);
            project.LastOutput = build.Directory;
        }
        bool sawGame = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report("جارٍ حفظ الأصل وتطبيق حزمة التجربة");
            RequireClosed(target);
            PackageInstaller.InstallTrial(build.Directory, target, directory, () => RequireClosed(target), cancellation);
            cancellation.ThrowIfCancellationRequested();
            RequireClosed(target);

            progress?.Report("جارٍ تشغيل Star Trucker عبر Steam");
            PackageInstaller.PublishTrialFile(launchRecord, DateTimeOffset.UtcNow + startupWindow);
            launch();
        }
        finally
        {
            if (PackageInstaller.ReadTrial(directory) is { } receipt)
            {
                sawGame = await WaitForExit(target, ReadLaunchDeadline(directory), progress, CancellationToken.None);
                progress?.Report("جارٍ استعادة ملفات اللعبة الأصلية");
                PackageInstaller.RestoreTrial(directory, receipt, () => RequireClosed(target));
                File.Delete(launchRecord);
            }
        }
        if (!sawGame)
            throw new InvalidOperationException("لم يبدأ Star Trucker ضمن مهلة التشغيل. استُعيدت الملفات الأصلية.");
        return build;
    }

    public static async Task RecoverPending(IProgress<string>? progress, CancellationToken cancellation)
    {
        if (!Directory.Exists(Journals)) return;
        Identity.RequireNoLinks(Journals);
        foreach (string directory in Directory.EnumerateDirectories(Journals))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(directory, "receipt.json"))) continue;
            FileStream journal;
            try { journal = LockJournal(directory); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33) { continue; }
            using (journal)
            {
                try
                {
                    var receipt = PackageInstaller.ReadTrial(directory);
                    if (receipt == null) continue;
                    if (!StringComparer.OrdinalIgnoreCase.Equals(JournalFor(receipt.Destination), directory))
                        throw new InvalidDataException("وجهة إيصال التجربة غير مطابقة.");
                    progress?.Report("Locara يتابع استعادة تجربة Star Trucker السابقة");
                    await WaitForExit(receipt.Destination, ReadLaunchDeadline(directory), progress, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    PackageInstaller.RestoreTrial(directory, receipt, () => RequireClosed(receipt.Destination));
                    File.Delete(Path.Combine(directory, "launch.json"));
                    progress?.Report("استُعيدت ملفات Star Trucker الأصلية بعد التجربة السابقة.");
                }
                catch (Exception error) when (error is IOException or InvalidOperationException
                    or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    progress?.Report("تحتاج تجربة Star Trucker إلى مراجعة\n" + error.Message + "\n" + directory);
                }
            }
        }
    }

    private static string JournalFor(string target) => Path.Combine(Journals, Identity.Hash(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)).ToUpperInvariant())));

    private static FileStream LockJournal(string directory)
    {
        string path = Path.Combine(directory, "trial.lock");
        Identity.RequireNoLinks(path);
        Directory.CreateDirectory(directory);
        return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static DateTimeOffset ReadLaunchDeadline(string directory)
    {
        string path = Path.Combine(directory, "launch.json");
        Identity.RequireNoLinks(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(path)) : DateTimeOffset.MinValue;
    }

    private static async Task<bool> WaitForExit(string target, DateTimeOffset deadline,
        IProgress<string>? progress, CancellationToken cancellation)
    {
        bool observed = false;
        DateTimeOffset? firstAbsent = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using Process? running = FindRunning(target);
            if (running != null)
            {
                if (!observed) progress?.Report("التجربة قيد التشغيل. أغلق Star Trucker لاستعادة الملفات.");
                observed = true;
                firstAbsent = null;
                try
                {
                    // Block on the OS process handle instead of polling Process.MainModule (EnumProcessModulesEx) while playing.
                    await running.WaitForExitAsync(cancellation);
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    await Task.Delay(ExitGracePollInterval, cancellation);
                }
                continue;
            }
            if (observed)
            {
                firstAbsent ??= now;
                if (now - firstAbsent >= TimeSpan.FromSeconds(2)) return true;
                await Task.Delay(ExitGracePollInterval, cancellation);
            }
            else if (now >= deadline) return false;
            else await Task.Delay(StartupPollInterval, cancellation);
        }
    }

    private static void RequireClosed(string target)
    {
        if (IsRunning(target))
            throw new InvalidOperationException("أغلق Star Trucker قبل تعديل ملفات هذا التثبيت.");
    }

    private static bool IsRunning(string target)
    {
        using Process? process = FindRunning(target);
        return process != null;
    }

    private static Process? FindRunning(string target)
    {
        string executable = Path.Combine(target, Executable);
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable));
        Process? matched = null;
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    if (process.HasExited) continue;
                    string? image = process.MainModule?.FileName;
                    if (image == null || StringComparer.OrdinalIgnoreCase.Equals(image, executable))
                    {
                        matched = process;
                        return matched;
                    }
                }
                catch (Win32Exception)
                {
                    if (!process.HasExited)
                    {
                        matched = process;
                        return matched;
                    }
                }
                catch (InvalidOperationException) { }
            }
            return null;
        }
        finally
        {
            foreach (var process in candidates)
            {
                if (!ReferenceEquals(process, matched)) process.Dispose();
            }
        }
    }
}
