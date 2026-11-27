using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Locara.Core;

public sealed record SelfUpdateEligibility(
    bool CanInstall,
    string Reason,
    string InstallDirectory,
    string ExecutablePath);

public sealed record UpdateDownloadProgress(
    string Stage,
    long BytesReceived,
    long? TotalBytes,
    double Percent);

public sealed record StagedAppUpdate(
    string Tag,
    string StagingDirectory,
    string ExtractedDirectory,
    string VerifiedSha256);

/// <summary>
/// Verified, fail-closed in-app self-updater for standalone Windows Locara builds.
/// Downloads official GitHub release assets over allowlisted HTTPS redirects only,
/// enforces strict size caps, verifies SHA-256 digests in constant time, extracts
/// archives with Zip-Slip protection, validates the PE executable header, and stages
/// a safe rollback-capable swap script.
/// </summary>
public static partial class AppSelfUpdater
{
    public const string DesktopExecutableName = "Locara.Desktop.exe";
    public const long MaxSha256FileBytes = 4096;
    public const long MaxPackageBytes = 250L * 1024 * 1024;
    public const long MaxExtractedBytes = 512L * 1024 * 1024;
    public const int MaxExtractedEntries = 2000;
    public const int MaxRedirectHops = 3;

    private static readonly SearchValues<char> UnsafeBatchCharacters =
        SearchValues.Create("&|<>^%!\"\r\n\0");

    private static readonly Lazy<HttpClient> SharedDownloadClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    });

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})[ \t]+\*?Locara-windows-x64\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256LineRegex();

    public static SelfUpdateEligibility CheckInstallability(string? appDirectory = null)
    {
        string rawDir = string.IsNullOrWhiteSpace(appDirectory) ? AppContext.BaseDirectory : appDirectory;
        string installDir;
        try
        {
            installDir = NormalizeDirectoryPath(rawDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SelfUpdateEligibility(
                false,
                "مسار مجلد التشغيل غير صالح للتحديث التلقائي. يرجى تنزيل التحديث يدوياً من صفحة الإصدارات.",
                rawDir,
                Path.Combine(rawDir, DesktopExecutableName));
        }

        string executablePath = Path.Combine(installDir, DesktopExecutableName);

        if (!Directory.Exists(installDir))
        {
            return new SelfUpdateEligibility(
                false,
                "مجلد التثبيت الحالي غير موجود. يرجى تنزيل التحديث يدوياً من صفحة الإصدارات.",
                installDir,
                executablePath);
        }

        if (IsDevBuildDirectory(installDir))
        {
            return new SelfUpdateEligibility(
                false,
                "التحديث التلقائي معطّل عند التشغيل من مجلد تطوير (bin\\Debug أو bin\\Release). يرجى استخدام النسخة الرسمية المستقلة أو التحديث يدوياً.",
                installDir,
                executablePath);
        }

        if (!File.Exists(executablePath))
        {
            return new SelfUpdateEligibility(
                false,
                "لم يتم العثور على ملف التشغيل Locara.Desktop.exe في المجلد الحالي. يرجى تنزيل التحديث يدوياً من صفحة الإصدارات.",
                installDir,
                executablePath);
        }

        try
        {
            Identity.RequireNoLinks(installDir);
            Identity.RequireNoLinks(executablePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new SelfUpdateEligibility(
                false,
                "لا يمكن تنفيذ التحديث التلقائي داخل مسار يحتوي على روابط نظام ملفات (symlink أو junction). يرجى التحديث يدوياً.",
                installDir,
                executablePath);
        }

        if (ContainsUnsafeBatchCharacters(installDir))
        {
            return new SelfUpdateEligibility(
                false,
                "مسار مجلد التثبيت يحتوي على رموز غير مدعومة للتحديث التلقائي. يرجى نقل التطبيق لمجلد عادي أو التحديث يدوياً.",
                installDir,
                executablePath);
        }

        if (!CanWriteToDirectory(installDir))
        {
            return new SelfUpdateEligibility(
                false,
                "لا يملك التطبيق صلاحية الكتابة داخل مجلد التثبيت الحالي. يرجى نقل التطبيق لمجلد قابل للكتابة أو تنزيل التحديث يدوياً.",
                installDir,
                executablePath);
        }

        return new SelfUpdateEligibility(
            true,
            "جاهز لتثبيت التحديث التلقائي.",
            installDir,
            executablePath);
    }

    public static bool TryParseSha256File(string? content, out string expectedHex)
    {
        expectedHex = "";
        if (string.IsNullOrEmpty(content))
            return false;

        string normalized = content[0] == '\uFEFF' ? content[1..] : content;
        normalized = normalized.Trim('\r', '\n');
        if (normalized.Length == 0
            || normalized.Contains('\r')
            || normalized.Contains('\n'))
        {
            return false;
        }

        Match match = Sha256LineRegex().Match(normalized);
        if (!match.Success)
            return false;

        byte[] hashBytes = Convert.FromHexString(match.Groups["hash"].Value);
        expectedHex = Convert.ToHexStringLower(hashBytes);
        return true;
    }

    public static async Task<StagedAppUpdate> DownloadAndVerifyAsync(
        ReleaseCheckResult release,
        IProgress<UpdateDownloadProgress>? progress = null,
        string? stagingRoot = null,
        HttpClient? httpClient = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (release.PackageAssetUrl is null
            || !AppUpdateChecker.IsTrustedReleaseAssetUrl(release.PackageAssetUrl)
            || !release.PackageAssetUrl.AbsolutePath.EndsWith("/" + AppUpdateChecker.WindowsPackageAssetName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Release package URL is missing or untrusted.");
        }

        if (release.Sha256AssetUrl is null
            || !AppUpdateChecker.IsTrustedReleaseAssetUrl(release.Sha256AssetUrl)
            || !release.Sha256AssetUrl.AbsolutePath.EndsWith("/" + AppUpdateChecker.WindowsPackageSha256AssetName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Release SHA-256 URL is missing or untrusted.");
        }

        if (release.PackageAssetSize is > MaxPackageBytes)
        {
            throw new InvalidDataException("Declared update package size exceeds maximum allowed limit.");
        }

        string effectiveStagingRoot = NormalizeDirectoryPath(
            string.IsNullOrWhiteSpace(stagingRoot)
                ? Path.Combine(LocaraPaths.UserDataDirectory, "updates")
                : stagingRoot);

        Identity.RequireNoLinks(effectiveStagingRoot);
        Directory.CreateDirectory(effectiveStagingRoot);

        string tag = !string.IsNullOrWhiteSpace(release.LatestTag)
            ? release.LatestTag.Trim()
            : release.LatestVersion?.ToTagString() ?? "update";
        string safeTag = SanitizeTagForFolder(tag);
        string stagingDir = Path.Combine(effectiveStagingRoot, $"{safeTag}-{Guid.NewGuid():N}");
        string zipPath = Path.Combine(stagingDir, AppUpdateChecker.WindowsPackageAssetName);
        string extractDir = Path.Combine(stagingDir, "extracted");

        Directory.CreateDirectory(stagingDir);

        bool committed = false;
        try
        {
            HttpClient client = httpClient ?? SharedDownloadClient.Value;
            string versionToken = $"{release.CurrentVersion.Major}.{release.CurrentVersion.Minor}.{release.CurrentVersion.Patch}";

            // 1. Download and strictly parse SHA-256 manifest
            progress?.Report(new UpdateDownloadProgress("sha256", 0, null, 0));
            string sha256Text;
            using (HttpResponseMessage shaResponse = await SendWithTrustedRedirectsAsync(
                client, release.Sha256AssetUrl, versionToken, cancellation).ConfigureAwait(false))
            {
                if (shaResponse.Content.Headers.ContentLength is > MaxSha256FileBytes)
                    throw new InvalidDataException("SHA-256 asset exceeds maximum allowed size.");

                await using Stream shaStream = await shaResponse.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                sha256Text = await ReadBoundedUtf8Async(shaStream, MaxSha256FileBytes, cancellation).ConfigureAwait(false);
            }

            if (!TryParseSha256File(sha256Text, out string expectedHex))
                throw new InvalidDataException("Invalid SHA-256 checksum file format.");

            byte[] expectedHashBytes = Convert.FromHexString(expectedHex);

            // 2. Download ZIP package while computing streaming SHA-256
            long bytesReceived = 0;
            long? totalBytes;
            byte[] actualHashBytes;

            using (HttpResponseMessage zipResponse = await SendWithTrustedRedirectsAsync(
                client, release.PackageAssetUrl, versionToken, cancellation).ConfigureAwait(false))
            {
                if (zipResponse.Content.Headers.ContentLength is > MaxPackageBytes)
                    throw new InvalidDataException("Update package exceeds maximum allowed size.");

                totalBytes = zipResponse.Content.Headers.ContentLength ?? release.PackageAssetSize;
                progress?.Report(new UpdateDownloadProgress("download", 0, totalBytes, 0));

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using Stream responseStream = await zipResponse.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using (var fileStream = new FileStream(
                    zipPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    while (true)
                    {
                        int read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false);
                        if (read == 0) break;

                        bytesReceived += read;
                        if (bytesReceived > MaxPackageBytes)
                            throw new InvalidDataException("Update package exceeds maximum allowed size.");

                        hasher.AppendData(buffer, 0, read);
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);

                        double percent = totalBytes is > 0
                            ? Math.Clamp(bytesReceived * 100.0 / totalBytes.Value, 0.0, 100.0)
                            : 0.0;
                        progress?.Report(new UpdateDownloadProgress("download", bytesReceived, totalBytes, percent));
                    }

                    await fileStream.FlushAsync(cancellation).ConfigureAwait(false);
                }

                actualHashBytes = hasher.GetHashAndReset();
            }

            // 3. Constant-time SHA-256 verification
            progress?.Report(new UpdateDownloadProgress("verify", bytesReceived, totalBytes ?? bytesReceived, 100.0));
            if (!CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes))
                throw new InvalidDataException("SHA-256 checksum mismatch for downloaded update package.");

            // 4. Safe extraction with strict Zip-Slip and zip-bomb guards
            progress?.Report(new UpdateDownloadProgress("extract", bytesReceived, totalBytes ?? bytesReceived, 100.0));
            ExtractZipSafely(zipPath, extractDir, cancellation);

            // 5. Verify extracted Locara.Desktop.exe exists and begins with PE 'MZ' signature
            string extractedExe = Path.Combine(extractDir, DesktopExecutableName);
            VerifyWindowsPeExecutable(extractedExe);

            progress?.Report(new UpdateDownloadProgress("ready", bytesReceived, totalBytes ?? bytesReceived, 100.0));
            committed = true;
            return new StagedAppUpdate(tag, stagingDir, extractDir, expectedHex);
        }
        finally
        {
            if (!committed)
                TryDeleteDirectory(stagingDir);
        }
    }

    public static string BuildSwapScript(
        int currentProcessId,
        string extractedDirectory,
        string installDirectory,
        string executableName = DesktopExecutableName)
    {
        if (currentProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentProcessId), "Process ID must be positive.");

        string safeExtracted = ValidateSafeBatchPath(extractedDirectory, nameof(extractedDirectory));
        string safeInstall = ValidateSafeBatchPath(installDirectory, nameof(installDirectory));
        string safeExe = ValidateSafeExecutableName(executableName, nameof(executableName));

        string? parentDir = Path.GetDirectoryName(safeExtracted);
        string? rootDir = Path.GetPathRoot(safeExtracted);
        bool canCleanParentStage = !string.IsNullOrEmpty(parentDir)
            && !string.Equals(parentDir, rootDir, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parentDir, safeInstall, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(safeExtracted), "extracted", StringComparison.OrdinalIgnoreCase)
            && !ContainsUnsafeBatchCharacters(parentDir);

        string pidText = currentProcessId.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("setlocal\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"set \"PID={pidText}\"\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"set \"SRC={safeExtracted}\"\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"set \"DST={safeInstall}\"\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"set \"EXE={safeExe}\"\r\n");
        sb.Append("set \"BAK=%DST%\\%EXE%.bak\"\r\n");
        if (canCleanParentStage)
            sb.Append(CultureInfo.InvariantCulture, $"set \"STAGE={parentDir}\"\r\n");

        sb.Append("set /a TRIES=0\r\n");
        sb.Append(":wait_loop\r\n");
        sb.Append("tasklist /FI \"PID eq %PID%\" /NH /FO CSV 2>nul | findstr /C:\",\\\"%PID%\\\",\" >nul\r\n");
        sb.Append("if errorlevel 1 goto apply_update\r\n");
        sb.Append("set /a TRIES+=1\r\n");
        sb.Append("if %TRIES% GEQ 60 goto end_fail\r\n");
        sb.Append("ping -n 2 127.0.0.1 >nul\r\n");
        sb.Append("goto wait_loop\r\n");

        sb.Append(":apply_update\r\n");
        sb.Append("if exist \"%DST%\\%EXE%\" (\r\n");
        sb.Append("    copy /Y \"%DST%\\%EXE%\" \"%BAK%\" >nul\r\n");
        sb.Append("    if errorlevel 1 goto end_fail\r\n");
        sb.Append(")\r\n");
        sb.Append("robocopy \"%SRC%\" \"%DST%\" /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NC /NS /NP >nul\r\n");
        sb.Append("if errorlevel 8 goto rollback\r\n");
        sb.Append("if exist \"%BAK%\" del /F /Q \"%BAK%\" >nul 2>&1\r\n");
        sb.Append("start \"\" /D \"%DST%\" \"%DST%\\%EXE%\"\r\n");
        sb.Append("if exist \"%SRC%\" rmdir /S /Q \"%SRC%\" >nul 2>&1\r\n");
        if (canCleanParentStage)
            sb.Append("if exist \"%STAGE%\" rmdir /S /Q \"%STAGE%\" >nul 2>&1\r\n");
        sb.Append("exit /b 0\r\n");

        sb.Append(":rollback\r\n");
        sb.Append("if exist \"%BAK%\" (\r\n");
        sb.Append("    copy /Y \"%BAK%\" \"%DST%\\%EXE%\" >nul\r\n");
        sb.Append("    del /F /Q \"%BAK%\" >nul 2>&1\r\n");
        sb.Append(")\r\n");
        sb.Append(":end_fail\r\n");
        sb.Append("exit /b 1\r\n");

        return sb.ToString();
    }

    public static void LaunchSwapAndRestart(StagedAppUpdate staged, string? installDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(staged);

        SelfUpdateEligibility eligibility = CheckInstallability(installDirectory);
        if (!eligibility.CanInstall)
            throw new InvalidOperationException(eligibility.Reason);

        string stagingDir = ValidateSafeBatchPath(staged.StagingDirectory, nameof(staged));
        string extractedDir = ValidateSafeBatchPath(staged.ExtractedDirectory, nameof(staged));

        Identity.RequireNoLinks(stagingDir);
        Identity.RequireNoLinks(extractedDir);

        if (!Directory.Exists(stagingDir) || !Directory.Exists(extractedDir))
            throw new DirectoryNotFoundException("Staged update directory was not found.");

        VerifyWindowsPeExecutable(Path.Combine(extractedDir, DesktopExecutableName));

        string script = BuildSwapScript(
            Environment.ProcessId,
            extractedDir,
            eligibility.InstallDirectory,
            DesktopExecutableName);

        string scriptPath = Path.Combine(stagingDir, "apply-update.cmd");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = eligibility.InstallDirectory
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(scriptPath);

        using Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Failed to launch update swap helper process.");
    }

    private static async Task<HttpResponseMessage> SendWithTrustedRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        string versionToken,
        CancellationToken cancellation)
    {
        if (!AppUpdateChecker.IsTrustedReleaseAssetUrl(initialUri))
            throw new InvalidDataException("Initial download URI is not a trusted GitHub release asset.");

        Uri currentUri = initialUri;
        for (int hop = 0; hop <= MaxRedirectHops; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Locara", versionToken));

            HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);

            if (IsRedirectStatusCode(response.StatusCode))
            {
                Uri? location = response.Headers.Location;
                response.Dispose();

                if (hop == MaxRedirectHops)
                    throw new InvalidDataException("Too many redirects while downloading update asset.");

                if (location is null)
                    throw new InvalidDataException("Redirect response did not specify a Location header.");

                Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!AppUpdateChecker.IsTrustedDownloadRedirectUrl(nextUri))
                    throw new InvalidDataException("Untrusted redirect target encountered while downloading update.");

                currentUri = nextUri;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                HttpStatusCode code = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Update download failed with HTTP {(int)code} ({code}).");
            }

            return response;
        }

        throw new InvalidDataException("Too many redirects while downloading update asset.");
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadBoundedUtf8Async(Stream stream, long maxBytes, CancellationToken cancellation)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException("Downloaded metadata file exceeds maximum allowed size.");
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static void ExtractZipSafely(string zipPath, string extractDir, CancellationToken cancellation)
    {
        Directory.CreateDirectory(extractDir);
        string normalizedExtractRoot = NormalizeDirectoryPath(extractDir);
        string extractRootWithSep = normalizedExtractRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedExtractRoot
            : normalizedExtractRoot + Path.DirectorySeparatorChar;

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        int entryCount = 0;
        long totalExtractedBytes = 0;
        byte[] buffer = new byte[81920];

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();

            entryCount++;
            if (entryCount > MaxExtractedEntries)
                throw new InvalidDataException("Update archive exceeds maximum allowed entry count.");

            string rawName = entry.FullName;
            if (string.IsNullOrEmpty(rawName)
                || rawName.Contains('\0')
                || rawName.StartsWith('/')
                || rawName.StartsWith('\\')
                || Path.IsPathRooted(rawName))
            {
                throw new InvalidDataException($"Unsafe archive entry path: {rawName}");
            }

            string[] segments = rawName.Split(['/', '\\'], StringSplitOptions.None);
            foreach (string segment in segments)
            {
                if (segment == "..")
                    throw new InvalidDataException($"Archive entry attempts directory traversal: {rawName}");
            }

            // Reject Unix symlink entries if present in ZIP metadata
            int unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
                throw new InvalidDataException($"Archive entry is a symbolic link: {rawName}");

            string relativePath = rawName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string destinationPath = Path.GetFullPath(Path.Combine(normalizedExtractRoot, relativePath));

            bool isDirectoryEntry = rawName.EndsWith('/') || rawName.EndsWith('\\') || string.IsNullOrEmpty(entry.Name);
            if (isDirectoryEntry)
            {
                string trimmedDest = destinationPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!trimmedDest.Equals(normalizedExtractRoot, StringComparison.OrdinalIgnoreCase)
                    && !destinationPath.StartsWith(extractRootWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Archive directory entry escapes target root: {rawName}");
                }

                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (!destinationPath.StartsWith(extractRootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive file entry escapes target root: {rawName}");

            string? parentDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            using Stream entryStream = entry.Open();
            using var outputStream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: false);

            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = entryStream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;

                totalExtractedBytes += read;
                if (totalExtractedBytes > MaxExtractedBytes)
                    throw new InvalidDataException("Extracted update size exceeds maximum allowed limit.");

                outputStream.Write(buffer, 0, read);
            }
        }
    }

    private static void VerifyWindowsPeExecutable(string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new InvalidDataException($"Update package does not contain {DesktopExecutableName}.");

        var info = new FileInfo(executablePath);
        if (info.Length < 2)
            throw new InvalidDataException($"Extracted {DesktopExecutableName} is truncated or invalid.");

        Span<byte> header = stackalloc byte[2];
        using (var stream = File.OpenRead(executablePath))
        {
            if (stream.ReadAtLeast(header, 2, throwOnEndOfStream: false) < 2)
                throw new InvalidDataException($"Unable to read PE header from {DesktopExecutableName}.");
        }

        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException($"Extracted {DesktopExecutableName} does not have a valid PE MZ header.");
    }

    private static bool IsDevBuildDirectory(string fullPath)
    {
        string normalized = fullPath.Replace('/', '\\');
        if (!normalized.EndsWith('\\'))
            normalized += '\\';

        return normalized.Contains("\\bin\\Debug\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanWriteToDirectory(string directory)
    {
        string probePath = Path.Combine(directory, $".locara-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch (Exception cleanupEx) when (cleanupEx is UnauthorizedAccessException or IOException)
            {
                // Best-effort cleanup of write probe file.
            }
            return false;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (root is not null && full.Length > root.Length)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    private static string SanitizeTagForFolder(string tag)
    {
        var sb = new StringBuilder(Math.Min(tag.Length, 32));
        foreach (char c in tag)
        {
            if (sb.Length >= 32) break;
            if (c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '.' or '-' or '_')
                sb.Append(c);
        }

        string cleaned = sb.ToString().Trim('.');
        return cleaned.Length > 0 ? cleaned : "update";
    }

    private static bool ContainsUnsafeBatchCharacters(string value)
    {
        if (value.AsSpan().IndexOfAny(UnsafeBatchCharacters) >= 0)
            return true;

        foreach (char c in value)
        {
            if (char.IsControl(c))
                return true;
        }
        return false;
    }

    private static string ValidateSafeBatchPath(string path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be null or whitespace.", paramName);
        if (ContainsUnsafeBatchCharacters(path))
            throw new ArgumentException("Path contains unsafe command shell characters.", paramName);

        string normalized = NormalizeDirectoryPath(path);
        if (ContainsUnsafeBatchCharacters(normalized))
            throw new ArgumentException("Normalized path contains unsafe command shell characters.", paramName);

        return normalized;
    }

    private static string ValidateSafeExecutableName(string executableName, string paramName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            throw new ArgumentException("Executable name cannot be null or whitespace.", paramName);
        if (ContainsUnsafeBatchCharacters(executableName)
            || executableName.Contains('/')
            || executableName.Contains('\\')
            || executableName.Contains(':')
            || executableName.Contains("..", StringComparison.Ordinal)
            || !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Executable name is invalid or unsafe.", paramName);
        }

        return executableName;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort staging cleanup.
        }
    }
}
