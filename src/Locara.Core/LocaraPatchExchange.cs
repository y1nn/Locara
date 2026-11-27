using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Locara.Core;

public enum PatchMergeMode
{
    FillEmptyOnly,
    UpdateAll
}

public enum PatchEntryStatus
{
    Ready,
    SkippedExisting,
    Identical,
    TokenError,
    SourceMismatch,
    NotFound
}

public sealed record PatchImportEntry(
    Unit? Unit,
    string Path,
    string Locator,
    string Source,
    string CurrentTarget,
    string ProposedTarget,
    PatchEntryStatus Status,
    string Issue)
{
    public bool Include { get; set; } = Status == PatchEntryStatus.Ready;
}

public sealed record PatchImportPreview(
    string PatchEngine,
    string PatchVersion,
    DateTimeOffset? ExportedAtUtc,
    PatchMergeMode Mode,
    IReadOnlyList<PatchImportEntry> Entries,
    int ReadyCount,
    int SkippedExistingCount,
    int IdenticalCount,
    int TokenErrorCount,
    int SourceMismatchCount,
    int NotFoundCount);

public sealed record PatchExportResult(string FilePath, int ExportedCount);

public sealed record PatchImportResult(int Applied, int Skipped, bool ApprovedDirectly);

public static class LocaraPatchExchange
{
    public const string SchemaId = "locara-patch-v1";
    public const string DefaultFileExtension = ".locara-patch.json";

    private const long MaxPatchFileBytes = 64 * 1024 * 1024;
    private const int MaxJsonDepth = 16;

    public static PatchExportResult ExportApprovedPatch(ProjectStore store, string filePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        Identity.RequireNoLinks(fullPath);
        EnsureParentDirectory(fullPath);

        var approvedUnits = store.Query("""
            SELECT id,path,locator,source,target,context,status,warning,revision,imported
            FROM units
            WHERE active=1 AND status='approved' AND target<>'' AND warning=''
            ORDER BY path, locator
            """, ProjectStore.ReadUnit);

        var settings = new ProjectSettings(store);
        string engine = !string.IsNullOrWhiteSpace(settings.ScannedEngine)
            ? settings.ScannedEngine
            : (settings.Engine.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : settings.Engine);
        string locaraVersion = AppUpdateChecker.GetCurrentVersion().ToTagString();

        string staging = fullPath + ".locara-" + Guid.NewGuid().ToString("N");
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
                writer.WriteString("locaraVersion", locaraVersion);
                writer.WriteString("engine", engine);
                writer.WriteString("exportedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteStartArray("entries");

                foreach (var unit in approvedUnits)
                {
                    string safePath = Identity.SafePath(unit.Path);
                    string sourceHash = Identity.Hash(Encoding.UTF8.GetBytes(unit.Source));

                    writer.WriteStartObject();
                    writer.WriteString("path", safePath);
                    writer.WriteString("locator", unit.Locator);
                    writer.WriteString("sourceHash", sourceHash);
                    writer.WriteString("source", unit.Source);
                    writer.WriteString("target", unit.Target);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            File.Move(staging, fullPath, overwrite: true);
            return new PatchExportResult(fullPath, approvedUnits.Count);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    public static PatchImportPreview PreviewImport(
        ProjectStore store,
        string filePath,
        PatchMergeMode mode = PatchMergeMode.FillEmptyOnly)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        Identity.RequireNoLinks(fullPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("ملف حزمة التعريب غير موجود.", fullPath);

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxPatchFileBytes)
            throw new InvalidDataException("حجم ملف حزمة التعريب يتجاوز الحد المسموح به (64 ميجابايت).");

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("تعذّرت قراءة ملف حزمة التعريب JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("بنية ملف حزمة التعريب غير صالحة.");

            if (!root.TryGetProperty("schema", out var schemaProp)
                || schemaProp.ValueKind != JsonValueKind.String
                || !string.Equals(schemaProp.GetString(), SchemaId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"ملف حزمة التعريب غير مدعوم. المخطط المطلوب: {SchemaId}.");
            }

            string patchEngine = ReadOptionalString(root, "engine");
            string patchVersion = ReadOptionalString(root, "locaraVersion");
            DateTimeOffset? exportedAtUtc = null;
            if (root.TryGetProperty("exportedAtUtc", out var expProp)
                && expProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    expProp.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedDate))
            {
                exportedAtUtc = parsedDate;
            }

            if (!root.TryGetProperty("entries", out var entriesProp) || entriesProp.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("ملف حزمة التعريب يفتقد مصفوفة السطور entries.");

            var activeUnits = LoadActiveUnitsByKey(store);
            string validationEngine = new ProjectSettings(store).ScannedEngine;
            if (string.IsNullOrWhiteSpace(validationEngine))
                validationEngine = patchEngine;

            var seenKeys = new HashSet<(string Path, string Locator)>(PathLocatorComparer.Instance);
            var previewEntries = new List<PatchImportEntry>();

            int readyCount = 0;
            int skippedExistingCount = 0;
            int identicalCount = 0;
            int tokenErrorCount = 0;
            int sourceMismatchCount = 0;
            int notFoundCount = 0;

            foreach (var item in entriesProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("يحتوي ملف حزمة التعريب على عنصر غير صالح داخل entries.");

                string rawPath = ReadRequiredString(item, "path");
                string safePath = Identity.SafePath(rawPath);
                string locator = ReadRequiredString(item, "locator");
                string sourceHash = ReadRequiredString(item, "sourceHash").Trim();
                string patchSource = ReadOptionalString(item, "source");
                string proposedTarget = ReadRequiredString(item, "target");

                if (!seenKeys.Add((safePath, locator)))
                    throw new InvalidDataException($"ملف الحزمة يحتوي على سطر مكرر: {safePath} ({locator}).");

                if (!activeUnits.TryGetValue((safePath, locator), out var unit))
                {
                    previewEntries.Add(new PatchImportEntry(
                        null,
                        safePath,
                        locator,
                        patchSource,
                        "",
                        proposedTarget,
                        PatchEntryStatus.NotFound,
                        "السطر غير موجود في المشروع الحالي."));
                    notFoundCount++;
                    continue;
                }

                string actualHash = Identity.Hash(Encoding.UTF8.GetBytes(unit.Source));
                if (!string.Equals(actualHash, sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    previewEntries.Add(new PatchImportEntry(
                        unit,
                        safePath,
                        locator,
                        unit.Source,
                        unit.Target,
                        proposedTarget,
                        PatchEntryStatus.SourceMismatch,
                        "تغيّر النص الأصلي في هذا المشروع عن النص المصدّر في الحزمة."));
                    sourceMismatchCount++;
                    continue;
                }

                if (string.Equals(unit.Target, proposedTarget, StringComparison.Ordinal))
                {
                    previewEntries.Add(new PatchImportEntry(
                        unit,
                        safePath,
                        locator,
                        unit.Source,
                        unit.Target,
                        proposedTarget,
                        PatchEntryStatus.Identical,
                        "الترجمة مطابقة لما هو محفوظ حالياً."));
                    identicalCount++;
                    continue;
                }

                if (mode == PatchMergeMode.FillEmptyOnly && !string.IsNullOrWhiteSpace(unit.Target))
                {
                    previewEntries.Add(new PatchImportEntry(
                        unit,
                        safePath,
                        locator,
                        unit.Source,
                        unit.Target,
                        proposedTarget,
                        PatchEntryStatus.SkippedExisting,
                        "السطر مترجم مسبقاً (وضع ملء الفارغ فقط)."));
                    skippedExistingCount++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(proposedTarget))
                {
                    previewEntries.Add(new PatchImportEntry(
                        unit,
                        safePath,
                        locator,
                        unit.Source,
                        unit.Target,
                        proposedTarget,
                        PatchEntryStatus.TokenError,
                        "الترجمة المقترحة في الحزمة فارغة."));
                    tokenErrorCount++;
                    continue;
                }

                string tokenIssue = Tokens.Validate(unit.Source, proposedTarget, validationEngine);
                if (tokenIssue.Length > 0)
                {
                    previewEntries.Add(new PatchImportEntry(
                        unit,
                        safePath,
                        locator,
                        unit.Source,
                        unit.Target,
                        proposedTarget,
                        PatchEntryStatus.TokenError,
                        tokenIssue));
                    tokenErrorCount++;
                    continue;
                }

                previewEntries.Add(new PatchImportEntry(
                    unit,
                    safePath,
                    locator,
                    unit.Source,
                    unit.Target,
                    proposedTarget,
                    PatchEntryStatus.Ready,
                    ""));
                readyCount++;
            }

            return new PatchImportPreview(
                patchEngine,
                patchVersion,
                exportedAtUtc,
                mode,
                previewEntries,
                readyCount,
                skippedExistingCount,
                identicalCount,
                tokenErrorCount,
                sourceMismatchCount,
                notFoundCount);
        }
    }

    public static PatchImportResult ApplyImport(
        ProjectStore store,
        PatchImportPreview preview,
        bool approveImported = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(preview);

        var eligible = preview.Entries
            .Where(entry => entry.Include && entry.Status == PatchEntryStatus.Ready && entry.Unit != null)
            .ToList();

        int skipped = preview.Entries.Count - eligible.Count;
        if (eligible.Count == 0)
            return new PatchImportResult(0, skipped, approveImported);

        if (approveImported)
        {
            var edits = new List<(Unit Unit, bool Approve)>(eligible.Count);
            foreach (var entry in eligible)
            {
                var snapshot = entry.Unit!;
                edits.Add((new Unit
                {
                    Id = snapshot.Id,
                    Path = snapshot.Path,
                    Locator = snapshot.Locator,
                    Source = snapshot.Source,
                    Target = entry.ProposedTarget,
                    Revision = snapshot.Revision
                }, true));
            }

            store.SavePage(edits);

            for (int i = 0; i < eligible.Count; i++)
            {
                var original = eligible[i].Unit!;
                var committed = edits[i].Unit;
                original.Target = committed.Target;
                original.Status = committed.Status;
                original.Warning = committed.Warning;
                original.Revision = committed.Revision;
            }

            return new PatchImportResult(eligible.Count, skipped, ApprovedDirectly: true);
        }

        string engine = new ProjectSettings(store).ScannedEngine;
        var postCommitUpdates = new List<Action>(eligible.Count);

        store.Execute("BEGIN IMMEDIATE");
        try
        {
            foreach (var entry in eligible)
            {
                var unit = entry.Unit!;
                if (string.IsNullOrWhiteSpace(entry.ProposedTarget))
                    throw new InvalidDataException("لا يمكن استيراد ترجمة فارغة.");

                string warning = Tokens.Validate(unit.Source, entry.ProposedTarget, engine);
                if (warning.Length > 0)
                    throw new InvalidDataException("تعذّر استيراد السطر بسبب عدم تطابق الرموز. " + warning);

                store.Execute("""
                    UPDATE units SET target=$t, status='imported', warning='', revision=revision+1
                    WHERE id=$i AND revision=$rev AND source=$o AND active=1
                    """,
                    ("$t", entry.ProposedTarget),
                    ("$i", unit.Id),
                    ("$rev", unit.Revision),
                    ("$o", unit.Source));

                if (store.Query("SELECT changes()", r => r.GetInt32(0))[0] != 1)
                {
                    var draft = new Unit
                    {
                        Id = unit.Id,
                        Path = unit.Path,
                        Locator = unit.Locator,
                        Source = unit.Source,
                        Target = entry.ProposedTarget,
                        Revision = unit.Revision
                    };
                    throw new EditConflictException(draft, store.UnitsById(unit.Id));
                }

                postCommitUpdates.Add(() =>
                {
                    unit.Target = entry.ProposedTarget;
                    unit.Status = "imported";
                    unit.Warning = "";
                    unit.Revision++;
                });
            }

            store.Execute("COMMIT");
            foreach (var apply in postCommitUpdates)
                apply();

            return new PatchImportResult(eligible.Count, skipped, ApprovedDirectly: false);
        }
        catch
        {
            store.Execute("ROLLBACK");
            throw;
        }
    }

    private static Dictionary<(string Path, string Locator), Unit> LoadActiveUnitsByKey(ProjectStore store)
    {
        var units = store.Query("""
            SELECT id,path,locator,source,target,context,status,warning,revision,imported
            FROM units WHERE active=1
            """, ProjectStore.ReadUnit);

        var map = new Dictionary<(string Path, string Locator), Unit>(units.Count, PathLocatorComparer.Instance);
        foreach (var unit in units)
        {
            string normalizedPath;
            try
            {
                normalizedPath = Identity.SafePath(unit.Path);
            }
            catch (InvalidDataException)
            {
                normalizedPath = unit.Path.Replace('\\', '/');
            }

            map[(normalizedPath, unit.Locator)] = unit;
        }
        return map;
    }

    private static string ReadRequiredString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"الحقل المطلوب '{propertyName}' مفقود أو غير صالح في ملف الحزمة.");

        return prop.GetString() ?? "";
    }

    private static string ReadOptionalString(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? "";
        return "";
    }

    private static void EnsureParentDirectory(string fullPath)
    {
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private sealed class PathLocatorComparer : IEqualityComparer<(string Path, string Locator)>
    {
        public static PathLocatorComparer Instance { get; } = new();

        public bool Equals((string Path, string Locator) x, (string Path, string Locator) y) =>
            string.Equals(x.Path, y.Path, StringComparison.Ordinal)
            && string.Equals(x.Locator, y.Locator, StringComparison.Ordinal);

        public int GetHashCode((string Path, string Locator) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Path),
                StringComparer.Ordinal.GetHashCode(obj.Locator));
    }
}
