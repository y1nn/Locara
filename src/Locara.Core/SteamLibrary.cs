using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Locara.Core;

public sealed record SteamGame(string AppId, string Name, string Directory)
{
    public string Display => Name + "  ·  " + Directory;
}

public static class SteamLibrary
{
    private static readonly Regex Key = new(
        """^\s*"(?<key>[^"]+)"\s+"(?<value>(?:\\.|[^"\\])*)"\s*$""", RegexOptions.CultureInvariant);

    public static IReadOnlyList<SteamGame> Discover(CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return settings?.GetValue("SteamPath") is string root ? Discover(root, cancellation) : [];
    }

    public static IReadOnlyList<SteamGame> Discover(string steamRoot, CancellationToken cancellation = default)
    {
        string configuration = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        var paths = ReadKeys(configuration).Where(pair => pair.Key == "path").Select(pair => pair.Value)
            .Prepend(steamRoot).Where(Path.IsPathFullyQualified)
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(StringComparer.OrdinalIgnoreCase);
        var games = new List<SteamGame>();
        foreach (string library in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            string apps = Path.Combine(library, "steamapps");
            foreach (string manifest in Manifests(apps))
            {
                cancellation.ThrowIfCancellationRequested();
                var fields = ReadKeys(manifest).ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                string? id = fields["appid"].FirstOrDefault();
                string? folder = fields["installdir"].FirstOrDefault();
                if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit)
                    || !Path.GetFileName(manifest).Equals($"appmanifest_{id}.acf", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(folder) || folder is "." or ".."
                    || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                string directory = Path.Combine(apps, "common", folder);
                if (!Directory.Exists(directory)) continue;
                games.Add(new(id, fields["name"].FirstOrDefault() is { Length: > 0 } name ? name : folder, directory));
            }
        }
        return games.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(game => game.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] Manifests(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetFiles(directory, "appmanifest_*.acf") : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<(string Key, string Value)> ReadKeys(string path)
    {
        string[] lines;
        try { lines = File.Exists(path) ? File.ReadAllLines(path) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { lines = []; }
        foreach (string line in lines)
        {
            var match = Key.Match(line);
            if (match.Success)
                yield return (match.Groups["key"].Value,
                    match.Groups["value"].Value.Replace(@"\\", @"\", StringComparison.Ordinal));
        }
    }
}
