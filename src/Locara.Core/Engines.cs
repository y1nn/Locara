using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Locara.Core;

public sealed class EngineProfile(string name,Func<IReadOnlyList<string>,bool> detect,Func<string,TextSlot,bool> include,string deployment):IEngineProfile
{
    public string Name=>name;
    public bool Detect(IReadOnlyList<string> resources)=>detect(resources);
    public bool Include(string path,TextSlot slot)=>include(path,slot);
    public string Deployment=>deployment;
}
public static class Engines
{
    private static readonly Regex MapFile = new(@"^Map[0-9]+\.json$", RegexOptions.IgnoreCase);
    private static readonly Regex DataPath = new(@"(?:^|/)data/[^/]+\.json$", RegexOptions.IgnoreCase);
    private static readonly Regex UnityDataMarker = new(
        @"(?:^|/)[^/]+_Data/(?:globalgamemanagers|data\.unity3d|resources\.assets|sharedassets0\.assets|RuntimeInitializeOnLoads\.json|ScriptingAssemblies\.json|StreamingAssets/aa/(?:.+/)?[^/]+\.(?:bin|json|bundle))$",
        RegexOptions.IgnoreCase);
    private static readonly Regex UnrealPakMarker = new(
        @"(?:^|/)Content/Paks/[^/]+\.pak$",
        RegexOptions.IgnoreCase);
    private static readonly Regex GameMakerMarker = new(
        @"(?:^|/)(?:data\.win|game\.unx|game\.ios)$",
        RegexOptions.IgnoreCase);
    private static readonly Regex EngineInternalMetadata = new(
        @"(?:^|/)[^/]+_Data/(?:RuntimeInitializeOnLoads\.json|ScriptingAssemblies\.json|boot\.config|app\.info|StreamingAssets/aa/(?:settings|catalog)\.json|StreamingAssets/aa/AddressablesLink/link\.xml|(?:il2cpp_data|Managed|Plugins)/.+)$"
        + @"|(?:^|/)[^/]+_BackUpThisFolder_ButDontShipItWithYourGame/.+"
        + @"|(?:^|/)[^/]*(?:_monoscripts_|localization-assets-shared|localization-locales|unitybuiltinshaders|duplicateassetisolation)[^/]*\.bundle$"
        + @"|(?:^|/)(?:MonoBleedingEdge|Mono/etc|_CommonRedist|Redist|DirectX|vcredist|EasyAntiCheat|BattlEye|CrashHandler|steam_settings)/.+|"
        + @"(?:^|/)UnityCrashHandler[^/]*$"
        + @"|(?:^|/)(?:Locara_Arabic_P|[^/]+_Locara_P)\.pak$"
        + @"|(?:^|/)(?:Engine/Config/[^/]+\.ini|[^/]+/Config/Default[^/]+\.ini)$"
        + @"|\.(?:deps|runtimeconfig)\.json$"
        + @"|\.(?:exe|dll)\.config$"
        + @"|(?:^|/)(?:package-lock\.json|manifest\.webmanifest|app\.manifest|dxvk\.conf|steam_appid\.txt)$",
        RegexOptions.IgnoreCase);

    public static bool IsEngineInternalMetadata(string relativePath) =>
        EngineInternalMetadata.IsMatch(relativePath.Replace('\\', '/'));

    public static IReadOnlyList<IEngineProfile> All {get;} = [
        new EngineProfile("godot",r=>r.Any(x=>x is "project.godot" or "project.binary"),(_,_)=>true,"لعبة Godot — يدعم Locara حزم PCK (الإصدار 2 وGodot 4.5+ الإصدار 3) وملفات .translation الثنائية والملفات النصية مع إعادة البناء والتشغيل عبر المحرك المطابق."),
        new EngineProfile("rpgmaker",r=>r.Any(x=>DataPath.IsMatch(x) && IsSystem(x)),RpgText,"Use the rebuilt data folder in a copy of the matching MV/MZ game and run Playtest."),
        new EngineProfile("renpy",r=>r.Any(x=>Formats.For(x) is RenpyFormat
            || x.StartsWith("game/", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(x).ToLowerInvariant() is ".rpyc" or ".rpa"),
            (_,_)=>true,"Generate Arabic translation templates in the matching Ren'Py SDK, edit game/tl/arabic, and copy the returned templates back to that language directory."),
        new EngineProfile("unity",r=>r.Any(x=>UnityDataMarker.IsMatch(x)),(_,_)=>true,"لعبة Unity — يدعم Locara حزم Addressables (.bundle) وI2 Localization وTextAsset داخل resources/sharedassets/data.unity3d مع حقن خط TextMeshPro العربي تلقائياً وخيار تصدير XUnity.AutoTranslator."),
        new EngineProfile("unreal",r=>r.Any(x=>UnrealPakMarker.IsMatch(x)),(_,_)=>true,"لعبة Unreal Engine 4/5 — يقرأ Locara نصوص .locres وجداول StringTable (.uasset) مباشرة من حزم .pak أو المجلدات ويولّد حزمة تعديل جاهزة (*_Locara_P.pak) أو يحدّث حزم UE5 IoStore بالتدفق المباشر مع حقن الخط العربي الثنائي."),
        new EngineProfile("gamemaker",r=>r.Any(x=>GameMakerMarker.IsMatch(x)),(_,_)=>true,"لعبة GameMaker — يدعم Locara ملفات الترجمة الخارجية المصاحبة (JSON/CSV/INI/PO/TOML/Lua) مع التثبيت الموثّق وتجربة Steam."),
        new EngineProfile("generic",_=>true,(_,_)=>true,"Inspect the output manifest, then integrate resources using the game's documented language loading path.")
    ];
    public static IEngineProfile Resolve(string name, IReadOnlyList<string> resources, string source = "") =>
        name == "auto"
            ? All.First(profile => profile.Detect(resources) || (profile.Name == "rpgmaker"
                && RpgDataAtRoot(source) && resources.Any(path => !path.Contains('/') && (IsSystem(path) || MapFile.IsMatch(path)))))
            : All.FirstOrDefault(profile => profile.Name == name) ?? throw new ArgumentException("Unknown engine profile.");

    internal static bool RpgDataAtRoot(string source) => (File.Exists(source) && !Containers.IsArchive(source))
        || (Directory.Exists(source)
            && Path.GetFileName(Path.TrimEndingDirectorySeparator(source)).Equals("data", StringComparison.OrdinalIgnoreCase));

    private static bool IsSystem(string path) => Path.GetFileName(path).Equals("System.json", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<TextSlot> Extract(IEngineProfile profile, Resource resource, IFormatHandler format,
        bool rpgDataAtRoot = false)
    {
        if (profile.Name != "rpgmaker")
            return format.Extract(resource.Bytes).Where(slot => profile.Include(resource.Path, slot)).ToList();
        if (format is not JsonFormat || (!DataPath.IsMatch(resource.Path)
            && !(rpgDataAtRoot && !resource.Path.Contains('/'))))
            return [];
        var root = JsonNode.Parse(resource.Bytes);
        var result = new List<TextSlot>();
        foreach (var slot in format.Extract(resource.Bytes))
        {
            int parameters = slot.Locator.LastIndexOf("/parameters/", StringComparison.Ordinal);
            if (parameters < 0)
            {
                if (RpgText(resource.Path, slot)) result.Add(slot);
                continue;
            }

            var command = root;
            foreach (var part in slot.Locator[1..parameters].Split('/'))
                command = command is JsonArray array ? array[int.Parse(part)] : (command as JsonObject)?[part.Replace("~1", "/").Replace("~0", "~")];
            if ((command as JsonObject)?["code"] is not JsonValue code || !code.TryGetValue<int>(out int value))
                continue;
            string argument = slot.Locator[(parameters + "/parameters/".Length)..];
            bool translatable = value switch
            {
                401 or 405 => argument == "0",
                102 => Regex.IsMatch(argument, @"^0/\d+$"),
                402 => argument == "1",
                _ => false
            };
            if (translatable) result.Add(slot with { Context = "Event " + slot.Locator[..parameters] });
        }
        return result;
    }
    private static bool RpgText(string path,TextSlot slot)
    {
        if(!path.EndsWith(".json",StringComparison.OrdinalIgnoreCase))return false;
        return Regex.IsMatch(slot.Locator, @"/(nickname|description|profile|displayName|currencyUnit|gameTitle)$|^/terms/")
            || (Regex.IsMatch(slot.Locator, @"^/\d+/name$") && Regex.IsMatch(Path.GetFileName(path),
                @"^(Actors|Animations|Armors|Classes|Enemies|Items|Skills|States|Weapons)\.json$", RegexOptions.IgnoreCase));
    }
}
