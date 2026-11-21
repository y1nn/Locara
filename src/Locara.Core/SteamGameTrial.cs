using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Locara.Core;

public static class SteamGameTrial
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ExitGracePollInterval = TimeSpan.FromMilliseconds(1000);
    private static string Journals => Path.Combine(LocaraPaths.UserDataDirectory, "SteamTrials");

    public static SteamGame? FindSteamGame(string sourcePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
            return null;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        return SteamLibrary.Discover().FirstOrDefault(game =>
            StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Directory)), root));
    }

    public static SteamGame ConfirmDestination(ProjectStore project, PackagePreview preview)
    {
        if (preview.ChangedFiles.Count == 0)
            throw new InvalidOperationException(preview.Notice.Length > 0 ? preview.Notice
                : "احفظ ترجمة لسطر واحد على الأقل ثم جرّبها داخل اللعبة.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("تشغيل تجربة Steam متاح على Windows.");

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Info.Source));
        Identity.RequireNoLinks(root);
        var game = FindSteamGame(root)
            ?? throw new InvalidOperationException("اختر مجلد لعبة مثبّتة فعلياً داخل مكتبة Steam.");
        return game;
    }

    public static Task<BuildResult> RunAsync(string projectDirectory, SteamGame game, string output,
        PackagePreview preview, string? password, IProgress<string>? progress, CancellationToken cancellation) =>
        RunCoreAsync(projectDirectory, game, output, preview, password, progress, cancellation, LaunchTimeout,
            () =>
            {
                using var launcher = Process.Start(new ProcessStartInfo($"steam://rungameid/{game.AppId}") { UseShellExecute = true });
            });

    internal static async Task<BuildResult> RunCoreAsync(string projectDirectory, SteamGame game, string output,
        PackagePreview preview, string? password, IProgress<string>? progress, CancellationToken cancellation,
        TimeSpan startupWindow, Action launch)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Directory));
        BuildResult build;
        using var journal = LockJournal(JournalFor(target));
        string directory = Path.GetDirectoryName(journal.Name)!;
        if (PackageInstaller.ReadTrial(directory) != null)
            throw new InvalidOperationException("توجد تجربة سابقة تنتظر الاستعادة.");
        string launchRecord = Path.Combine(directory, "launch.json");
        File.Delete(launchRecord);

        using (var project = new ProjectStore(projectDirectory))
        {
            var confirmed = ConfirmDestination(project, preview);
            if (!StringComparer.OrdinalIgnoreCase.Equals(confirmed.Directory, target))
                throw new InvalidOperationException("تغيّرت وجهة التجربة بعد الموافقة.");
            RequireClosed(target, game.Name);
            progress?.Report("جارٍ بناء حزمة التجربة");
            build = new ProjectService(project).Build(output, final: false, password: password, cancellation: cancellation, reviewed: preview);
            project.LastOutput = build.Directory;
        }

        bool sawGame = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report($"جارٍ حفظ الأصل وتطبيق حزمة التجربة على {game.Name}");
            RequireClosed(target, game.Name);
            PackageInstaller.InstallGenericTrial(build.Directory, target, directory, () => RequireClosed(target, game.Name), cancellation);
            cancellation.ThrowIfCancellationRequested();
            RequireClosed(target, game.Name);

            progress?.Report($"جارٍ تشغيل {game.Name} عبر Steam");
            PackageInstaller.PublishTrialFile(launchRecord, DateTimeOffset.UtcNow + startupWindow);
            launch();
        }
        finally
        {
            if (PackageInstaller.ReadTrial(directory) is { } receipt)
            {
                sawGame = await WaitForExit(target, game.Name, ReadLaunchDeadline(directory), progress, CancellationToken.None);
                progress?.Report($"جارٍ استعادة ملفات {game.Name} الأصلية");
                PackageInstaller.RestoreGenericTrial(directory, receipt, () => RequireClosed(target, game.Name));
                File.Delete(launchRecord);
            }
        }
        if (!sawGame)
            throw new InvalidOperationException($"لم تبدأ {game.Name} ضمن مهلة التشغيل. استُعيدت الملفات الأصلية.");
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
                    string gameName = Path.GetFileName(receipt.Destination);
                    progress?.Report($"Locara يتابع استعادة تجربة {gameName} السابقة");
                    await WaitForExit(receipt.Destination, gameName, ReadLaunchDeadline(directory), progress, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    PackageInstaller.RestoreGenericTrial(directory, receipt, () => RequireClosed(receipt.Destination, gameName));
                    File.Delete(Path.Combine(directory, "launch.json"));
                    progress?.Report($"استُعيدت ملفات {gameName} الأصلية بعد التجربة السابقة.");
                }
                catch (Exception error) when (error is IOException or InvalidOperationException
                    or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    progress?.Report("تحتاج تجربة Steam إلى مراجعة\n" + error.Message + "\n" + directory);
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

    private static async Task<bool> WaitForExit(string target, string gameName, DateTimeOffset deadline,
        IProgress<string>? progress, CancellationToken cancellation)
    {
        bool observed = false;
        DateTimeOffset? firstAbsent = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using Process? running = FindRunningProcessIn(target);
            if (running != null)
            {
                if (!observed) progress?.Report($"التجربة قيد التشغيل. أغلق {gameName} لاستعادة الملفات الأصلية.");
                observed = true;
                firstAbsent = null;
                try
                {
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

    private static void RequireClosed(string target, string gameName)
    {
        string? exe = PackageInstaller.FindRunningExecutableIn(target);
        if (exe != null)
            throw new InvalidOperationException($"أغلق {gameName} ({exe}) قبل تعديل ملفات هذا التثبيت.");
    }

    private static Process? FindRunningProcessIn(string gameDirectory)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(gameDirectory)) return null;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        var processes = Process.GetProcesses();
        Process? match = null;
        try
        {
            foreach (var process in processes)
            {
                if (match != null) { process.Dispose(); continue; }
                try
                {
                    string? image = PackageInstaller.TryGetProcessImagePath(process.Id);
                    if (image != null && Identity.Within(image, root))
                    {
                        match = process;
                        continue;
                    }
                }
                catch { }
                process.Dispose();
            }
            return match;
        }
        catch
        {
            match?.Dispose();
            throw;
        }
    }
}
