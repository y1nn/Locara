using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Locara.Core;

public enum ReleaseCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Unavailable
}

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string PreRelease = "") : IComparable<SemanticVersion>
{
    public string NormalizedPreRelease => PreRelease ?? "";

    public string ToTagString() =>
        NormalizedPreRelease.Length == 0
            ? $"v{Major}.{Minor}.{Patch}"
            : $"v{Major}.{Minor}.{Patch}-{NormalizedPreRelease}";

    public override string ToString() => ToTagString();

    public static bool TryParse(string? raw, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim();
        if (s[0] is 'v' or 'V')
            s = s[1..];
        if (s.Length == 0) return false;

        // Strip SemVer build metadata (+gitsha, +build.1)
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        if (s.Length == 0) return false;

        string preRelease = "";
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = s[(dash + 1)..];
            s = s[..dash];
            if (preRelease.Length == 0 || !IsValidPreRelease(preRelease))
                return false;
        }

        string[] parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;

        if (!TryParseNonNegativeAsciiInt(parts[0], out int major)
            || !TryParseNonNegativeAsciiInt(parts[1], out int minor))
            return false;

        int patch = 0;
        if (parts.Length >= 3 && !TryParseNonNegativeAsciiInt(parts[2], out patch))
            return false;

        if (parts.Length == 4 && !TryParseNonNegativeAsciiInt(parts[3], out _))
            return false;

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        int cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        string a = NormalizedPreRelease;
        string b = other.NormalizedPreRelease;
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;  // Stable release outranks pre-release
        if (b.Length == 0) return -1;

        string[] aIds = a.Split('.');
        string[] bIds = b.Split('.');
        int min = Math.Min(aIds.Length, bIds.Length);
        for (int i = 0; i < min; i++)
        {
            bool aNum = TryParseNonNegativeAsciiInt(aIds[i], out int aVal);
            bool bNum = TryParseNonNegativeAsciiInt(bIds[i], out int bVal);
            if (aNum && bNum)
            {
                cmp = aVal.CompareTo(bVal);
                if (cmp != 0) return cmp;
            }
            else if (aNum != bNum)
            {
                // Per SemVer 2.0.0 §11.4.3: numeric identifiers always have lower precedence than non-numeric.
                return aNum ? -1 : 1;
            }
            else
            {
                cmp = string.CompareOrdinal(aIds[i], bIds[i]);
                if (cmp != 0) return cmp;
            }
        }
        return aIds.Length.CompareTo(bIds.Length);
    }

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    private static bool TryParseNonNegativeAsciiInt(string token, out int value)
    {
        value = 0;
        if (token.Length == 0 || token.Length > 9) return false;
        foreach (char c in token)
        {
            if (c is < '0' or > '9') return false;
        }
        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidPreRelease(string value)
    {
        string[] segments = value.Split('.');
        foreach (string seg in segments)
        {
            if (seg.Length == 0) return false;
            foreach (char c in seg)
            {
                bool ok = c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-';
                if (!ok) return false;
            }
        }
        return true;
    }
}

public sealed record ReleaseCheckResult(
    ReleaseCheckStatus Status,
    SemanticVersion CurrentVersion,
    SemanticVersion? LatestVersion = null,
    string? LatestTag = null,
    string? ReleaseName = null,
    Uri? ReleaseUrl = null,
    DateTimeOffset? PublishedAt = null)
{
    public bool IsUpdateAvailable =>
        Status == ReleaseCheckStatus.UpdateAvailable
        && LatestVersion is not null
        && ReleaseUrl is not null;
}

/// <summary>
/// Quiet, privacy-preserving release checker that queries the official GitHub Releases API
/// (<c>y1nn/Locara</c>) with zero telemetry, zero tracking identifiers, and strict URL validation.
/// </summary>
public static class AppUpdateChecker
{
    public const string RepositoryOwner = "y1nn";
    public const string RepositoryName = "Locara";
    private const int MaxResponseBytes = 256 * 1024;
    private const string TrustedReleasePathPrefix = $"/y1nn/Locara/releases/";

    public static readonly Uri LatestReleaseApiUri =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    public static readonly Uri OfficialReleasesPageUri =
        new($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases");

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static readonly Lazy<HttpClient> SharedHttpClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    });

    public static string DefaultPreferencePath =>
        Path.Combine(LocaraPaths.UserDataDirectory, "update-check.txt");

    public static bool ReadAutoCheckEnabled(string? preferencePath = null)
    {
        string path = preferencePath ?? DefaultPreferencePath;
        try
        {
            if (!File.Exists(path)) return true;
            string raw = File.ReadAllText(path).Trim();
            if (raw.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw == "0")
                return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static void WriteAutoCheckEnabled(bool enabled, string? preferencePath = null)
    {
        string path = preferencePath ?? DefaultPreferencePath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, enabled ? "enabled" : "disabled", Encoding.UTF8);
    }

    public static SemanticVersion GetCurrentVersion(Assembly? assembly = null)
    {
        Assembly target = assembly ?? Assembly.GetEntryAssembly() ?? typeof(AppUpdateChecker).Assembly;
        string? infoVer = target.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemanticVersion.TryParse(infoVer, out var parsed))
            return parsed;

        Version? asmVer = target.GetName().Version;
        if (asmVer != null)
            return new SemanticVersion(Math.Max(0, asmVer.Major), Math.Max(0, asmVer.Minor), Math.Max(0, asmVer.Build));

        return new SemanticVersion(1, 0, 0);
    }

    public static bool IsTrustedReleaseUrl(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        string path = uri.AbsolutePath;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        return path.StartsWith(TrustedReleasePathPrefix, StringComparison.OrdinalIgnoreCase)
            || path.Equals("/y1nn/Locara/releases", StringComparison.OrdinalIgnoreCase);
    }

    public static ReleaseCheckResult ParseLatestReleaseJson(string json, SemanticVersion currentVersion)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseBytes)
            return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);

            if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);

            if (root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True)
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);

            if (!root.TryGetProperty("tag_name", out var tagProp) || tagProp.ValueKind != JsonValueKind.String)
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);

            string rawTag = (tagProp.GetString() ?? "").Trim();
            if (!SemanticVersion.TryParse(rawTag, out var latestVersion))
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);

            if (!root.TryGetProperty("html_url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);

            string rawUrl = (urlProp.GetString() ?? "").Trim();
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var releaseUrl) || !IsTrustedReleaseUrl(releaseUrl))
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);

            string? releaseName = null;
            if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                string trimmedName = (nameProp.GetString() ?? "").Trim();
                if (trimmedName.Length > 0)
                    releaseName = trimmedName.Length <= 120 ? trimmedName : trimmedName[..120];
            }

            DateTimeOffset? publishedAt = null;
            if (root.TryGetProperty("published_at", out var pubProp)
                && pubProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(pubProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                publishedAt = dto;
            }

            bool isNewer = latestVersion > currentVersion;
            return new ReleaseCheckResult(
                isNewer ? ReleaseCheckStatus.UpdateAvailable : ReleaseCheckStatus.UpToDate,
                currentVersion,
                latestVersion,
                rawTag,
                releaseName,
                releaseUrl,
                publishedAt);
        }
        catch (JsonException)
        {
            return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, currentVersion);
        }
    }

    public static async Task<ReleaseCheckResult> CheckForUpdatesAsync(
        SemanticVersion? currentVersion = null,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        CancellationToken cancellation = default)
    {
        SemanticVersion current = currentVersion ?? GetCurrentVersion();
        HttpClient client = httpClient ?? SharedHttpClient.Value;
        TimeSpan effectiveTimeout = timeout is { TotalMilliseconds: > 0 } ? timeout.Value : DefaultTimeout;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(effectiveTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Locara", $"{current.Major}.{current.Minor}.{current.Patch}"));

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, current);

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > MaxResponseBytes)
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, current);

            await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            string? json = await ReadBoundedUtf8Async(stream, MaxResponseBytes, cts.Token).ConfigureAwait(false);
            if (json == null)
                return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, current);

            return ParseLatestReleaseJson(json, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return new ReleaseCheckResult(ReleaseCheckStatus.Unavailable, current);
        }
    }

    private static async Task<string?> ReadBoundedUtf8Async(Stream stream, int maxBytes, CancellationToken cancellation)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
