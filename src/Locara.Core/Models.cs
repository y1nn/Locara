using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace Locara.Core;

public record TextSlot(string Locator, string Source, string Context = "", string? ExistingTarget = null);
public record Resource(string Path, byte[] Bytes);
public record Diagnostic(string Path, string Message);
public record ScanResult(int Files, int Units, IReadOnlyList<Diagnostic> Diagnostics, string Package = "");
public record BuildResult(string Directory, int Files, int Translations);
public record SourcePackage(string Path, string Format, string Status);
public enum GameLaunch { None, Steam, GodotPack, RenpyCopy }
public record SourceCapability(string Engine, string Version, bool CanRead, bool CanBuild, GameLaunch Launch,
    string Deployment, string Arabic, bool CanGenerateTemplates = false);
public record LanguageSource(string Code, string Name, string Package, string Sample)
{
    public string Display => $"{Name} ({Code})  ·  {Sample}";
}
public record PlannedResource(string Path, int Translations);
public record PackagePreview(string Package, bool Final, IReadOnlyList<PlannedResource> Resources, int Blocked,
    IReadOnlyList<string> ChangedFiles)
{
    public string Notice { get; init; } = "";
    internal string Fingerprint { get; init; } = "";
}
public record ExtractionRule(string? Key = null, string? Source = null, string? Target = null, string? Sheet = null, string[]? Fields = null, string[]? FieldPatterns = null);
public record TablePreview(string Sheet, IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows,
    IReadOnlyList<int>? RowsWiderThanHeader = null);
public record ExtractionPreview(IReadOnlyList<TablePreview> Tables, IReadOnlyList<TextSlot> Fields, ExtractionRule? Rule);
public record ProjectInfo(string Name, string Source, string Engine);
public sealed class Unit : INotifyPropertyChanged
{
    private string status = "pending";
    private string warning = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public long Revision { get; internal set; }
    public string ImportedTarget { get; internal set; } = "";
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Locator { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Context { get; set; } = "";
    public string Status
    {
        get => status;
        set
        {
            if (status == value) return;
            status = value;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        }
    }
    public string Warning
    {
        get => warning;
        set
        {
            if (warning == value) return;
            warning = value;
            PropertyChanged?.Invoke(this, new(nameof(Warning)));
        }
    }
}

public interface IFormatHandler
{
    string Name { get; }
    bool Supports(string path);
    IReadOnlyList<TextSlot> Extract(byte[] bytes);
    byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations);
}

public interface IContainerAdapter
{
    string Name { get; }
    bool Supports(string path);
    ResourceIndex Index(string source, string? password, CancellationToken cancellation);
    void WriteIndexed(string destination, IReadOnlyList<ResourceEntry> resources, string? password, CancellationToken cancellation, ResourceIndex? original = null);
    IReadOnlyList<Resource> Read(string source, string? password, CancellationToken cancellation);
    void Write(string destination, IReadOnlyList<Resource> resources, string? password, CancellationToken cancellation);
}

public interface IEngineProfile
{
    string Name { get; }
    bool Detect(IReadOnlyList<string> paths);
    bool Include(string path, TextSlot slot);
    string Deployment { get; }
}

public static class Identity
{
    private static readonly HashSet<string> ReservedNames = new(
        new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }
        .Concat("123456789¹²³".SelectMany(digit => new[] { "COM" + digit, "LPT" + digit })),
        StringComparer.OrdinalIgnoreCase);

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string UnitId(string path, string locator) => Hash(Encoding.UTF8.GetBytes(path + "\n" + locator));
    public static string SafePath(string path)
    {
        path = path.Replace('\\', '/');
        if (path.StartsWith("res://", StringComparison.Ordinal)) path = path[6..];
        if (path.Split('/').Any(segment => segment.Length == 0
            || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character))
            || ReservedNames.Contains(segment.Split('.')[0].TrimEnd(' '))))
            throw new InvalidDataException("Unsafe resource path: " + path);
        return path;
    }

    internal static bool Within(string candidate, string root)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static void RequireNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Choose a path outside filesystem links: " + current);
        }
    }
}

public static class LocaraPaths
{
    public static string UserDataDirectory
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string primary = Path.Combine(localAppData, "Locara");
            if (Directory.Exists(primary)) return primary;
            string legacy = Path.Combine(localAppData, "Locara2");
            return Directory.Exists(legacy) ? legacy : primary;
        }
    }
}
