using Microsoft.Data.Sqlite;

namespace Locara.Core;

public sealed record SharedMemoryStats(int TotalEntries, long TotalReuses, string DatabasePath);

public sealed record SharedMemoryPrefillResult(
    int MatchedUnits,
    int SavedCharacters,
    int SkippedTokenMismatches,
    int SkippedGlossaryConflicts,
    bool AutoApproved);

/// <summary>
/// Cross-project SQLite WAL translation memory stored in %LOCALAPPDATA%\Locara\translation-memory.db
/// for persisting approved translations (common menus, settings, HUD labels) and auto-prefilling
/// 100% exact matches before invoking AI bots.
/// </summary>
public sealed class SharedTranslationMemory : IDisposable
{
    private const int MaxSourceLength = 500;
    private const int MaxTargetLength = 800;
    private const int MaxLineCount = 3;

    private readonly SqliteConnection connection;
    public string DatabasePath { get; }

    public SharedTranslationMemory(string? databasePath = null, int commandTimeout = 10)
    {
        DatabasePath = Path.GetFullPath(string.IsNullOrWhiteSpace(databasePath)
            ? LocaraPaths.SharedMemoryDatabasePath
            : databasePath);

        string? parentDir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(parentDir))
            Directory.CreateDirectory(parentDir);

        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            DefaultTimeout = commandTimeout
        }.ToString());

        try
        {
            connection.Open();
            Execute($"PRAGMA busy_timeout={Math.Min(commandTimeout, 15) * 1000}; PRAGMA journal_mode=WAL;");
            InitializeSchema();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static SharedTranslationMemory OpenDefault() => new(LocaraPaths.SharedMemoryDatabasePath);

    private void InitializeSchema()
    {
        int version = Query("PRAGMA user_version", r => r.GetInt32(0)).Single();
        if (version == 1) return;

        Execute("BEGIN IMMEDIATE");
        try
        {
            version = Query("PRAGMA user_version", r => r.GetInt32(0)).Single();
            if (version == 0)
            {
                Execute("""
                    CREATE TABLE IF NOT EXISTS shared_tm (
                        source TEXT PRIMARY KEY,
                        target TEXT NOT NULL,
                        use_count INTEGER NOT NULL DEFAULT 1,
                        source_engine TEXT NOT NULL DEFAULT '',
                        updated TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE INDEX IF NOT EXISTS shared_tm_updated ON shared_tm(updated DESC);
                    PRAGMA user_version = 1;
                    """);
            }
            else if (version != 1)
            {
                throw new InvalidDataException($"Unsupported shared translation memory schema version: {version}");
            }
            Execute("COMMIT");
        }
        catch
        {
            Execute("ROLLBACK");
            throw;
        }
    }

    public static bool IsEligible(string? source, string? target, string engine = "")
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return false;

        if (source.Length > MaxSourceLength || target.Length > MaxTargetLength)
            return false;

        int lines = 1;
        foreach (char c in source)
        {
            if (c == '\n' && ++lines > MaxLineCount)
                return false;
        }

        if (Tokens.Validate(source, target, engine).Length != 0)
            return false;

        bool sourceHasLatin = source.Any(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));
        if (sourceHasLatin && !ArabicTextEngine.HasArabic(target))
            return false;

        return true;
    }

    public string? TryLookup(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        return Query("SELECT target FROM shared_tm WHERE source=$s", r => r.GetString(0), ("$s", source))
            .FirstOrDefault();
    }

    public SharedMemoryStats GetStats()
    {
        var row = Query("SELECT COUNT(*), COALESCE(SUM(use_count), 0) FROM shared_tm",
            r => (Count: r.GetInt32(0), Reuses: r.GetInt64(1))).Single();
        return new SharedMemoryStats(row.Count, row.Reuses, DatabasePath);
    }

    public int RecordApproved(IEnumerable<(string Source, string Target)> pairs, string engine = "")
    {
        var eligible = new List<(string Source, string Target)>();
        foreach (var (src, tgt) in pairs)
        {
            if (IsEligible(src, tgt, engine))
                eligible.Add((src, tgt));
        }
        if (eligible.Count == 0) return 0;

        Execute("BEGIN IMMEDIATE");
        try
        {
            foreach (var (src, tgt) in eligible)
            {
                Execute("""
                    INSERT INTO shared_tm(source, target, use_count, source_engine, updated)
                    VALUES($s, $t, 1, $e, CURRENT_TIMESTAMP)
                    ON CONFLICT(source) DO UPDATE SET
                        target = excluded.target,
                        use_count = shared_tm.use_count + 1,
                        source_engine = CASE WHEN excluded.source_engine <> '' THEN excluded.source_engine ELSE shared_tm.source_engine END,
                        updated = CURRENT_TIMESTAMP
                    """, ("$s", src), ("$t", tgt), ("$e", engine ?? ""));
            }
            Execute("COMMIT");
            return eligible.Count;
        }
        catch
        {
            Execute("ROLLBACK");
            throw;
        }
    }

    public int SyncApprovedFromProject(ProjectStore store)
    {
        string engine = new ProjectSettings(store).ScannedEngine;
        var approved = store.Query("""
            SELECT source, target FROM units
            WHERE active=1 AND status='approved' AND warning='' AND target<>''
            """, r => (Source: r.GetString(0), Target: r.GetString(1)));
        return RecordApproved(approved, engine);
    }

    public SharedMemoryPrefillResult PrefillProject(
        ProjectStore store,
        IEnumerable<Unit>? scope = null,
        bool autoApprove = false)
    {
        var pending = (scope ?? store.Units(limit: -1, status: "pending"))
            .Where(u => u.Status == "pending" && !string.IsNullOrWhiteSpace(u.Source))
            .DistinctBy(u => u.Id)
            .ToList();

        if (pending.Count == 0)
            return new(0, 0, 0, 0, autoApprove);

        var memory = LoadAllMap();
        if (memory.Count == 0)
            return new(0, 0, 0, 0, autoApprove);

        string engine = new ProjectSettings(store).ScannedEngine;
        var manualGlossary = store.Glossary();

        var matches = new List<(Unit Unit, string Target)>();
        int skippedTokens = 0;
        int skippedGlossary = 0;

        foreach (var unit in pending)
        {
            if (!memory.TryGetValue(unit.Source, out string? tmTarget) || string.IsNullOrWhiteSpace(tmTarget))
                continue;

            if (Tokens.Validate(unit.Source, tmTarget, engine).Length != 0)
            {
                skippedTokens++;
                continue;
            }

            if (manualGlossary.Count > 0
                && GlossaryConsistencyChecker.CheckUnit(unit.Source, tmTarget, manualGlossary, engine).Count > 0)
            {
                skippedGlossary++;
                continue;
            }

            matches.Add((unit, tmTarget));
        }

        if (matches.Count == 0)
            return new(0, 0, skippedTokens, skippedGlossary, autoApprove);

        var applied = store.ApplyPendingTranslations(matches, autoApprove);
        if (applied.Count == 0)
            return new(0, 0, skippedTokens, skippedGlossary, autoApprove);

        IncrementUseCounts(applied.Select(a => a.Source));
        int savedChars = applied.Sum(a => a.Source.Length);
        return new(applied.Count, savedChars, skippedTokens, skippedGlossary, autoApprove);
    }

    public void Clear() => Execute("DELETE FROM shared_tm");

    private Dictionary<string, string> LoadAllMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT source, target FROM shared_tm";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = reader.GetString(1);
        return map;
    }

    private void IncrementUseCounts(IEnumerable<string> sources)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string s in sources)
            counts[s] = counts.GetValueOrDefault(s) + 1;
        if (counts.Count == 0) return;

        Execute("BEGIN IMMEDIATE");
        try
        {
            foreach (var (src, delta) in counts)
            {
                Execute("UPDATE shared_tm SET use_count = use_count + $d, updated = CURRENT_TIMESTAMP WHERE source = $s",
                    ("$d", delta), ("$s", src));
            }
            Execute("COMMIT");
        }
        catch
        {
            Execute("ROLLBACK");
            throw;
        }
    }

    private void Execute(string sql, params (string Key, object? Value)[] values)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in values)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Key, object? Value)[] values)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in values)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            result.Add(map(reader));
        return result;
    }

    public void Dispose() => connection.Dispose();
}
