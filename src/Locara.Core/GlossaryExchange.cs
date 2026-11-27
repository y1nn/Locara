using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Locara.Core;

public enum GlossaryImportMode
{
    AddMissingOnly,
    OverwriteExisting
}

public enum GlossaryEntryState
{
    New,
    Identical,
    Conflict,
    DuplicateInFile,
    Invalid
}

public sealed record GlossaryImportEntry(
    string Source,
    string Target,
    string ExistingTarget,
    GlossaryEntryState State,
    string Note);

public sealed record GlossaryImportPreview(
    string SourceFormat,
    IReadOnlyList<GlossaryImportEntry> Entries,
    int NewCount,
    int IdenticalCount,
    int ConflictCount,
    int DuplicateCount,
    int InvalidCount);

public sealed record GlossaryImportResult(int Added, int Updated, int Skipped);

public static class GlossaryExchange
{
    public const string SchemaId = "locara-glossary-v1";
    private const long MaxImportFileBytes = 16 * 1024 * 1024;
    private const int MaxJsonDepth = 16;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private static readonly HashSet<string> SourceHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "source", "term", "en", "english", "original", "key", "msgid", "المصطلح", "الأصل"
    };

    private static readonly HashSet<string> TargetHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "target", "translation", "ar", "arabic", "value", "msgstr", "الترجمة", "المقابل"
    };

    public static int ExportJson(ProjectStore store, string filePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string path = Path.GetFullPath(filePath);
        Identity.RequireNoLinks(path);
        EnsureParentDirectory(path);

        var terms = store.Glossary()
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        string staging = path + ".locara-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", SchemaId);
                writer.WriteString("exportedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteStartArray("terms");
                foreach (var (source, target) in terms)
                {
                    writer.WriteStartObject();
                    writer.WriteString("source", source);
                    writer.WriteString("target", target);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            File.Move(staging, path, overwrite: true);
            return terms.Count;
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    public static int ExportCsv(ProjectStore store, string filePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string path = Path.GetFullPath(filePath);
        Identity.RequireNoLinks(path);
        EnsureParentDirectory(path);

        var terms = store.Glossary()
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        string staging = path + ".locara-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var writer = new StreamWriter(staging, append: false, Utf8WithBom))
            {
                writer.WriteLine("source,target");
                foreach (var (source, target) in terms)
                {
                    writer.Write(QuoteCsv(EscapeFormula(source)));
                    writer.Write(',');
                    writer.WriteLine(QuoteCsv(EscapeFormula(target)));
                }
            }

            File.Move(staging, path, overwrite: true);
            return terms.Count;
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    public static GlossaryImportPreview PreviewImport(ProjectStore store, string filePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string path = Path.GetFullPath(filePath);
        Identity.RequireNoLinks(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("ملف القاموس غير موجود.", path);

        var info = new FileInfo(path);
        if (info.Length > MaxImportFileBytes)
            throw new InvalidDataException("حجم ملف القاموس يتجاوز الحد المسموح به (16 ميجابايت).");

        string rawText = File.ReadAllText(path, Utf8NoBom);
        if (rawText.Length > 0 && rawText[0] == '\uFEFF')
            rawText = rawText[1..];

        string format = DetectFormat(path, rawText);
        List<(string Source, string Target)> parsedRows = format == "json"
            ? ParseJsonTerms(rawText)
            : ParseCsvTerms(rawText);

        var existingMap = BuildExistingMap(store);
        var seenInFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<GlossaryImportEntry>(parsedRows.Count);

        int newCount = 0;
        int identicalCount = 0;
        int conflictCount = 0;
        int duplicateCount = 0;
        int invalidCount = 0;

        foreach (var (rawSource, rawTarget) in parsedRows)
        {
            string source = rawSource.Trim();
            string target = rawTarget.Trim();

            if (source.Length == 0 || target.Length == 0)
            {
                entries.Add(new GlossaryImportEntry(
                    source,
                    target,
                    "",
                    GlossaryEntryState.Invalid,
                    "المصطلح أو الترجمة فارغة."));
                invalidCount++;
                continue;
            }

            if (seenInFile.TryGetValue(source, out string? previousTarget))
            {
                string existingDisplay = existingMap.TryGetValue(source, out var existingEntry)
                    ? existingEntry.Target
                    : previousTarget;
                string duplicateNote = string.Equals(previousTarget, target, StringComparison.Ordinal)
                    ? "مكرر داخل الملف بنفس الترجمة — سيُتجاهل."
                    : $"مكرر داخل الملف بترجمة مختلفة (السابق: {previousTarget}).";
                entries.Add(new GlossaryImportEntry(
                    source,
                    target,
                    existingDisplay,
                    GlossaryEntryState.DuplicateInFile,
                    duplicateNote));
                duplicateCount++;
                continue;
            }

            seenInFile[source] = target;

            if (!existingMap.TryGetValue(source, out var current))
            {
                entries.Add(new GlossaryImportEntry(
                    source,
                    target,
                    "",
                    GlossaryEntryState.New,
                    "مصطلح جديد"));
                newCount++;
            }
            else if (string.Equals(current.Target, target, StringComparison.Ordinal))
            {
                entries.Add(new GlossaryImportEntry(
                    source,
                    target,
                    current.Target,
                    GlossaryEntryState.Identical,
                    "مطابق للموجود في القاموس"));
                identicalCount++;
            }
            else
            {
                entries.Add(new GlossaryImportEntry(
                    source,
                    target,
                    current.Target,
                    GlossaryEntryState.Conflict,
                    "يتعارض مع الترجمة الحالية في القاموس"));
                conflictCount++;
            }
        }

        return new GlossaryImportPreview(
            format,
            entries,
            newCount,
            identicalCount,
            conflictCount,
            duplicateCount,
            invalidCount);
    }

    public static GlossaryImportResult ApplyImport(
        ProjectStore store,
        GlossaryImportPreview preview,
        GlossaryImportMode mode = GlossaryImportMode.AddMissingOnly)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(preview);

        if (preview.Entries.Count == 0)
            return new GlossaryImportResult(0, 0, 0);

        int added = 0;
        int updated = 0;
        int skipped = 0;

        store.Execute("BEGIN IMMEDIATE");
        try
        {
            var existingMap = BuildExistingMap(store);

            foreach (var entry in preview.Entries)
            {
                if (entry.State is GlossaryEntryState.Invalid
                    or GlossaryEntryState.DuplicateInFile
                    or GlossaryEntryState.Identical)
                {
                    skipped++;
                    continue;
                }

                string source = entry.Source.Trim();
                string target = entry.Target.Trim();
                if (source.Length == 0 || target.Length == 0)
                {
                    skipped++;
                    continue;
                }

                if (existingMap.TryGetValue(source, out var existing))
                {
                    if (mode != GlossaryImportMode.OverwriteExisting
                        || string.Equals(existing.Target, target, StringComparison.Ordinal))
                    {
                        skipped++;
                        continue;
                    }

                    if (!string.Equals(existing.Key, source, StringComparison.Ordinal))
                        store.RemoveTerm(existing.Key);

                    store.Glossary(source, target);
                    existingMap[source] = (source, target);
                    updated++;
                }
                else
                {
                    store.Glossary(source, target);
                    existingMap[source] = (source, target);
                    added++;
                }
            }

            store.Execute("COMMIT");
            return new GlossaryImportResult(added, updated, skipped);
        }
        catch
        {
            store.Execute("ROLLBACK");
            throw;
        }
    }

    private static Dictionary<string, (string Key, string Target)> BuildExistingMap(ProjectStore store)
    {
        var map = new Dictionary<string, (string Key, string Target)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, target) in store.Glossary())
        {
            if (!map.ContainsKey(key))
                map[key] = (key, target);
        }
        return map;
    }

    private static string DetectFormat(string filePath, string content)
    {
        string ext = Path.GetExtension(filePath);
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return "json";
        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return "csv";

        ReadOnlySpan<char> trimmed = content.AsSpan().TrimStart();
        if (!trimmed.IsEmpty && trimmed[0] is '{' or '[')
            return "json";

        return "csv";
    }

    private static List<(string Source, string Target)> ParseJsonTerms(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("ملف القاموس JSON فارغ.");

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaxJsonDepth,
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            var root = doc.RootElement;
            var rows = new List<(string Source, string Target)>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                ReadTermArray(root, rows);
                return rows;
            }

            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("بنية ملف القاموس JSON غير صالحة.");

            bool hasSchema = root.TryGetProperty("schema", out var schemaProp);
            if (hasSchema)
            {
                if (schemaProp.ValueKind != JsonValueKind.String
                    || !string.Equals(schemaProp.GetString(), SchemaId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"مخطط القاموس غير مدعوم. المتوقع: {SchemaId}.");
                }
            }

            if (root.TryGetProperty("terms", out var termsProp))
            {
                if (termsProp.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("خاصية terms في ملف القاموس يجب أن تكون مصفوفة.");

                ReadTermArray(termsProp, rows);
                return rows;
            }

            if (hasSchema)
                throw new InvalidDataException("ملف القاموس يفتقد مصفوفة المصطلحات terms.");

            // Support simple flat key-value JSON map { "Mana": "مانا" }
            foreach (var prop in root.EnumerateObject())
            {
                string target = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "") : "";
                rows.Add((prop.Name, target));
            }
            return rows;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("تعذّرت قراءة ملف القاموس JSON: " + ex.Message, ex);
        }
    }

    private static void ReadTermArray(JsonElement array, List<(string Source, string Target)> rows)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                rows.Add(("", ""));
                continue;
            }

            string source = TryReadStringProperty(item, "source");
            string target = TryReadStringProperty(item, "target");
            rows.Add((source, target));
        }
    }

    private static string TryReadStringProperty(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? "";
        return "";
    }

    private static List<(string Source, string Target)> ParseCsvTerms(string csvText)
    {
        var rawRows = DelimitedFormat.Parse(csvText, ',');
        var nonEmptyRows = rawRows
            .Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .ToList();

        if (nonEmptyRows.Count == 0)
            throw new InvalidDataException("ملف القاموس CSV فارغ.");

        var firstRow = nonEmptyRows[0];
        if (firstRow.Count < 2)
            throw new InvalidDataException("يجب أن يحتوي ملف القاموس CSV على عمودين على الأقل: source و target.");

        int sourceCol = -1;
        int targetCol = -1;
        for (int i = 0; i < firstRow.Count; i++)
        {
            string header = firstRow[i].Trim();
            if (sourceCol < 0 && SourceHeaderNames.Contains(header))
                sourceCol = i;
            else if (targetCol < 0 && TargetHeaderNames.Contains(header))
                targetCol = i;
        }

        bool hasHeader = sourceCol >= 0 && targetCol >= 0;
        if (!hasHeader)
        {
            sourceCol = 0;
            targetCol = 1;
        }

        var results = new List<(string Source, string Target)>(nonEmptyRows.Count);
        int startIndex = hasHeader ? 1 : 0;
        for (int i = startIndex; i < nonEmptyRows.Count; i++)
        {
            var row = nonEmptyRows[i];
            string source = sourceCol < row.Count ? UnescapeFormula(row[sourceCol]) : "";
            string target = targetCol < row.Count ? UnescapeFormula(row[targetCol]) : "";
            results.Add((source, target));
        }

        return results;
    }

    private static void EnsureParentDirectory(string fullPath)
    {
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private static string QuoteCsv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool IsFormula(string value) =>
        value.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '-' or '@';

    private static string EscapeFormula(string value) =>
        IsFormula(value) || value.StartsWith('\'') ? "'" + value : value;

    private static string UnescapeFormula(string value) =>
        value.StartsWith('\'') && value.Length > 1 && (value[1] == '\'' || IsFormula(value[1..]))
            ? value[1..]
            : value;
}
