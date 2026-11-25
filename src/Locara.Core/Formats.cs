using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using LoadOptions = System.Xml.Linq.LoadOptions;
using SaveOptions = System.Xml.Linq.SaveOptions;
using YamlDotNet.RepresentationModel;

namespace Locara.Core;

public static class Formats
{
    public static IReadOnlyList<IFormatHandler> All { get; } =
        LocalizationProviderRegistry.All.Select(p => p.Handler).ToArray();
    internal static bool IsArabicTarget(string? language) => string.IsNullOrWhiteSpace(language)
        || language.Split('-', '_')[0].Equals("ar", StringComparison.OrdinalIgnoreCase)
        || language.Equals("arabic", StringComparison.OrdinalIgnoreCase);
    public static IFormatHandler? For(string path) => All.FirstOrDefault(f => f.Supports(path));

    public static bool Configurable(string path) =>
        For(path) is { } fmt && LocalizationProviderRegistry.TryGetByName(fmt.Name, out var p) && p.Metadata.IsConfigurable;

    internal static ExtractionPreview Inspect(byte[] bytes, IFormatHandler format, ExtractionRule? rule) => format switch
    {
        DelimitedFormat table => new([table.Inspect(bytes)], [], rule),
        SpreadsheetFormat workbook => new(workbook.Inspect(bytes), [], rule),
        JsonFormat or XmlFormat or IniFormat or YamlFormat or TomlFormat or LuaFormat or StarTruckerCatalog => new([], format.Extract(bytes), rule),
        _ => throw new NotSupportedException("هذا المورد يستخدم قواعد المحرك والصيغة الحالية.")
    };

    internal static IReadOnlyList<TextSlot> Extract(IEngineProfile profile, string path, byte[] bytes,
        IFormatHandler format, ExtractionRule? rule, bool rpgDataAtRoot, Action<string> advise)
    {
        bool engineExtracts = profile.Name == "rpgmaker";
        var slots = engineExtracts ? Engines.Extract(profile, new(path, bytes), format, rpgDataAtRoot) : format switch
        {
            DelimitedFormat table => table.Extract(bytes, rule),
            SpreadsheetFormat workbook => workbook.Extract(bytes, rule),
            RenpyFormat renpy => renpy.Extract(bytes, advise),
            _ => format.Extract(bytes)
        };
        var fields = rule?.Fields?.ToHashSet(StringComparer.Ordinal);
        var fieldPatterns = rule?.FieldPatterns is { Length: > 0 } patterns ? patterns : null;
        return slots.Where(slot => (engineExtracts || profile.Include(path, slot))
            && (fields == null || fields.Contains(slot.Locator))
            && (fieldPatterns == null || FieldSemantics.MatchesAnyPattern(slot.Locator, fieldPatterns))).ToList();
    }

    internal static byte[] Apply(byte[] bytes, IFormatHandler format,
        IReadOnlyDictionary<string, string> translations, ExtractionRule? rule)
    {
        byte[] output = format switch
        {
            DelimitedFormat table => table.Apply(bytes, translations, rule),
            SpreadsheetFormat workbook => workbook.Apply(bytes, translations, rule),
            _ => format.Apply(bytes, translations)
        };
        _ = InspectAfterWrite(output);
        return output;

        IReadOnlyList<TextSlot> InspectAfterWrite(byte[] result) => format switch
        {
            DelimitedFormat table => table.Extract(result, rule),
            SpreadsheetFormat workbook => workbook.Extract(result, rule),
            _ => format.Extract(result)
        };
    }

}

public sealed class JsonFormat : IFormatHandler
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    public string Name => "json";
    public bool Supports(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    private static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");
    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    private static JsonNode? ParseNode(byte[] bytes)
    {
        ReadOnlySpan<byte> payload = HasUtf8Bom(bytes) ? bytes.AsSpan(3) : bytes.AsSpan();
        try
        {
            return JsonNode.Parse(payload, nodeOptions: null, documentOptions: ParseOptions);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException("ملف JSON يحتوي على ترميز UTF-8 غير صالح.", error);
        }
    }
    private static void Walk(JsonNode? node, string path, Action<string, string> found)
    {
        if (node is JsonObject obj) foreach (var pair in obj) Walk(pair.Value, path + "/" + Escape(pair.Key), found);
        else if (node is JsonArray array) for (int i = 0; i < array.Count; i++) Walk(array[i], path + "/" + i, found);
        else if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) found(path, text);
    }
    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        try
        {
            var result = new List<TextSlot>();
            Walk(ParseNode(bytes), "", (p, s) => result.Add(new(p, s, p)));
            return result;
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException("ملف JSON يحتوي على ترميز UTF-8 غير صالح.", error);
        }
    }
    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string,string> translations)
    {
        var slots = Extract(bytes);
        if (!slots.Any(slot => translations.TryGetValue(slot.Locator, out var target) && target != slot.Source)) return bytes;
        var root = ParseNode(bytes);
        foreach (var slot in slots)
        {
            if (!translations.TryGetValue(slot.Locator, out var target)) continue;
            if (slot.Locator == "") { root = JsonValue.Create(target); continue; }
            var parts = slot.Locator[1..].Split('/').Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToArray();
            var parent = root;
            foreach (var part in parts[..^1]) parent = parent is JsonArray a ? a[int.Parse(part)] : parent![part];
            if (parent is JsonArray arr) arr[int.Parse(parts[^1])] = target; else parent![parts[^1]] = target;
        }
        string serialized = root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
        byte[] body = Encoding.UTF8.GetBytes(serialized);
        if (!HasUtf8Bom(bytes)) return body;
        byte[] withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
        return withBom;
    }
}

public sealed class DelimitedFormat(char delimiter) : IFormatHandler
{
    public string Name => delimiter == ',' ? "csv" : "tsv";
    public bool Supports(string path) => Path.GetExtension(path).Equals("." + Name, StringComparison.OrdinalIgnoreCase);
    internal static List<List<string>> Parse(string text, char separator)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i+1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && (c == separator || c == '\r' || c == '\n'))
            {
                row.Add(field.ToString()); field.Clear();
                if (c != separator) { rows.Add(row); row = []; if (c == '\r' && i+1 < text.Length && text[i+1] == '\n') i++; }
            }
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unclosed quoted field.");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
    internal static int SourceColumn(IReadOnlyList<string> headers)
    {
        var names = new[] { "source", "en", "english", "text", "value", "source_text", "msgstr_en" };
        for (int i=0;i<headers.Count;i++) if (names.Contains(headers[i].Trim().ToLowerInvariant())) return i;
        int locale = LocaleColumn(headers, "en");
        if (locale >= 0) return locale;
        throw new InvalidDataException("Map a source column by naming its header source, en, english, text or value.");
    }
    internal static int ExistingTargetColumn(List<string> headers)
    {
        int arabic = headers.FindIndex(h => h.Trim().ToLowerInvariant() is "ar" or "arabic" or "msgstr_ar");
        if (arabic < 0) arabic = LocaleColumn(headers, "ar");
        return arabic >= 0 ? arabic : headers.FindIndex(h => h.Trim().ToLowerInvariant() is "target" or "translation" or "target_text" or "translated");
    }
    private static int LocaleColumn(IReadOnlyList<string> headers, string locale)
    {
        // Unity Localization CSV headers have the form English(en) or Arabic(ar).
        string suffix = "(" + locale + ")";
        for (int index = 0; index < headers.Count; index++)
            if (headers[index].TrimEnd().EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }

    internal static string NewTargetHeader(IReadOnlyList<string> headers, int source, ExtractionRule? rule)
    {
        if (rule?.Target is { Length: > 0 } target) return target;
        return LocaleColumn(headers, "en") == source ? "Arabic(ar)" : "target";
    }

    internal static (int Key, int Source, int Target) Columns(List<string> headers, ExtractionRule? rule)
    {
        int Named(string name, bool newColumn)
        {
            var matches = headers.Select((header, index) => (header, index))
                .Where(column => column.header.Equals(name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1) return matches[0].index;
            if (matches.Count == 0 && newColumn) return headers.Count;
            throw new InvalidDataException("يجب أن يكون اسم العمود موجوداً وفريداً — " + name);
        }

        int key = rule?.Key switch
        {
            null => headers.FindIndex(h => h.Trim().ToLowerInvariant() is "id" or "key" or "key id" or "string_id" or "msgid" or "identifier"),
            "" => -1,
            string name => Named(name, false)
        };
        int source = rule?.Source is { Length: > 0 } sourceName ? Named(sourceName, false) : SourceColumn(headers);
        int target = rule?.Target is { Length: > 0 } targetName ? Named(targetName, true) : ExistingTargetColumn(headers);
        if (source == key || source == target || (key >= 0 && key == target))
            throw new InvalidDataException("اختر أعمدة مختلفة للمعرّف والمصدر والترجمة.");
        return (key, source, target);
    }

    private List<List<string>> Read(byte[] bytes) => Parse(Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF), delimiter);
    internal TablePreview Inspect(byte[] bytes)
    {
        var rows = Read(bytes);
        if (rows.Count == 0) return new("", [], []);
        int headers = rows[0].Count;
        var widerRows = Enumerable.Range(1, rows.Count - 1).Where(row => rows[row].Count > headers)
            .Take(5).Select(row => row + 1).ToList();
        return new("", rows[0], rows.Skip(1).Take(5).Select(row => row.ToArray()).ToList(), widerRows);
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes) => Extract(bytes, null);
    internal IReadOnlyList<TextSlot> Extract(byte[] bytes, ExtractionRule? rule)
    {
        var rows = Read(bytes);
        return rows.Count == 0 ? [] : Entries(rows, Columns(rows[0], rule)).Select(entry => entry.Slot).ToList();
    }

    private static IEnumerable<(int Row, TextSlot Slot)> Entries(List<List<string>> rows, (int Key, int Source, int Target) columns)
    {
        var (key, col, target) = columns;
        var keys = new HashSet<string>();
        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count <= col || string.IsNullOrWhiteSpace(row[col])) continue;
            string locator = key >= 0 && row.Count > key ? "key:" + row[key] : "row:" + i;
            if (!keys.Add(locator)) throw new InvalidDataException("Duplicate table key: " + locator);
            yield return (i, new TextSlot(locator, row[col], locator.StartsWith("key:", StringComparison.Ordinal) ? locator : "row " + i, target >= 0 && target < rows[0].Count && row.Count > target ? row[target] : null));
        }
    }
    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations) => Apply(bytes, translations, null);
    internal byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations, ExtractionRule? rule)
    {
        var rows = Read(bytes);
        if (rows.Count == 0) return bytes;
        var columns = Columns(rows[0], rule);
        var entries = Entries(rows, columns).Where(entry =>
            translations.TryGetValue(entry.Slot.Locator, out var target) && target != entry.Slot.ExistingTarget).ToList();
        if (entries.Count == 0) return bytes;
        int col = columns.Target;
        if (col < 0 || col == rows[0].Count)
        {
            col = rows.Max(row => row.Count);
            while (rows[0].Count < col) rows[0].Add("");
            rows[0].Add(NewTargetHeader(rows[0], columns.Source, rule));
        }
        foreach (var (row, slot) in entries)
        {
            while (rows[row].Count <= col) rows[row].Add("");
            rows[row][col] = translations[slot.Locator];
        }
        string Quote(string s) => s.IndexOfAny([delimiter,'"','\r','\n'])>=0 ? "\""+s.Replace("\"","\"\"")+"\"" : s;
        return Encoding.UTF8.GetBytes(string.Join("\r\n",rows.Select(r=>string.Join(delimiter,r.Select(Quote))))+"\r\n");
    }
}

public sealed class XmlFormat : IFormatHandler
{
    public string Name=>"xml/xliff";
    public bool Supports(string path)=>new[]{".xml",".xlf",".xliff"}.Contains(Path.GetExtension(path).ToLowerInvariant());
    internal static XDocument Read(byte[] bytes)
    {
        using var stream=new MemoryStream(bytes);
        using var reader=System.Xml.XmlReader.Create(stream,new System.Xml.XmlReaderSettings{ DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null });
        return XDocument.Load(reader,LoadOptions.PreserveWhitespace);
    }
    private static string Key(XElement e)=>string.Join("/",e.AncestorsAndSelf().Reverse().Select(x=>x.Name.LocalName+"["+x.ElementsBeforeSelf(x.Name).Count()+"]"));
    private static IEnumerable<XElement> Sources(XDocument doc)=>doc.Descendants().Where(e=>e.Name.LocalName=="source" || (e.Name.LocalName is "string" or "entry" && !e.HasElements));
    private static string Content(XElement element) => element.HasElements
        ? string.Concat(element.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))) : element.Value;
    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        return Sources(Read(bytes)).Where(e => !string.IsNullOrWhiteSpace(e.Value)).Select(e =>
        {
            var target = e.Name.LocalName == "source" ? e.Parent?.Element(e.Name.Namespace + "target") : null;
            var language = (string?)target?.Attribute(XNamespace.Xml + "lang") ?? e.Ancestors()
                .Select(x => (string?)x.Attribute("trgLang") ?? (string?)x.Attribute("target-language")).FirstOrDefault(x => x != null);
            return new TextSlot(Key(e), Content(e), e.Parent?.Attribute("id")?.Value ?? "",
                target != null && Formats.IsArabicTarget(language) ? Content(target) : null);
        }).ToList();
    }
    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        var doc = Read(bytes);
        bool changed = false;
        foreach (var e in Sources(doc).ToList())
        {
            if (!translations.TryGetValue(Key(e), out var target)) continue;
            string? current = e.Name.LocalName == "source"
                ? (e.Parent?.Element(e.Name.Namespace + "target") is { } existing ? Content(existing) : null)
                : Content(e);
            if (target == current) continue;
            XElement dest = e;
            if (e.Name.LocalName == "source")
            {
                dest = e.Parent!.Element(e.Name.Namespace + "target") ?? new XElement(e.Name.Namespace + "target");
                if (dest.Parent == null) e.AddAfterSelf(dest);
            }
            if (e.HasElements)
            {
                var fragment = Read(Encoding.UTF8.GetBytes("<root>" + target + "</root>")).Root!;
                dest.ReplaceNodes(fragment.Nodes());
            }
            else dest.Value = target;
            changed = true;
        }
        if (!changed) return bytes;
        string contents = doc.ToString(SaveOptions.DisableFormatting);
        if (doc.Declaration is { } declaration)
            contents = new XDeclaration(declaration.Version,
                string.IsNullOrWhiteSpace(declaration.Encoding) ? "UTF-8" : declaration.Encoding,
                declaration.Standalone) + "\n" + contents;
        return Encoding.UTF8.GetBytes(contents);
    }
}

public sealed class IniFormat : IFormatHandler
{
    public string Name => "ini";
    public bool Supports(string path) => Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<(string Key, int Line, string Prefix, string Value)> Entries(string[] lines)
    {
        string section = "";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            string s = lines[i].Trim();
            if (s.StartsWith('[') && s.EndsWith(']')) { section = s; continue; }
            int pos = lines[i].IndexOf('=');
            if (pos < 0 || s.StartsWith(';') || s.StartsWith('#')) continue;
            string baseKey = section + "/" + lines[i][..pos].Trim();
            int occurrence = counts.GetValueOrDefault(baseKey) + 1;
            counts[baseKey] = occurrence;
            string locator = occurrence == 1 ? baseKey : $"{baseKey}#{occurrence}";
            yield return (locator, i, lines[i][..(pos + 1)], lines[i][(pos + 1)..]);
        }
    }
    public IReadOnlyList<TextSlot> Extract(byte[] bytes) =>
        Entries(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n'))
            .Where(e => !string.IsNullOrWhiteSpace(e.Value))
            .Select(e => new TextSlot(e.Key, e.Value.TrimEnd('\r'), e.Key)).ToList();

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        string text = Encoding.UTF8.GetString(bytes);
        bool bom = text.Length > 0 && text[0] == '\uFEFF';
        var lines = (bom ? text[1..] : text).Split('\n');
        bool changed = false;
        foreach (var e in Entries(lines).ToList())
        {
            if (!translations.TryGetValue(e.Key, out var t)) continue;
            if (t.Contains('\n') || t.Contains('\r')) throw new InvalidDataException("INI target must fit one line.");
            string updated = e.Prefix + t + (lines[e.Line].EndsWith('\r') ? "\r" : "");
            if (lines[e.Line] == updated) continue;
            lines[e.Line] = updated;
            changed = true;
        }
        return changed ? Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + string.Join('\n', lines)) : bytes;
    }
}

public sealed class YamlFormat : IFormatHandler
{
    public string Name=>"yaml";
    public bool Supports(string path)=>new[]{".yml",".yaml"}.Contains(Path.GetExtension(path).ToLowerInvariant());
    private static bool HasUtf8Bom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
    private static YamlStream Read(byte[] b)
    {
        string text = HasUtf8Bom(b) ? Encoding.UTF8.GetString(b, 3, b.Length - 3) : Encoding.UTF8.GetString(b);
        var y = new YamlStream();
        y.Load(new StringReader(text));
        return y;
    }
    private static void Walk(YamlNode n,string key,Action<string,YamlScalarNode> found)
    {
        if(n is YamlMappingNode m) foreach(var p in m.Children) Walk(p.Value,key+"/"+p.Key.ToString().Replace("~","~0").Replace("/","~1"),found);
        else if(n is YamlSequenceNode a)for(int i=0;i<a.Children.Count;i++)Walk(a.Children[i],key+"/"+i,found);
        else if(n is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value) && !double.TryParse(s.Value,out _) && s.Value is not ("true" or "false" or "null"))found(key,s);
    }
    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        var y = Read(bytes);
        var r = new List<TextSlot>();
        for (int i = 0; i < y.Documents.Count; i++) Walk(y.Documents[i].RootNode, i.ToString(), (k, s) => r.Add(new(k, s.Value!, k)));
        return r;
    }
    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        if (translations.Count == 0) return bytes;
        var y = Read(bytes);
        bool changed = false;
        for (int i = 0; i < y.Documents.Count; i++)
        {
            Walk(y.Documents[i].RootNode, i.ToString(), (k, s) =>
            {
                if (translations.TryGetValue(k, out var t) && t != s.Value)
                {
                    s.Value = t;
                    changed = true;
                }
            });
        }
        if (!changed) return bytes;
        using var w = new StringWriter();
        y.Save(w, false);
        byte[] body = Encoding.UTF8.GetBytes(w.ToString());
        if (!HasUtf8Bom(bytes)) return body;
        byte[] withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
        return withBom;
    }
}

public sealed class SpreadsheetFormat : IFormatHandler
{
    public string Name => "xlsx";
    public bool Supports(string path) => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

    private static List<string> Headers(IXLWorksheet sheet) => Enumerable.Range(1, sheet.LastColumnUsed()?.ColumnNumber() ?? 0)
        .Select(column => sheet.Cell(1, column).GetString()).ToList();

    internal IReadOnlyList<TablePreview> Inspect(byte[] bytes)
    {
        using var book = new XLWorkbook(new MemoryStream(bytes));
        return book.Worksheets.Select(sheet =>
        {
            var headers = Headers(sheet);
            int sampleRows = Math.Min(5, Math.Max(0, (sheet.LastRowUsed()?.RowNumber() ?? 1) - 1));
            return new TablePreview(sheet.Name, headers, Enumerable.Range(2, sampleRows)
                .Select(row => Enumerable.Range(1, headers.Count).Select(col => sheet.Cell(row, col).GetString()).ToArray()).ToList());
        }).ToList();
    }

    private static IEnumerable<IXLWorksheet> Sheets(XLWorkbook book, ExtractionRule? rule) =>
        string.IsNullOrEmpty(rule?.Sheet) ? book.Worksheets : [book.Worksheet(rule.Sheet)];

    private static IEnumerable<(int Row, TextSlot Slot)> Entries(IXLWorksheet sheet, (int Key, int Source, int Target) columns)
    {
        int last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int row = 2; row <= last; row++)
        {
            var source = sheet.Cell(row, columns.Source + 1);
            if (source.HasFormula || source.IsEmpty()) continue;
            string? key = columns.Key < 0 ? null : sheet.Cell(row, columns.Key + 1).GetString();
            string locator = sheet.Name + "!" + (string.IsNullOrEmpty(key) ? source.Address.ToString() : "key:" + key);
            if (!keys.Add(locator)) throw new InvalidDataException("Duplicate worksheet key: " + locator);
            var target = columns.Target < 0 ? null : sheet.Cell(row, columns.Target + 1);
            yield return (row, new(locator, source.GetString(), sheet.Name, target is { HasFormula: false } ? target.GetString() : null));
        }
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes) => Extract(bytes, null);
    internal IReadOnlyList<TextSlot> Extract(byte[] bytes, ExtractionRule? rule)
    {
        using var book = new XLWorkbook(new MemoryStream(bytes));
        var slots = new List<TextSlot>();
        foreach (var sheet in Sheets(book, rule))
        {
            var headers = Headers(sheet);
            if (headers.Count == 0) continue;
            // Keep the existing worksheet-and-cell identity for automatically detected tables.
            var columns = DelimitedFormat.Columns(headers, rule ?? new(Key: ""));
            slots.AddRange(Entries(sheet, columns).Select(entry => entry.Slot));
        }
        return slots;
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations) => Apply(bytes, translations, null);
    internal byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations, ExtractionRule? rule)
    {
        using var book = new XLWorkbook(new MemoryStream(bytes));
        bool updated = false;
        foreach (var sheet in Sheets(book, rule))
        {
            var headers = Headers(sheet);
            if (headers.Count == 0) continue;
            var columns = DelimitedFormat.Columns(headers, rule ?? new(Key: ""));
            var changes = Entries(sheet, columns).Where(entry =>
                translations.TryGetValue(entry.Slot.Locator, out var target) && target != entry.Slot.ExistingTarget).ToList();
            if (changes.Count == 0) continue;
            updated = true;
            int target = columns.Target < 0 ? headers.Count : columns.Target;
            if (target == headers.Count) sheet.Cell(1, target + 1).Value = DelimitedFormat.NewTargetHeader(headers, columns.Source, rule);
            foreach (var (row, slot) in changes)
            {
                var cell = sheet.Cell(row, target + 1);
                if (cell.HasFormula) throw new InvalidDataException("Target cell has a formula.");
                cell.Value = translations[slot.Locator];
            }
        }
        if (!updated) return bytes;
        using var output = new MemoryStream();
        book.SaveAs(output);
        return output.ToArray();
    }
}
