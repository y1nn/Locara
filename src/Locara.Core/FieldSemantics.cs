using System.Text.RegularExpressions;

namespace Locara.Core;

public enum FieldSemanticHint
{
    Neutral,
    LikelyHumanText,
    LikelyTechnical
}

/// <summary>
/// Semantic field-name analyzer and locator pattern normalizer for structured formats
/// (JSON, YAML, XML, INI, CSV/TSV, XLSX). Distinguishes player-facing prose keys
/// from technical/engine configuration keys and normalizes variable array indices into
/// reusable schema patterns (e.g. "/0/items/12/description" -> "/*/items/*/description").
/// </summary>
public static partial class FieldSemantics
{
    private static readonly HashSet<string> HumanKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "title", "name", "displayname", "display_name", "display-name",
        "description", "desc", "shortdesc", "short_desc", "longdesc", "long_desc",
        "summary", "dialogue", "dialog", "line", "lines", "caption", "captions",
        "subtitle", "subtitles", "tooltip", "tooltips", "hint", "hints", "help",
        "lore", "flavor", "flavortext", "flavor_text", "flavour", "message", "messages", "msg",
        "label", "labels", "header", "headers", "heading", "body", "content", "contents",
        "prompt", "prompts", "choice", "choices", "quest", "objective", "objectives",
        "bio", "biography", "note", "notes", "story", "speech", "narration", "narrator",
        "greeting", "farewell", "response", "responses", "answer", "answers", "question", "questions",
        "button", "tab", "warning", "error", "errormessage", "notice", "tip", "tips",
        "quote", "quotes", "comment", "comments", "tutorial", "instruction", "instructions",
        "overview", "detail", "details", "info", "information", "tagline", "slogan",
        "speaker", "character", "charactername", "npc", "author", "credit", "credits",
        "nickname", "profile", "localized", "translation", "english", "en", "en_us", "en-us"
    };

    private static readonly HashSet<string> TechnicalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "uid", "uuid", "guid", "key", "keys", "code", "codename",
        "prefab", "prefabs", "asset", "assets", "sprite", "sprites", "icon", "icons",
        "texture", "textures", "image", "images", "img", "mesh", "meshes", "model", "models",
        "material", "materials", "shader", "shaders", "font", "fonts", "atlas",
        "sound", "sounds", "audio", "sfx", "bgm", "music", "voice", "clip", "clips", "bank",
        "anim", "animation", "animations", "animator", "controller", "state", "states",
        "layer", "layers", "tag", "tags", "class", "classname", "type", "types", "kind",
        "category", "enum", "mode", "modes", "style", "styles", "layout", "layouts",
        "align", "alignment", "anchor", "pivot", "orientation", "direction", "dir",
        "color", "colour", "tint", "hex", "rgba", "rgb",
        "biome", "biomes", "element", "elements", "elementid", "world", "seed", "terrain",
        "path", "paths", "file", "files", "filename", "filepath", "folder", "directory",
        "url", "uri", "href", "src", "link", "endpoint", "host",
        "script", "scripts", "method", "function", "callback", "handler", "event", "events",
        "trigger", "triggers", "hook", "flag", "flags", "mask", "bitmask",
        "version", "ver", "hash", "checksum", "sha", "sha256", "md5", "crc",
        "namespace", "assembly", "module", "package", "bundle", "scene", "level",
        "coord", "coords", "position", "pos", "rotation", "rot", "scale", "size",
        "width", "height", "depth", "offset", "padding", "margin", "bounds", "rect",
        "order", "sortorder", "priority", "weight", "chance", "probability", "rate",
        "speed", "duration", "cooldown", "delay", "range", "radius", "damage", "health",
        "cost", "price", "formula", "regex", "pattern", "format", "encoding", "charset",
        "locale", "lang", "language", "culture", "region", "currency",
        "systemlanguage", "system_language", "langcode", "lang_code", "languagecode", "language_code",
        "localecode", "locale_code", "isocode", "iso_code", "culturecode", "culture_code",
        "culturename", "culture_name", "textdirection", "text_direction", "fileversion", "formatversion",
        "countrycode", "country_code", "regioncode", "region_code", "currencycode", "currency_code",
        "errorcode", "error_code", "statuscode", "status_code", "colorcode", "color_code",
        "hashcode", "shortcode", "unicode", "bbcode", "netcode",
        "parent", "parentid", "child", "children", "ref", "reference", "targetid", "sourceid",
        "table", "sheet", "column", "row", "index", "slot", "group", "groupid",
        "component", "components", "behavior", "behaviour", "trait", "traits",
        "spawn", "spawner", "loot", "drop", "recipe", "ingredient", "product",
        "hotkey", "keycode", "binding", "input", "action", "axis", "command", "cmd",
        "bg", "background", "fg", "foreground", "border", "frame", "cursor", "theme"
    };

    private static readonly string[] TechnicalSuffixes =
    [
        "_id", "id", "_key", "_ref", "_path", "_file", "_prefab", "_sprite", "_icon",
        "_shader", "_sound", "_audio", "_anim", "_tag", "_type", "_mode", "_style",
        "_layout", "_color", "_colour", "_guid", "_uuid", "_hash", "_url", "_uri",
        "_code", "_locale", "_lang", "_charset", "_encoding"
    ];

    private static readonly string[] HumanSuffixes =
    [
        "_name", "name", "_title", "title", "_desc", "_description", "description",
        "_text", "text", "_label", "label", "_tooltip", "tooltip", "_caption", "caption",
        "_dialogue", "_dialog", "_message", "_msg", "_hint", "_lore", "_summary", "_subtitle"
    ];

    public static string NormalizeLocatorPattern(string locator)
    {
        if (string.IsNullOrEmpty(locator)) return "";
        // XML XPath-like locators: "root[0]/entry[12]/text[0]" -> "root[*]/entry[*]/text[*]"
        if (XmlIndexPattern().IsMatch(locator))
            return XmlIndexPattern().Replace(locator, "[*]");

        // JSON pointer ("/0/items/12/description") or YAML path ("0/elements/5/name")
        string[] parts = locator.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && AllDigitsPattern().IsMatch(parts[i]))
                parts[i] = "*";
        }
        return string.Join('/', parts);
    }

    public static string ExtractLeafKey(string? locator)
    {
        if (string.IsNullOrWhiteSpace(locator)) return "";
        string trimmed = locator.Trim();

        // Strip trailing XML index "[0]" or "[*]"
        string normalized = XmlIndexPattern().Replace(trimmed, "");
        // Strip leading INI / loc-txt "[Section]" prefix before splitting on '/' (handles "[UI/Menu]systemLanguage")
        if (normalized.StartsWith('[') && normalized.IndexOf(']') is int leadClose && leadClose > 1 && leadClose + 1 < normalized.Length)
        {
            string secInner = normalized[1..leadClose];
            if (secInner != "*")
                normalized = normalized[(leadClose + 1)..].Trim();
        }
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            string seg = parts[i].Replace("~1", "/").Replace("~0", "~").Trim();
            if (seg.StartsWith('[') && seg.IndexOf(']') is int secClose && secClose > 0 && secClose + 1 < seg.Length)
                seg = seg[(secClose + 1)..].Trim();
            // Strip spreadsheet cell prefix "Sheet!key:foo" -> "foo"
            int keyIdx = seg.IndexOf("key:", StringComparison.OrdinalIgnoreCase);
            if (keyIdx >= 0 && keyIdx + 4 < seg.Length)
                seg = seg[(keyIdx + 4)..];
            // Strip duplicate-key "#N" disambiguator ("systemLanguage#5" -> "systemLanguage")
            int hashIdx = seg.LastIndexOf('#');
            if (hashIdx > 0 && hashIdx + 1 < seg.Length && AllDigitsPattern().IsMatch(seg[(hashIdx + 1)..]))
                seg = seg[..hashIdx];
            if (seg.Length == 0 || seg == "*" || AllDigitsPattern().IsMatch(seg))
                continue;
            return seg;
        }
        return "";
    }

    public static FieldSemanticHint ClassifyFieldKey(string? locator)
    {
        string leaf = ExtractLeafKey(locator);
        if (leaf.Length == 0) return FieldSemanticHint.Neutral;

        if (HumanKeys.Contains(leaf))
            return FieldSemanticHint.LikelyHumanText;
        if (TechnicalKeys.Contains(leaf))
            return FieldSemanticHint.LikelyTechnical;

        // Check camelCase / snake_case suffixes cleanly (e.g. "itemPrefab" -> technical, "questTitle" -> human)
        string lower = leaf.ToLowerInvariant();
        if (lower.Length >= 5)
        {
            foreach (string suffix in HumanSuffixes)
            {
                if (lower.EndsWith(suffix, StringComparison.Ordinal) && lower.Length > suffix.Length)
                {
                    // Guard against technical compounds like "filename", "classname", "codename", "assetName", "prefabName", "spriteName", "sceneName", "soundName", "tagName", "keyName"
                    if (suffix is "name" or "_name" && HasTechnicalPrefix(lower[..^suffix.Length]))
                        return FieldSemanticHint.LikelyTechnical;
                    return FieldSemanticHint.LikelyHumanText;
                }
            }
            foreach (string suffix in TechnicalSuffixes)
            {
                if (lower.EndsWith(suffix, StringComparison.Ordinal) && lower.Length > suffix.Length)
                    return FieldSemanticHint.LikelyTechnical;
            }
        }
        return FieldSemanticHint.Neutral;
    }

    private static bool HasTechnicalPrefix(string prefix)
    {
        string trimmed = prefix.TrimEnd('_', '-');
        return trimmed is "file" or "class" or "code" or "asset" or "prefab" or "sprite" or "icon"
            or "texture" or "shader" or "material" or "scene" or "sound" or "audio" or "anim"
            or "animation" or "state" or "layer" or "tag" or "key" or "type" or "font" or "atlas"
            or "bundle" or "package" or "assembly" or "method" or "function" or "event" or "trigger"
            or "element" or "biome" or "object" or "component" or "script" or "mesh" or "model";
    }

    public static bool MatchesPattern(string locator, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string trimmed = pattern.Trim();
        if (string.Equals(locator, trimmed, StringComparison.Ordinal))
            return true;
        string normalized = NormalizeLocatorPattern(locator);
        if (string.Equals(normalized, trimmed, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!trimmed.Contains('/') && !trimmed.Contains('['))
        {
            string leaf = ExtractLeafKey(locator);
            if (leaf.Length > 0 && string.Equals(leaf, trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool MatchesAnyPattern(string locator, IReadOnlyList<string>? patterns)
    {
        if (patterns == null || patterns.Count == 0) return true;
        foreach (string pattern in patterns)
        {
            if (MatchesPattern(locator, pattern))
                return true;
        }
        return false;
    }

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex AllDigitsPattern();

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex XmlIndexPattern();
}
