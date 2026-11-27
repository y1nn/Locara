using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class AppSelfUpdaterTests
{
    private static readonly Uri ValidZipAssetUri =
        new("https://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip");

    private static readonly Uri ValidShaAssetUri =
        new("https://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip.sha256");

    private static readonly Uri ValidRedirectUri =
        new("https://objects.githubusercontent.com/github-production-release-asset-2e65be/locara-150?X-Amz-Signature=deadbeef");

    [Fact]
    public void ParseLatestReleaseJsonExtractsTrustedAssetsAndIgnoresUntrustedAssets()
    {
        var current = new SemanticVersion(1, 4, 0);

        // 1. Valid official assets -> CanSelfUpdate = true
        string validJson = """
            {
              "tag_name": "v1.5.0",
              "name": "Locara v1.5.0",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/y1nn/Locara/releases/tag/v1.5.0",
              "published_at": "2026-12-01T12:00:00Z",
              "assets": [
                {
                  "name": "Locara-windows-x64.zip",
                  "size": 12345678,
                  "browser_download_url": "https://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip"
                },
                {
                  "name": "Locara-windows-x64.zip.sha256",
                  "size": 90,
                  "browser_download_url": "https://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip.sha256"
                }
              ]
            }
            """;

        var ok = AppUpdateChecker.ParseLatestReleaseJson(validJson, current);
        Assert.Equal(ReleaseCheckStatus.UpdateAvailable, ok.Status);
        Assert.True(ok.IsUpdateAvailable);
        Assert.True(ok.CanSelfUpdate);
        Assert.Equal(ValidZipAssetUri, ok.PackageAssetUrl);
        Assert.Equal(ValidShaAssetUri, ok.Sha256AssetUrl);
        Assert.Equal(12345678L, ok.PackageAssetSize);

        // 2. Untrusted asset URL does NOT fail release check, but disables CanSelfUpdate
        string spoofedAssetJson = validJson.Replace(
            "\"https://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip\"",
            "\"https://evil.example.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip\"",
            StringComparison.Ordinal);

        var spoofed = AppUpdateChecker.ParseLatestReleaseJson(spoofedAssetJson, current);
        Assert.Equal(ReleaseCheckStatus.UpdateAvailable, spoofed.Status);
        Assert.True(spoofed.IsUpdateAvailable);
        Assert.False(spoofed.CanSelfUpdate);
        Assert.Null(spoofed.PackageAssetUrl);
        Assert.Null(spoofed.PackageAssetSize);
        Assert.Equal(ValidShaAssetUri, spoofed.Sha256AssetUrl);

        // 3. Up-to-date release with valid assets -> CanSelfUpdate = false
        var upToDate = AppUpdateChecker.ParseLatestReleaseJson(validJson, new SemanticVersion(1, 5, 0));
        Assert.Equal(ReleaseCheckStatus.UpToDate, upToDate.Status);
        Assert.False(upToDate.IsUpdateAvailable);
        Assert.False(upToDate.CanSelfUpdate);
    }

    [Theory]
    [InlineData("https://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip", true)]
    [InlineData("https://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip.sha256", true)]
    [InlineData("https://GITHUB.COM/y1nn/Locara/releases/download/v1.5.0/LOCARA-WINDOWS-X64.ZIP", true)]
    [InlineData("http://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip", false)]
    [InlineData("https://evil.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip", false)]
    [InlineData("https://github.com/other/Locara/releases/download/v1.5.0/Locara-windows-x64.zip", false)]
    [InlineData("https://github.com:8443/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip", false)]
    [InlineData("https://user@github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip", false)]
    [InlineData("https://github.com/y1nn/Locara/releases/download/v1.5.0/../v1.4.0/Locara-windows-x64.zip", false)]
    [InlineData("https://github.com/y1nn/Locara/releases/download/v1.5.0/%2e%2e/v1.4.0/Locara-windows-x64.zip", false)]
    [InlineData("https://github.com/y1nn/Locara/releases/download/Locara-windows-x64.zip", false)]
    [InlineData("https://github.com/y1nn/Locara/releases/download/v1.5.0/other.zip", false)]
    public void IsTrustedReleaseAssetUrlValidatesStrictly(string rawUrl, bool expected)
    {
        Assert.True(Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri));
        Assert.Equal(expected, AppUpdateChecker.IsTrustedReleaseAssetUrl(uri));
    }

    [Theory]
    [InlineData("https://github.com/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123?sig=abc", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/456", true)]
    [InlineData("https://github-releases.githubusercontent.com/789/Locara-windows-x64.zip", true)]
    [InlineData("http://objects.githubusercontent.com/github-production-release-asset/123", false)]
    [InlineData("https://evil.com/github-production-release-asset/123", false)]
    [InlineData("https://evil.objects.githubusercontent.com/asset", false)]
    [InlineData("https://objects.githubusercontent.com:8443/asset", false)]
    [InlineData("https://user:pass@objects.githubusercontent.com/asset", false)]
    [InlineData("https://objects.githubusercontent.com/a/../b", false)]
    [InlineData("https://github.com/other/repo/releases/download/v1.5.0/Locara-windows-x64.zip", false)]
    public void IsTrustedDownloadRedirectUrlValidatesStrictly(string rawUrl, bool expected)
    {
        Assert.True(Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri));
        Assert.Equal(expected, AppUpdateChecker.IsTrustedDownloadRedirectUrl(uri));
    }

    [Fact]
    public void IsTrustedUrlHelpersRejectNullAndRelativeUris()
    {
        var relative = new Uri("/y1nn/Locara/releases/download/v1.5.0/Locara-windows-x64.zip", UriKind.Relative);
        Assert.False(AppUpdateChecker.IsTrustedReleaseAssetUrl(null));
        Assert.False(AppUpdateChecker.IsTrustedReleaseAssetUrl(relative));
        Assert.False(AppUpdateChecker.IsTrustedDownloadRedirectUrl(null));
        Assert.False(AppUpdateChecker.IsTrustedDownloadRedirectUrl(relative));
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  Locara-windows-x64.zip\n", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *Locara-windows-x64.zip\r\n", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("\uFEFF0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\tLocara-windows-x64.zip", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void TryParseSha256FileAcceptsValidFormats(string content, string expected)
    {
        Assert.True(AppSelfUpdater.TryParseSha256File(content, out string parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("short  Locara-windows-x64.zip")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz  Locara-windows-x64.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  wrong-file.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  Locara-windows-x64.zip\n0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  extra.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void TryParseSha256FileRejectsInvalidFormats(string content)
    {
        Assert.False(AppSelfUpdater.TryParseSha256File(content, out string parsed));
        Assert.Equal("", parsed);
    }

    [Fact]
    public async Task DownloadAndVerifyAsyncSucceedsAcrossTrustedRedirectAndExtractsPeExecutable()
    {
        using var ws = new TempWorkspace();
        string stagingRoot = ws.PathOf("updates");

        byte[] zipBytes = CreateZipBytes(
            ("Locara.Desktop.exe", [(byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00]),
            ("assets/readme.txt", Encoding.UTF8.GetBytes("Locara v1.5.0")));
        string expectedHex = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
        string shaContent = $"{expectedHex}  {AppUpdateChecker.WindowsPackageAssetName}\r\n";

        var seenUris = new List<Uri>();
        using var client = new HttpClient(new StubHandler((req, _) =>
        {
            seenUris.Add(req.RequestUri!);
            if (req.RequestUri == ValidShaAssetUri)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(shaContent, Encoding.UTF8, "text/plain")
                });
            }

            if (req.RequestUri == ValidZipAssetUri)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = ValidRedirectUri;
                return Task.FromResult(redirect);
            }

            if (req.RequestUri == ValidRedirectUri)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        var stages = new List<string>();
        var progress = new SyncProgress<UpdateDownloadProgress>(p => stages.Add(p.Stage));

        var release = CreateReleaseResult(zipBytes.Length);
        StagedAppUpdate staged = await AppSelfUpdater.DownloadAndVerifyAsync(
            release,
            progress,
            stagingRoot,
            client);

        Assert.Equal("v1.5.0", staged.Tag);
        Assert.Equal(expectedHex, staged.VerifiedSha256);
        Assert.True(Directory.Exists(staged.StagingDirectory));
        Assert.True(Directory.Exists(staged.ExtractedDirectory));
        Assert.True(File.Exists(Path.Combine(staged.ExtractedDirectory, "Locara.Desktop.exe")));
        Assert.Equal("Locara v1.5.0", File.ReadAllText(Path.Combine(staged.ExtractedDirectory, "assets", "readme.txt")));
        Assert.Contains(ValidRedirectUri, seenUris);
        Assert.Contains("sha256", stages);
        Assert.Contains("download", stages);
        Assert.Contains("verify", stages);
        Assert.Contains("extract", stages);
        Assert.Contains("ready", stages);
    }

    [Fact]
    public async Task DownloadAndVerifyAsyncRejectsUntrustedRedirectBeforeDownloading()
    {
        using var ws = new TempWorkspace();
        string stagingRoot = ws.PathOf("updates");

        bool evilHostContacted = false;
        using var client = new HttpClient(new StubHandler((req, _) =>
        {
            if (req.RequestUri!.Host.Equals("evil.example.com", StringComparison.OrdinalIgnoreCase))
            {
                evilHostContacted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (req.RequestUri == ValidShaAssetUri)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://evil.example.com/Locara-windows-x64.zip.sha256");
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppSelfUpdater.DownloadAndVerifyAsync(CreateReleaseResult(1024), stagingRoot: stagingRoot, httpClient: client));

        Assert.False(evilHostContacted);
        Assert.Empty(Directory.GetDirectories(stagingRoot));
    }

    [Fact]
    public async Task DownloadAndVerifyAsyncRejectsExcessiveRedirectsAndUntrustedAssetUrlsBeforeNetworkCall()
    {
        using var ws = new TempWorkspace();
        string stagingRoot = ws.PathOf("updates");

        // 1. Untrusted PackageAssetUrl rejected before any network request
        int calls = 0;
        using (var noCallClient = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        })))
        {
            var badRelease = new ReleaseCheckResult(
                ReleaseCheckStatus.UpdateAvailable,
                new SemanticVersion(1, 4, 0),
                new SemanticVersion(1, 5, 0),
                "v1.5.0",
                "Locara v1.5.0",
                new Uri("https://github.com/y1nn/Locara/releases/tag/v1.5.0"),
                DateTimeOffset.UtcNow,
                new Uri("https://evil.example.com/Locara-windows-x64.zip"),
                ValidShaAssetUri);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                AppSelfUpdater.DownloadAndVerifyAsync(badRelease, stagingRoot: stagingRoot, httpClient: noCallClient));
            Assert.Equal(0, calls);
        }

        // 2. More than 3 redirects -> rejected and staging cleaned up
        int redirectCount = 0;
        using (var loopClient = new HttpClient(new StubHandler((_, _) =>
        {
            redirectCount++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri($"https://objects.githubusercontent.com/hop/{redirectCount}");
            return Task.FromResult(redirect);
        })))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                AppSelfUpdater.DownloadAndVerifyAsync(CreateReleaseResult(1024), stagingRoot: stagingRoot, httpClient: loopClient));
            Assert.Equal(4, redirectCount);
            Assert.Empty(Directory.GetDirectories(stagingRoot));
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAsyncAbortsAndDeletesStagingOnSha256Mismatch()
    {
        using var ws = new TempWorkspace();
        string stagingRoot = ws.PathOf("updates");

        byte[] zipBytes = CreateZipBytes(("Locara.Desktop.exe", [(byte)'M', (byte)'Z', 0x00, 0x01]));
        string wrongShaContent = $"{new string('a', 64)}  {AppUpdateChecker.WindowsPackageAssetName}\n";

        using var client = new HttpClient(new StubHandler((req, _) =>
        {
            if (req.RequestUri == ValidShaAssetUri)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(wrongShaContent, Encoding.UTF8, "text/plain")
                });
            }

            if (req.RequestUri == ValidZipAssetUri)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppSelfUpdater.DownloadAndVerifyAsync(CreateReleaseResult(zipBytes.Length), stagingRoot: stagingRoot, httpClient: client));

        Assert.Empty(Directory.GetDirectories(stagingRoot));
    }

    [Fact]
    public async Task DownloadAndVerifyAsyncRejectsZipSlipAndCleansUpStaging()
    {
        using var ws = new TempWorkspace();
        string stagingRoot = ws.PathOf("updates");

        byte[] zipBytes = CreateZipBytes(
            ("Locara.Desktop.exe", [(byte)'M', (byte)'Z', 0x00, 0x01]),
            ("../escape.txt", Encoding.UTF8.GetBytes("pwned")));
        string shaContent = $"{Convert.ToHexStringLower(SHA256.HashData(zipBytes))}  {AppUpdateChecker.WindowsPackageAssetName}\n";

        using var client = CreateDirectStubClient(shaContent, zipBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppSelfUpdater.DownloadAndVerifyAsync(CreateReleaseResult(zipBytes.Length), stagingRoot: stagingRoot, httpClient: client));

        Assert.False(File.Exists(ws.PathOf("updates", "escape.txt")));
        Assert.Empty(Directory.GetDirectories(stagingRoot));
    }

    [Fact]
    public async Task DownloadAndVerifyAsyncRejectsMissingOrInvalidDesktopExecutable()
    {
        using var ws = new TempWorkspace();
        string stagingRoot = ws.PathOf("updates");

        // 1. Missing Locara.Desktop.exe
        byte[] missingExeZip = CreateZipBytes(("readme.txt", Encoding.UTF8.GetBytes("hello")));
        string missingExeSha = $"{Convert.ToHexStringLower(SHA256.HashData(missingExeZip))}  {AppUpdateChecker.WindowsPackageAssetName}\n";
        using (var client = CreateDirectStubClient(missingExeSha, missingExeZip))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                AppSelfUpdater.DownloadAndVerifyAsync(CreateReleaseResult(missingExeZip.Length), stagingRoot: stagingRoot, httpClient: client));
            Assert.Empty(Directory.GetDirectories(stagingRoot));
        }

        // 2. Non-PE Locara.Desktop.exe (missing MZ header)
        byte[] badHeaderZip = CreateZipBytes(("Locara.Desktop.exe", Encoding.ASCII.GetBytes("NOT_A_PE_BINARY")));
        string badHeaderSha = $"{Convert.ToHexStringLower(SHA256.HashData(badHeaderZip))}  {AppUpdateChecker.WindowsPackageAssetName}\n";
        using (var client = CreateDirectStubClient(badHeaderSha, badHeaderZip))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                AppSelfUpdater.DownloadAndVerifyAsync(CreateReleaseResult(badHeaderZip.Length), stagingRoot: stagingRoot, httpClient: client));
            Assert.Empty(Directory.GetDirectories(stagingRoot));
        }
    }

    [Fact]
    public void CheckInstallabilityRejectsDevPathsAndAcceptsCleanStandaloneDir()
    {
        using var ws = new TempWorkspace();

        // 1. Dev build path bin\Release\net10.0-windows -> rejected
        string devDir = ws.PathOf("src", "Locara.Desktop", "bin", "Release", "net10.0-windows");
        Directory.CreateDirectory(devDir);
        File.WriteAllBytes(Path.Combine(devDir, "Locara.Desktop.exe"), [(byte)'M', (byte)'Z', 0x00, 0x00]);

        SelfUpdateEligibility devEligibility = AppSelfUpdater.CheckInstallability(devDir);
        Assert.False(devEligibility.CanInstall);
        Assert.Contains("bin\\Release", devEligibility.Reason, StringComparison.Ordinal);

        // 2. Clean writable standalone dir with Locara.Desktop.exe -> accepted
        string standaloneDir = ws.PathOf("LocaraApp");
        Directory.CreateDirectory(standaloneDir);
        File.WriteAllBytes(Path.Combine(standaloneDir, "Locara.Desktop.exe"), [(byte)'M', (byte)'Z', 0x00, 0x00]);

        SelfUpdateEligibility okEligibility = AppSelfUpdater.CheckInstallability(standaloneDir);
        Assert.True(okEligibility.CanInstall);
        Assert.Equal(Path.GetFullPath(standaloneDir), okEligibility.InstallDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(standaloneDir), "Locara.Desktop.exe"), okEligibility.ExecutablePath);

        // 3. Missing Locara.Desktop.exe -> rejected
        string emptyDir = ws.PathOf("EmptyApp");
        Directory.CreateDirectory(emptyDir);
        Assert.False(AppSelfUpdater.CheckInstallability(emptyDir).CanInstall);
    }

    [Fact]
    public void BuildSwapScriptRejectsUnsafeInputsAndGeneratesExpectedSwapCommands()
    {
        using var ws = new TempWorkspace();
        string stageDir = ws.PathOf("updates", "v1.5.0-abc");
        string extractedDir = Path.Combine(stageDir, "extracted");
        string installDir = ws.PathOf("Locara");

        string script = AppSelfUpdater.BuildSwapScript(4242, extractedDir, installDir);

        Assert.Contains("set \"PID=4242\"", script, StringComparison.Ordinal);
        Assert.Contains($"set \"SRC={extractedDir}\"", script, StringComparison.Ordinal);
        Assert.Contains($"set \"DST={installDir}\"", script, StringComparison.Ordinal);
        Assert.Contains("set \"EXE=Locara.Desktop.exe\"", script, StringComparison.Ordinal);
        Assert.Contains("set \"BAK=%DST%\\%EXE%.bak\"", script, StringComparison.Ordinal);
        Assert.Contains("copy /Y \"%DST%\\%EXE%\" \"%BAK%\"", script, StringComparison.Ordinal);
        Assert.Contains("robocopy \"%SRC%\" \"%DST%\"", script, StringComparison.Ordinal);
        Assert.Contains(":rollback", script, StringComparison.Ordinal);
        Assert.Contains("copy /Y \"%BAK%\" \"%DST%\\%EXE%\"", script, StringComparison.Ordinal);
        Assert.Contains("start \"\" /D \"%DST%\" \"%DST%\\%EXE%\"", script, StringComparison.Ordinal);
        Assert.Contains($"set \"STAGE={stageDir}\"", script, StringComparison.Ordinal);

        // Reject shell metacharacters / command injection attempts
        string[] unsafePaths =
        [
            ws.PathOf("bad&calc"),
            ws.PathOf("bad|dir"),
            ws.PathOf("bad<in"),
            ws.PathOf("bad>out"),
            ws.PathOf("bad^caret"),
            ws.PathOf("bad%TEMP%"),
            ws.PathOf("bad!bang"),
            ws.PathOf("bad\"quote"),
            ws.PathOf("bad\ncmd"),
            ws.PathOf("bad\rcmd")
        ];

        foreach (string unsafePath in unsafePaths)
        {
            Assert.Throws<ArgumentException>(() => AppSelfUpdater.BuildSwapScript(4242, unsafePath, installDir));
            Assert.Throws<ArgumentException>(() => AppSelfUpdater.BuildSwapScript(4242, extractedDir, unsafePath));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => AppSelfUpdater.BuildSwapScript(0, extractedDir, installDir));
        Assert.Throws<ArgumentException>(() => AppSelfUpdater.BuildSwapScript(4242, extractedDir, installDir, "..\\evil.exe"));
    }

    private static ReleaseCheckResult CreateReleaseResult(long? size = null) =>
        new(
            ReleaseCheckStatus.UpdateAvailable,
            new SemanticVersion(1, 4, 0),
            new SemanticVersion(1, 5, 0),
            "v1.5.0",
            "Locara v1.5.0",
            new Uri("https://github.com/y1nn/Locara/releases/tag/v1.5.0"),
            DateTimeOffset.UtcNow,
            ValidZipAssetUri,
            ValidShaAssetUri,
            size);

    private static HttpClient CreateDirectStubClient(string shaContent, byte[] zipBytes) =>
        new(new StubHandler((req, _) =>
        {
            if (req.RequestUri == ValidShaAssetUri)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(shaContent, Encoding.UTF8, "text/plain")
                });
            }

            if (req.RequestUri == ValidZipAssetUri)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

    private static byte[] CreateZipBytes(params (string EntryName, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(entryName);
                using Stream s = entry.Open();
                s.Write(content, 0, content.Length);
            }
        }
        return ms.ToArray();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
