using System.Text;
using System.Xml.Linq;

namespace Locara.Core;

internal sealed class StarTruckerCatalog : IFormatHandler
{
    internal const string LanguageDirectory = "Star Trucker_Data/StreamingAssets/XML/Strings";
    internal const string LanguageArchive = LanguageDirectory + "/en.xml";
    internal const string MenuContext = "قوائم Star Trucker";
    internal static readonly StarTruckerCatalog Instance = new();

    private static readonly HashSet<string> MenuKeys = new(StringComparer.Ordinal)
    {
        "STR_SUBTITLE", "STR_LOADING", "STR_SAVING", "STR_PLEASEWAIT", "STR_PRESSTOSTART", "STR_SELECT",
        "STR_BACK", "STR_RESUMEGAME", "STR_EXITGAME", "STR_OK", "STR_YES", "STR_NO", "STR_CONTINUE",
        "STR_CONFIRM", "STR_CANCEL", "STR_NEWGAME", "STR_CONTINUEGAME", "STR_EXITTOMAINMENU",
        "STR_POPUP_QUITGAME_HEADER", "STR_POPUP_QUITGAME"
    };

    private sealed record Entry(XElement Element, string Locator, string Context, string LegacyLocator);

    public string Name => "star-trucker-strings";
    public bool Supports(string path) => path.Equals("strings.xml", StringComparison.OrdinalIgnoreCase);

    internal static bool IsGameArchive(string path) =>
        Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)
        && (Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "").EndsWith("/" + LanguageDirectory, StringComparison.OrdinalIgnoreCase);

    internal static bool IsMenuContext(string context) =>
        context.StartsWith(MenuContext + " · ", StringComparison.Ordinal);

    private static IReadOnlyList<Entry> Entries(XDocument catalog)
    {
        if (catalog.Root is not { } root || root.Name != "data")
            throw new InvalidDataException("بنية كتالوج Star Trucker غير معروفة.");
        var elements = root.Elements("string").ToList();
        var counts = elements.ToLookup(element => (string?)element.Attribute("id") ?? "", StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new List<Entry>(elements.Count);
        for (int index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            string key = (string?)element.Attribute("id") ?? "";
            if (element.HasElements || string.IsNullOrWhiteSpace(element.Value)) continue;
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("يوجد نص بلا معرّف في كتالوج Star Trucker.");

            bool duplicate = counts[key].Count() > 1;
            string locator = "id:" + Uri.EscapeDataString(key);
            string context = MenuKeys.Contains(key) ? MenuContext + " · " + key : key;
            if (duplicate)
            {
                int occurrence = occurrences.GetValueOrDefault(key) + 1;
                occurrences[key] = occurrence;
                locator = "duplicate:" + Uri.EscapeDataString(key) + ":" + occurrence;
                context += " · معرّف مكرر " + occurrence;
            }
            entries.Add(new(element, locator, context, $"data[0]/string[{index}]"));
        }
        return entries;
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes) => Entries(XmlFormat.Read(bytes))
        .Select(entry => new TextSlot(entry.Locator, entry.Element.Value, entry.Context)).ToList();

    internal static Dictionary<string, string> LegacyLocators(byte[] bytes) => Entries(XmlFormat.Read(bytes))
        .ToDictionary(entry => entry.Locator, entry => entry.LegacyLocator, StringComparer.Ordinal);

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        var catalog = XmlFormat.Read(bytes);
        var entries = Entries(catalog).ToDictionary(entry => entry.Locator, StringComparer.Ordinal);
        foreach (var (locator, target) in translations)
        {
            if (!entries.TryGetValue(locator, out var entry))
                throw new InvalidDataException("لم يعد موضع نص Star Trucker موجوداً. أعد فحص المصدر.");
            entry.Element.Value = target;
        }
        string contents = catalog.ToString(SaveOptions.DisableFormatting);
        if (catalog.Declaration is { } declaration)
            contents = new XDeclaration(declaration.Version, "UTF-8", declaration.Standalone) + "\n" + contents;
        return Encoding.UTF8.GetBytes(contents);
    }
}
