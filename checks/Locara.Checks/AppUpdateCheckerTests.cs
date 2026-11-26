using System.Net;
using System.Text;
using Locara.Core;
using Xunit;

namespace Locara.Checks;

public sealed class AppUpdateCheckerTests
{
    [Theory]
    [InlineData("v1.3.0", 1, 3, 0, "")]
    [InlineData("1.4.0", 1, 4, 0, "")]
    [InlineData("V2.10.5", 2, 10, 5, "")]
    [InlineData("1.4", 1, 4, 0, "")]
    [InlineData("1.4.0.0", 1, 4, 0, "")]
    [InlineData("v1.4.0+abcdef1234", 1, 4, 0, "")]
    [InlineData("v1.4.0-beta.2+sha.99", 1, 4, 0, "beta.2")]
    [InlineData("1.5.0-rc.1", 1, 5, 0, "rc.1")]
    public void SemanticVersionParsesValidReleaseTags(string raw, int major, int minor, int patch, string pre)
    {
        Assert.True(SemanticVersion.TryParse(raw, out var parsed));
        Assert.Equal(major, parsed.Major);
        Assert.Equal(minor, parsed.Minor);
        Assert.Equal(patch, parsed.Patch);
        Assert.Equal(pre, parsed.NormalizedPreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("-1.0.0")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-beta$1")]
    [InlineData("9999999999.0.0")]
    [InlineData("1.2.3.4.5")]
    public void SemanticVersionRejectsMalformedInputs(string? raw)
    {
        Assert.False(SemanticVersion.TryParse(raw, out _));
    }

    [Fact]
    public void SemanticVersionComparesStableAndPreReleaseAccordingToSemVer()
    {
        string[] ordered =
        [
            "v1.3.0",
            "v1.4.0-alpha.1",
            "v1.4.0-alpha.beta",
            "v1.4.0-beta.1",
            "v1.4.0-beta.2",
            "v1.4.0-beta.11",
            "v1.4.0-rc.1",
            "v1.4.0",
            "v1.4.1",
            "v1.10.0",
            "v2.0.0"
        ];

        var versions = ordered.Select(s =>
        {
            Assert.True(SemanticVersion.TryParse(s, out var v));
            return v;
        }).ToArray();

        for (int i = 0; i < versions.Length; i++)
        {
            Assert.Equal(0, versions[i].CompareTo(versions[i]));
            for (int j = i + 1; j < versions.Length; j++)
            {
                Assert.True(versions[i] < versions[j], $"Expected {versions[i]} < {versions[j]}");
                Assert.True(versions[j] > versions[i], $"Expected {versions[j]} > {versions[i]}");
            }
        }
    }

    [Fact]
    public void ParseLatestReleaseJsonDetectsUpdateUpToDateAndRejectsUnsafeResponses()
    {
        var current = new SemanticVersion(1, 4, 0);

        // 1. Newer official release -> UpdateAvailable
        string newerJson = """
            {
              "tag_name": "v1.5.0",
              "name": "Locara v1.5.0",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/y1nn/Locara/releases/tag/v1.5.0",
              "published_at": "2026-12-01T12:00:00Z"
            }
            """;
        var newer = AppUpdateChecker.ParseLatestReleaseJson(newerJson, current);
        Assert.Equal(ReleaseCheckStatus.UpdateAvailable, newer.Status);
        Assert.True(newer.IsUpdateAvailable);
        Assert.Equal(new SemanticVersion(1, 5, 0), newer.LatestVersion);
        Assert.Equal("v1.5.0", newer.LatestTag);
        Assert.Equal("Locara v1.5.0", newer.ReleaseName);
        Assert.Equal(new Uri("https://github.com/y1nn/Locara/releases/tag/v1.5.0"), newer.ReleaseUrl);
        Assert.NotNull(newer.PublishedAt);

        // 2. Same or older official release -> UpToDate
        string sameJson = """
            {
              "tag_name": "v1.4.0",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/y1nn/Locara/releases/tag/v1.4.0"
            }
            """;
        var same = AppUpdateChecker.ParseLatestReleaseJson(sameJson, current);
        Assert.Equal(ReleaseCheckStatus.UpToDate, same.Status);
        Assert.False(same.IsUpdateAvailable);

        string olderJson = sameJson.Replace("v1.4.0", "v1.3.0", StringComparison.Ordinal);
        var older = AppUpdateChecker.ParseLatestReleaseJson(olderJson, current);
        Assert.Equal(ReleaseCheckStatus.UpToDate, older.Status);
        Assert.False(older.IsUpdateAvailable);

        // 3. Draft or prerelease -> Unavailable
        Assert.Equal(ReleaseCheckStatus.Unavailable,
            AppUpdateChecker.ParseLatestReleaseJson(newerJson.Replace("\"draft\": false", "\"draft\": true", StringComparison.Ordinal), current).Status);
        Assert.Equal(ReleaseCheckStatus.Unavailable,
            AppUpdateChecker.ParseLatestReleaseJson(newerJson.Replace("\"prerelease\": false", "\"prerelease\": true", StringComparison.Ordinal), current).Status);

        // 4. Untrusted or spoofed html_url -> Unavailable
        string[] untrustedUrls =
        [
            "http://github.com/y1nn/Locara/releases/tag/v1.5.0",
            "https://evil.example.com/y1nn/Locara/releases/tag/v1.5.0",
            "https://github.com/otheruser/Locara/releases/tag/v1.5.0",
            "https://github.com/y1nn/Locara/releases/../../other/repo",
            "https://user@github.com/y1nn/Locara/releases/tag/v1.5.0",
            "https://github.com:8443/y1nn/Locara/releases/tag/v1.5.0",
            "javascript:alert(1)"
        ];
        foreach (string badUrl in untrustedUrls)
        {
            string badJson = newerJson.Replace("https://github.com/y1nn/Locara/releases/tag/v1.5.0", badUrl, StringComparison.Ordinal);
            var res = AppUpdateChecker.ParseLatestReleaseJson(badJson, current);
            Assert.Equal(ReleaseCheckStatus.Unavailable, res.Status);
            Assert.False(res.IsUpdateAvailable);
        }

        // 5. Malformed JSON, non-object JSON, missing tag, oversized JSON -> Unavailable without throwing
        Assert.Equal(ReleaseCheckStatus.Unavailable, AppUpdateChecker.ParseLatestReleaseJson("{bad json", current).Status);
        Assert.Equal(ReleaseCheckStatus.Unavailable, AppUpdateChecker.ParseLatestReleaseJson("[]", current).Status);
        Assert.Equal(ReleaseCheckStatus.Unavailable, AppUpdateChecker.ParseLatestReleaseJson("""{"html_url":"https://github.com/y1nn/Locara/releases/tag/v1.5.0"}""", current).Status);
        Assert.Equal(ReleaseCheckStatus.Unavailable, AppUpdateChecker.ParseLatestReleaseJson(new string(' ', 300_000), current).Status);
    }

    [Fact]
    public async Task CheckForUpdatesAsyncSendsPrivacySafeHeadersAndFailsQuietlyOnErrorsAndTimeout()
    {
        var current = new SemanticVersion(1, 4, 0);
        HttpRequestMessage? captured = null;

        // 1. Success path: verify exact GitHub API URL, minimal User-Agent, no tracking/auth headers
        using (var okClient = new HttpClient(new StubHandler((req, _) =>
        {
            captured = req;
            const string body = """
                {
                  "tag_name": "v1.5.0",
                  "name": "Locara v1.5.0",
                  "draft": false,
                  "prerelease": false,
                  "html_url": "https://github.com/y1nn/Locara/releases/tag/v1.5.0"
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        })))
        {
            var res = await AppUpdateChecker.CheckForUpdatesAsync(current, okClient, TimeSpan.FromSeconds(2));
            Assert.True(res.IsUpdateAvailable);
            Assert.Equal(new SemanticVersion(1, 5, 0), res.LatestVersion);
            Assert.NotNull(captured);
            Assert.Equal(AppUpdateChecker.LatestReleaseApiUri, captured!.RequestUri);
            Assert.Equal("Locara/1.4.0", captured.Headers.UserAgent.ToString());
            Assert.Null(captured.Headers.Authorization);
            Assert.False(captured.Headers.Contains("Cookie"));
        }

        // 2. Rate-limit (403) / server error (500) -> quiet Unavailable
        using (var rateLimitClient = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
        {
            var res = await AppUpdateChecker.CheckForUpdatesAsync(current, rateLimitClient, TimeSpan.FromSeconds(2));
            Assert.Equal(ReleaseCheckStatus.Unavailable, res.Status);
        }

        // 3. Offline / network exception -> quiet Unavailable
        using (var offlineClient = new HttpClient(new StubHandler((_, _) =>
            throw new HttpRequestException("Offline"))))
        {
            var res = await AppUpdateChecker.CheckForUpdatesAsync(current, offlineClient, TimeSpan.FromSeconds(2));
            Assert.Equal(ReleaseCheckStatus.Unavailable, res.Status);
        }

        // 4. Slow server exceeding short timeout -> cancels promptly and returns Unavailable
        using (var slowClient = new HttpClient(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })))
        {
            var res = await AppUpdateChecker.CheckForUpdatesAsync(current, slowClient, TimeSpan.FromMilliseconds(60));
            Assert.Equal(ReleaseCheckStatus.Unavailable, res.Status);
        }

        // 5. Oversized payload (>256 KiB) -> rejected safely
        using (var hugeClient = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('a', 300_000), Encoding.UTF8, "application/json")
            }))))
        {
            var res = await AppUpdateChecker.CheckForUpdatesAsync(current, hugeClient, TimeSpan.FromSeconds(2));
            Assert.Equal(ReleaseCheckStatus.Unavailable, res.Status);
        }
    }

    [Fact]
    public void AutoCheckPreferencePersistsAndDefaultsToEnabled()
    {
        using var ws = new TempWorkspace();
        string prefPath = ws.PathOf("update-check.txt");

        Assert.True(AppUpdateChecker.ReadAutoCheckEnabled(prefPath));

        AppUpdateChecker.WriteAutoCheckEnabled(false, prefPath);
        Assert.False(AppUpdateChecker.ReadAutoCheckEnabled(prefPath));

        AppUpdateChecker.WriteAutoCheckEnabled(true, prefPath);
        Assert.True(AppUpdateChecker.ReadAutoCheckEnabled(prefPath));

        var current = AppUpdateChecker.GetCurrentVersion(typeof(AppUpdateChecker).Assembly);
        Assert.True(current >= new SemanticVersion(1, 4, 0));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
