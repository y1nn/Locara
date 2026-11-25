using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Locara.Core;

public record PackageChange(string Path, string OriginalHash, string LocalizedHash);
public record InstallManifest(string Payload, IReadOnlyList<PackageChange> Changes);
public record InstallReceipt(string Destination, IReadOnlyList<PackageChange> Changes);
public record InstallResult(string ReceiptDirectory, int Files);
public record InPlaceInstallState(
    string Destination,
    string ReceiptDirectory,
    int Files,
    DateTimeOffset InstalledAt,
    string Engine,
    string Summary,
    bool ModifiedExternally,
    int ModifiedCount);

public static class PackageInstaller
{
    private const string ManifestName = "install.json";
    private const string ReceiptName = "receipt.json";
    private const string MetaName = "meta.json";
    private record InPlaceMeta(DateTimeOffset InstalledAt, string Engine, string Summary, string PackageDirectory);

    internal static void WriteManifest(string staging, InstallManifest manifest) =>
        File.WriteAllText(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest));

    public static string InPlaceReceiptDirectory(ProjectStore project) =>
        Path.Combine(project.DirectoryPath, "live-install");

    public static string ResolveInstallDestination(ProjectStore project)
    {
        string source = project.Info.Source;
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("اختر مجلد اللعبة أولاً.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        if (Directory.Exists(full)) return full;
        if (File.Exists(full))
            return Path.GetDirectoryName(full) ?? throw new DirectoryNotFoundException("تعذّر تحديد مجلد اللعبة المصدر.");
        throw new DirectoryNotFoundException("مجلد اللعبة المصدر غير موجود: " + full);
    }

    public static InPlaceInstallState? GetInPlaceStatus(ProjectStore project, CancellationToken cancellation = default)
    {
        string liveDir = InPlaceReceiptDirectory(project);
        string receiptPath = Path.Combine(liveDir, ReceiptName);
        if (!File.Exists(receiptPath)) return null;

        InstallReceipt receipt;
        try { receipt = Read<InstallReceipt>(receiptPath); }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { return null; }

        int localized = 0, original = 0, external = 0;
        foreach (var change in receipt.Changes)
        {
            cancellation.ThrowIfCancellationRequested();
            string target = Resolve(receipt.Destination, change.Path);
            bool isAdded = change.OriginalHash.Length == 0;
            if (!File.Exists(target))
            {
                if (isAdded) original++;
                else external++;
                continue;
            }
            string hash = FileHash(target, cancellation);
            if (hash == change.LocalizedHash) localized++;
            else if (!isAdded && hash == change.OriginalHash) original++;
            else external++;
        }
        // If all game files already match OriginalHash (e.g., Steam verified integrity), treat as cleanly restored.
        if (localized == 0 && external == 0) return null;

        InPlaceMeta? meta = null;
        string metaPath = Path.Combine(liveDir, MetaName);
        if (File.Exists(metaPath))
        {
            try { meta = Read<InPlaceMeta>(metaPath); }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { }
        }
        return new InPlaceInstallState(
            receipt.Destination,
            liveDir,
            receipt.Changes.Count,
            meta?.InstalledAt ?? File.GetLastWriteTimeUtc(receiptPath),
            meta?.Engine ?? "",
            meta?.Summary ?? "",
            external > 0,
            external);
    }

    public static InstallResult InstallInPlace(ProjectStore project, string package,
        string engine = "", string summary = "", CancellationToken cancellation = default)
    {
        string destination = ResolveInstallDestination(project);
        RequireGameClosed(destination);
        var current = GetInPlaceStatus(project, cancellation);
        if (current != null)
        {
            if (current.ModifiedExternally)
                throw new InvalidOperationException("تغيّرت ملفات اللعبة خارجياً (مثل تحديث رسمي للعبة). اعتمد التحديث وأعد فحص الملفات أولاً.");
            throw new InvalidOperationException("التعريب مثبّت بالفعل داخل اللعبة. استخدم خيار التحديث أو استعد الملفات الأصلية أولاً.");
        }

        string liveDir = InPlaceReceiptDirectory(project);
        if (Directory.Exists(liveDir))
            Directory.Delete(liveDir, recursive: true);

        var result = InstallVerified(package, destination, cancellation, source: null, journal: null,
            verifyClosed: () => RequireGameClosed(destination), receiptOverride: liveDir);
        PublishTrialFile(Path.Combine(liveDir, MetaName), new InPlaceMeta(
            DateTimeOffset.UtcNow, engine, summary, Path.GetFullPath(package)));
        return result;
    }

    public static int RestoreInPlace(ProjectStore project, CancellationToken cancellation = default)
    {
        string liveDir = InPlaceReceiptDirectory(project);
        string receiptPath = Path.Combine(liveDir, ReceiptName);
        if (!File.Exists(receiptPath)) return 0;
        var receipt = Read<InstallReceipt>(receiptPath);
        RequireGameClosed(receipt.Destination);
        int restored = Restore(liveDir, cancellation);
        if (Directory.Exists(liveDir))
            Directory.Delete(liveDir, recursive: true);
        return restored;
    }

    public static void ClearInPlaceRecord(ProjectStore project)
    {
        string liveDir = InPlaceReceiptDirectory(project);
        if (Directory.Exists(liveDir))
            Directory.Delete(liveDir, recursive: true);
    }

    public static (BuildResult Build, InstallResult Install, bool UpdatedExisting) DeployInPlace(
        ProjectStore project, string outputDirectory, bool final = true, string? password = null,
        CancellationToken cancellation = default)
    {
        string destination = ResolveInstallDestination(project);
        RequireGameClosed(destination);
        var existing = GetInPlaceStatus(project, cancellation);
        bool updatedExisting = false;
        if (existing != null)
        {
            if (existing.ModifiedExternally)
                throw new InvalidOperationException("تغيّرت ملفات اللعبة خارجياً (مثل تحديث رسمي للعبة). اعتمد التحديث وأعد فحص الملفات أولاً.");
            RestoreInPlace(project, cancellation);
            updatedExisting = true;
        }

        var service = new ProjectService(project);
        var plan = service.Preview(final, password, cancellation);
        if (plan.ChangedFiles.Count == 0)
            throw new InvalidOperationException("لا توجد ترجمات معتمدة أو تغييرات مؤهلة للتثبيت.");

        var build = service.Build(outputDirectory, final, password, cancellation, plan);
        project.LastOutput = build.Directory;
        string engine = project.Get("scanned_engine");
        string summary = plan.Notice.Length > 0
            ? plan.Notice
            : $"{build.Translations} نص مترجم";
        var install = InstallInPlace(project, build.Directory, engine, summary, cancellation);
        return (build, install, updatedExisting);
    }

    public static string? FindRunningExecutableIn(string gameDirectory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            return null;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    string? image = TryGetProcessImagePath(process.Id);
                    if (image != null && Identity.Within(image, root))
                        return Path.GetFileName(image);
                }
                catch { }
            }
            return null;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static void RequireGameClosed(string gameDirectory)
    {
        string? running = FindRunningExecutableIn(gameDirectory);
        if (running != null)
            throw new InvalidOperationException($"أغلق اللعبة ({running}) أولاً قبل تعديل أو استعادة ملفاتها.");
    }

    internal static string? TryGetProcessImagePath(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 4) return null;
        const uint processQueryLimitedInformation = 0x1000;
        nint handle = OpenProcess(processQueryLimitedInformation, false, processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size == 0)
                return null;
            return new string(buffer, 0, (int)size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    public static InstallResult Install(ProjectStore project, string package, string copy, CancellationToken cancellation = default)
    {
        string source = project.Info.Source;
        if (string.IsNullOrEmpty(source)) throw new InvalidOperationException("Open the project that built this package.");
        return InstallVerified(package, copy, cancellation, source: source);
    }

    internal static InstallResult InstallTrial(string package, string target, string journal,
        Action verifyClosed, CancellationToken cancellation) =>
        InstallVerified(package, target, cancellation, journal: journal, verifyClosed: verifyClosed);

    internal static InstallResult InstallGenericTrial(string package, string target, string journal,
        Action verifyClosed, CancellationToken cancellation) =>
        InstallVerified(package, target, cancellation, verifyClosed: verifyClosed, receiptOverride: journal);

    private static InstallResult InstallVerified(string package, string copy, CancellationToken cancellation,
        string? source = null, string? journal = null, Action? verifyClosed = null, string? receiptOverride = null)
    {
        package = Path.GetFullPath(package);
        copy = Path.GetFullPath(copy);
        Identity.RequireNoLinks(package);
        Identity.RequireNoLinks(copy);
        if (!Directory.Exists(copy)) throw new DirectoryNotFoundException("Choose the directory of the game copy.");
        if (Identity.Within(copy, package) || Identity.Within(package, copy))
            throw new IOException("The exported package and the game copy must be in separate directories.");

        var manifest = Read<InstallManifest>(Path.Combine(package, ManifestName));
        if (manifest.Changes.Count == 0) throw new InvalidOperationException("This export has no translated changes to install.");
        if (journal != null) StarTruckerDisplay.ValidateTrialChanges(manifest.Changes);
        string payload = manifest.Payload == "." ? package : Resolve(package, manifest.Payload);

        var targets = manifest.Changes.Select(change =>
        {
            string target = Resolve(copy, change.Path);
            if (source != null && Identity.Within(target, source))
                throw new IOException("Choose a separate game copy.");
            return (Change: change, Target: target, Localized: Resolve(payload, change.Path));
        }).ToList();

        if (targets.Select(item => item.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new InvalidDataException("Duplicate installation paths.");

        // Verify all files before the first change. Preserve backups separately from the game.
        foreach (var item in targets)
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Change.OriginalHash.Length == 0)
            {
                if (File.Exists(item.Target))
                    throw new IOException("ملف التعديل موجود بالفعل في اللعبة. أزله أو استعد الأصل أولاً — " + item.Change.Path);
            }
            else
            {
                Verify(item.Target, item.Change.OriginalHash);
            }
            Verify(item.Localized, item.Change.LocalizedHash);
        }
        string receipt = journal ?? receiptOverride ?? Path.Combine(package, "installation-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(receipt, "original");
        Identity.RequireNoLinks(backup);
        Directory.CreateDirectory(backup);
        foreach (var item in targets)
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Change.OriginalHash.Length == 0) continue;
            string path = Resolve(backup, item.Change.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                if (journal == null && receiptOverride == null) File.Copy(item.Target, path);
                else
                {
                    using var input = File.OpenRead(item.Target);
                    using var output = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    Containers.Copy(input, output, input.Length, cancellation);
                    output.Flush(flushToDisk: true);
                }
            }
            catch (IOException error) when (IsSharingViolation(error))
            {
                throw new IOException("الملف قيد الاستخدام من برنامج آخر أو من اللعبة. أغلق اللعبة وأي نافذة تستخدم الملف ثم أعد المحاولة — " + item.Target, error);
            }
            Verify(path, item.Change.OriginalHash, cancellation);
        }
        PublishTrialFile(Path.Combine(receipt, ReceiptName), new InstallReceipt(copy, manifest.Changes));

        verifyClosed?.Invoke();
        Apply(targets.Select(item => (item.Target, item.Localized, Resolve(backup, item.Change.Path),
            item.Change.OriginalHash, item.Change.LocalizedHash)), cancellation, verifyClosed);
        return new(receipt, targets.Count);
    }

    internal static InstallReceipt? ReadTrial(string directory)
    {
        string path = Path.Combine(directory, ReceiptName);
        Identity.RequireNoLinks(path);
        return File.Exists(path) ? Read<InstallReceipt>(path) : null;
    }

    internal static void RestoreTrial(string directory, InstallReceipt receipt, Action verifyClosed)
    {
        StarTruckerDisplay.ValidateTrialChanges(receipt.Changes);
        RestoreTrialCore(directory, receipt, verifyClosed);
    }

    internal static void RestoreGenericTrial(string directory, InstallReceipt receipt, Action verifyClosed) =>
        RestoreTrialCore(directory, receipt, verifyClosed);

    private static void RestoreTrialCore(string directory, InstallReceipt receipt, Action verifyClosed)
    {
        var issues = new List<string>();
        foreach (var change in receipt.Changes)
        {
            try
            {
                verifyClosed();
                string target = Resolve(receipt.Destination, change.Path);
                if (change.OriginalHash.Length == 0)
                {
                    if (!File.Exists(target)) continue;
                    Replace(target, "", change.LocalizedHash, "");
                    continue;
                }
                string current = FileHash(target);
                if (current == change.OriginalHash) continue;
                string backup = Resolve(Path.Combine(directory, "original"), change.Path);
                Verify(backup, change.OriginalHash);
                Replace(target, backup, change.LocalizedHash, change.OriginalHash);
                Verify(target, change.OriginalHash);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                issues.Add(change.Path + " — " + error.Message);
            }
        }
        if (issues.Count > 0)
            throw new IOException(string.Join('\n', issues) + "\nالنسخ الاحتياطية: " + Path.Combine(directory, "original"));
        File.Delete(Path.Combine(directory, ReceiptName));
    }

    internal static void PublishTrialFile<T>(string path, T data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static int Restore(string receiptDirectory, CancellationToken cancellation = default)
    {
        receiptDirectory = Path.GetFullPath(receiptDirectory);
        string backup = Path.Combine(receiptDirectory, "original");
        var receipt = Read<InstallReceipt>(Path.Combine(receiptDirectory, ReceiptName));
        if (File.Exists(Path.Combine(receiptDirectory, "trial.lock")))
            throw new InvalidOperationException("استخدم استعادة تجربة Steam داخل Locara.");
        string beforeRestore = Path.Combine(receiptDirectory, "restore-" + Guid.NewGuid().ToString("N"));
        var pending = new List<(string Target, string Replacement, string Rollback, string Before, string After)>();
        foreach (var change in receipt.Changes)
        {
            cancellation.ThrowIfCancellationRequested();
            string target = Resolve(receipt.Destination, change.Path);
            if (change.OriginalHash.Length == 0)
            {
                if (!File.Exists(target)) continue;
                string addedHash = FileHash(target, cancellation);
                if (addedHash != change.LocalizedHash)
                    throw new IOException($"تغيّر الملف بعد التثبيت. راجع نسختك الحالية قبل الاستعادة — {target}");
                pending.Add((target, "", Resolve(beforeRestore, change.Path), change.LocalizedHash, ""));
                continue;
            }
            string hash = FileHash(target, cancellation);
            if (hash == change.OriginalHash) continue;
            if (hash != change.LocalizedHash)
                throw new IOException($"تغيّر الملف بعد التثبيت. راجع نسختك الحالية قبل الاستعادة — {target}");
            string original = Resolve(backup, change.Path);
            Verify(original, change.OriginalHash, cancellation);
            pending.Add((target, original, Resolve(beforeRestore, change.Path), change.LocalizedHash, change.OriginalHash));
        }

        bool rollbackIncomplete = false;
        try
        {
            foreach (var file in pending)
            {
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(file.Rollback)!);
                try
                {
                    using var input = File.OpenRead(file.Target);
                    using var output = new FileStream(file.Rollback, FileMode.CreateNew, FileAccess.Write);
                    Containers.Copy(input, output, input.Length, cancellation);
                }
                catch (IOException error) when (IsSharingViolation(error))
                {
                    throw new IOException("الملف قيد الاستخدام من برنامج آخر أو من اللعبة. أغلق اللعبة وأي نافذة تستخدم الملف ثم أعد المحاولة — " + file.Target, error);
                }
                Verify(file.Rollback, file.Before, cancellation);
            }
            try { Apply(pending, cancellation); }
            catch (AggregateException error)
            {
                rollbackIncomplete = true;
                throw new IOException($"تعذّر إكمال التراجع. تجد نسخ ما قبل الاستعادة في {beforeRestore}", error);
            }
            return pending.Count;
        }
        finally
        {
            if (!rollbackIncomplete && Directory.Exists(beforeRestore))
                Directory.Delete(beforeRestore, recursive: true);
        }
    }

    private static void Apply(IEnumerable<(string Target, string Replacement, string Rollback, string Before, string After)> files,
        CancellationToken cancellation, Action? verifyClosed = null)
    {
        var completed = new Stack<(string Target, string Rollback, string Expected, string Original)>();
        try
        {
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                verifyClosed?.Invoke();
                Replace(file.Target, file.Replacement, file.Before, file.After);
                completed.Push((file.Target, file.Rollback, file.After, file.Before));
            }
        }
        catch
        {
            List<Exception> failures = [];
            foreach (var file in completed)
            {
                try
                {
                    verifyClosed?.Invoke();
                    Replace(file.Target, file.Rollback, file.Expected, file.Original);
                }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count > 0)
                throw new AggregateException("Some files could not be rolled back. Original copies remain in the receipt folder.", failures);
            throw;
        }
    }

    private static bool IsSharingViolation(IOException error) =>
        (error.HResult & 0xFFFF) is 32 or 33;

    private static void ClearReadOnlyIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
    }

    private static void Replace(string target, string replacement, string before, string after)
    {
        if (after.Length == 0)
        {
            if (!File.Exists(target)) return;
            Verify(target, before);
            try
            {
                ClearReadOnlyIfPresent(target);
                File.Delete(target);
            }
            catch (IOException error) when (IsSharingViolation(error))
            {
                throw new IOException("الملف قيد الاستخدام من برنامج آخر أو من اللعبة. أغلق اللعبة وأي نافذة تستخدم الملف ثم أعد المحاولة — " + target, error);
            }
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)
                && Path.GetFileName(parent).Equals("~mods", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                try { Directory.Delete(parent); } catch (IOException) { }
            }
            return;
        }

        string? targetDir = Path.GetDirectoryName(target);
        if (before.Length == 0 && !string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        string temporary = target + ".locara-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(replacement, temporary);
            ClearReadOnlyIfPresent(temporary);
            Verify(temporary, after);
            if (before.Length == 0)
            {
                if (File.Exists(target))
                    throw new IOException("ملف التعديل موجود بالفعل في الوجهة: " + target);
                File.Move(temporary, target);
            }
            else
            {
                Verify(target, before);
                ClearReadOnlyIfPresent(target);
                File.Replace(temporary, target, null);
            }
        }
        catch (IOException error) when (IsSharingViolation(error))
        {
            throw new IOException("الملف قيد الاستخدام من برنامج آخر أو من اللعبة. أغلق اللعبة وأي نافذة تستخدم الملف ثم أعد المحاولة — " + target, error);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Resolve(string root, string relative)
    {
        string path = Path.Combine(root, Identity.SafePath(relative));
        Identity.RequireNoLinks(path);
        return path;
    }

    internal static string FileHash(string path, CancellationToken cancellation = default)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            Containers.Copy(stream, Stream.Null, stream.Length, cancellation, checksum: hash);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (IOException error) when (IsSharingViolation(error))
        {
            throw new IOException("الملف قيد الاستخدام من برنامج آخر أو من اللعبة. أغلق اللعبة وأي نافذة تستخدم الملف ثم أعد المحاولة — " + path, error);
        }
    }

    private static void Verify(string path, string expected, CancellationToken cancellation = default)
    {
        if (!File.Exists(path) || FileHash(path, cancellation) != expected)
            throw new IOException("File is missing or differs from the expected version: " + path);
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid package metadata.");
}
