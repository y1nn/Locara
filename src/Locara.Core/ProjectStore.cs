using Microsoft.Data.Sqlite;

namespace Locara.Core;

public sealed class ProjectStore : IDisposable
{
    private readonly SqliteConnection connection;
    public string DirectoryPath { get; }
    public static string ReadName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string db = Path.Combine(path, "project.db");
        if (File.Exists(db))
        {
            try
            {
                var settings = new SqliteConnectionStringBuilder
                { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 };
                using var connection = new SqliteConnection(settings.ToString());
                connection.Open();
                using var query = connection.CreateCommand();
                query.CommandText = "SELECT value FROM settings WHERE key='name'";
                name = query.ExecuteScalar() as string ?? name;
            }
            catch (SqliteException) { }
        }
        return name;
    }

    private const string V3Schema = """
        CREATE INDEX IF NOT EXISTS units_order ON units(active,status,path,locator);
        CREATE INDEX IF NOT EXISTS units_memory ON units(active,status,source,context);
        CREATE VIRTUAL TABLE IF NOT EXISTS units_fts USING fts5(
            source, target, path, context,
            content='units', content_rowid='rowid',
            tokenize='trigram'
        );
        CREATE TRIGGER IF NOT EXISTS units_ai AFTER INSERT ON units BEGIN
            INSERT INTO units_fts(rowid, source, target, path, context)
            VALUES (new.rowid, new.source, new.target, new.path, new.context);
        END;
        CREATE TRIGGER IF NOT EXISTS units_ad AFTER DELETE ON units BEGIN
            INSERT INTO units_fts(units_fts, rowid, source, target, path, context)
            VALUES ('delete', old.rowid, old.source, old.target, old.path, old.context);
        END;
        CREATE TRIGGER IF NOT EXISTS units_au AFTER UPDATE OF source, target, path, context ON units BEGIN
            INSERT INTO units_fts(units_fts, rowid, source, target, path, context)
            VALUES ('delete', old.rowid, old.source, old.target, old.path, old.context);
            INSERT INTO units_fts(rowid, source, target, path, context)
            VALUES (new.rowid, new.source, new.target, new.path, new.context);
        END;
        CREATE TABLE IF NOT EXISTS team_runs(
            id TEXT PRIMARY KEY,
            scope TEXT NOT NULL,
            package TEXT NOT NULL,
            engine TEXT NOT NULL,
            connection TEXT NOT NULL,
            settings_json TEXT NOT NULL,
            prompts_json TEXT NOT NULL,
            glossary_json TEXT NOT NULL,
            workers INTEGER NOT NULL,
            supervisor_inflight INTEGER NOT NULL DEFAULT 0,
            supervisor_sections_json TEXT NOT NULL DEFAULT '',
            supervisor_error TEXT NOT NULL DEFAULT '',
            used_characters INTEGER NOT NULL DEFAULT 0,
            used_requests INTEGER NOT NULL DEFAULT 0,
            pause_reason TEXT NOT NULL DEFAULT '',
            active INTEGER NOT NULL DEFAULT 1,
            created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE INDEX IF NOT EXISTS team_runs_scope ON team_runs(scope, active);
        CREATE TABLE IF NOT EXISTS team_batches(
            run_id TEXT NOT NULL REFERENCES team_runs(id) ON DELETE CASCADE,
            batch_id INTEGER NOT NULL,
            section TEXT NOT NULL,
            rows_json TEXT NOT NULL,
            stage TEXT NOT NULL DEFAULT 'translate',
            in_flight INTEGER NOT NULL DEFAULT 0,
            error TEXT NOT NULL DEFAULT '',
            drafts_json TEXT NOT NULL DEFAULT '',
            reviews_json TEXT NOT NULL DEFAULT '',
            saved_json TEXT NOT NULL DEFAULT '',
            skipped_json TEXT NOT NULL DEFAULT '',
            corrections INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(run_id, batch_id)
        );
        CREATE TABLE IF NOT EXISTS team_notes(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES team_runs(id) ON DELETE CASCADE,
            batch_id INTEGER NOT NULL,
            unit_id TEXT NOT NULL,
            source TEXT NOT NULL,
            draft TEXT NOT NULL DEFAULT '',
            suggestion TEXT NOT NULL DEFAULT '',
            issue TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS team_notes_run ON team_notes(run_id, batch_id);
        """;

    private const string V5Schema = """
        CREATE TABLE IF NOT EXISTS usage_ledger(
            provider TEXT NOT NULL,
            model TEXT NOT NULL,
            requests INTEGER NOT NULL DEFAULT 0,
            input_tokens INTEGER NOT NULL DEFAULT 0,
            output_tokens INTEGER NOT NULL DEFAULT 0,
            thinking_tokens INTEGER NOT NULL DEFAULT 0,
            total_tokens INTEGER NOT NULL DEFAULT 0,
            characters INTEGER NOT NULL DEFAULT 0,
            updated TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY(provider, model)
        );
        """;

    public ProjectStore(string directory) : this(directory, 30) { }

    public ProjectStore(string directory, int commandTimeout)
    {
        DirectoryPath = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(DirectoryPath, "project.db"))) throw new FileNotFoundException("Choose a Locara project folder.");
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "project.db"), DefaultTimeout = commandTimeout
        }.ToString());
        try
        {
            connection.Open();
            Execute($"PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout={Math.Min(commandTimeout, 5) * 1000};");
            if (SchemaVersion() != 5) Upgrade();
        }
        catch { connection.Dispose(); throw; }
    }

    private int SchemaVersion() => Query("PRAGMA user_version", r => r.GetInt32(0)).Single();

    private void Upgrade()
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            int version = SchemaVersion();
            if (version == 1)
            {
                Execute("""
                    ALTER TABLE units ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE units ADD COLUMN imported TEXT NOT NULL DEFAULT '';
                    CREATE TABLE sources(path TEXT PRIMARY KEY COLLATE NOCASE, scope TEXT NOT NULL UNIQUE);
                    INSERT INTO sources SELECT value,'' FROM settings WHERE key='source' AND value<>'';
                    DELETE FROM settings WHERE key='scanned_source';
                    PRAGMA user_version=2;
                    """);
                version = 2;
            }
            if (version == 2)
            {
                Execute(V3Schema);
                Execute("INSERT INTO units_fts(units_fts) VALUES('rebuild');");
                TranslationTeam.MigrateLegacySettings(this);
                Execute("PRAGMA user_version=3;");
                version = 3;
            }
            if (version == 3)
            {
                var columns = Query("PRAGMA table_info(team_runs)", row => row.GetString(1)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!columns.Contains("used_input_tokens"))
                    Execute("ALTER TABLE team_runs ADD COLUMN used_input_tokens INTEGER NOT NULL DEFAULT 0;");
                if (!columns.Contains("used_output_tokens"))
                    Execute("ALTER TABLE team_runs ADD COLUMN used_output_tokens INTEGER NOT NULL DEFAULT 0;");
                if (!columns.Contains("used_total_tokens"))
                    Execute("ALTER TABLE team_runs ADD COLUMN used_total_tokens INTEGER NOT NULL DEFAULT 0;");
                Execute("PRAGMA user_version=4;");
                version = 4;
            }
            if (version == 4)
            {
                Execute(V5Schema);
                SeedUsageLedgerFromHistory();
                Execute("PRAGMA user_version=5;");
                version = 5;
            }
            if (version != 5) throw new InvalidDataException("Unsupported project database version.");
            Execute("COMMIT");
        }
        catch { Execute("ROLLBACK"); throw; }
    }

    private void SeedUsageLedgerFromHistory()
    {
        try
        {
            var fromLog = ModelPricing.ParseLegacyLog(Path.Combine(DirectoryPath, "team-live.log"));
            if (fromLog.Count > 0)
            {
                foreach (var entry in fromLog)
                {
                    var v = entry.Value;
                    Execute("""
                        INSERT INTO usage_ledger(provider,model,requests,input_tokens,output_tokens,thinking_tokens,total_tokens,characters,updated)
                        VALUES($p,$m,$r,$in,$out,$th,$tot,0,CURRENT_TIMESTAMP)
                        ON CONFLICT(provider,model) DO UPDATE SET
                            requests = usage_ledger.requests + excluded.requests,
                            input_tokens = usage_ledger.input_tokens + excluded.input_tokens,
                            output_tokens = usage_ledger.output_tokens + excluded.output_tokens,
                            thinking_tokens = usage_ledger.thinking_tokens + excluded.thinking_tokens,
                            total_tokens = usage_ledger.total_tokens + excluded.total_tokens,
                            updated = CURRENT_TIMESTAMP
                        """, ("$p", "api"), ("$m", entry.Key), ("$r", v.Reqs), ("$in", v.In), ("$out", v.Out), ("$th", v.Think), ("$tot", v.Total));
                }
                return;
            }

            var runs = Query("""
                SELECT connection, used_requests, used_input_tokens, used_output_tokens, used_total_tokens, used_characters
                FROM team_runs WHERE used_total_tokens > 0
                """, row => (
                    Conn: row.GetString(0), Reqs: row.GetInt64(1),
                    In: row.GetInt64(2), Out: row.GetInt64(3), Tot: row.GetInt64(4), Chars: row.GetInt64(5)));
            foreach (var r in runs)
            {
                string[] parts = r.Conn.Split('\n');
                string provider = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "api";
                string model = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "unknown";
                long think = Math.Max(0, r.Tot - r.In - r.Out);
                Execute("""
                    INSERT INTO usage_ledger(provider,model,requests,input_tokens,output_tokens,thinking_tokens,total_tokens,characters,updated)
                    VALUES($p,$m,$r,$in,$out,$th,$tot,$ch,CURRENT_TIMESTAMP)
                    ON CONFLICT(provider,model) DO UPDATE SET
                        requests = usage_ledger.requests + excluded.requests,
                        input_tokens = usage_ledger.input_tokens + excluded.input_tokens,
                        output_tokens = usage_ledger.output_tokens + excluded.output_tokens,
                        thinking_tokens = usage_ledger.thinking_tokens + excluded.thinking_tokens,
                        total_tokens = usage_ledger.total_tokens + excluded.total_tokens,
                        characters = usage_ledger.characters + excluded.characters,
                        updated = CURRENT_TIMESTAMP
                    """, ("$p", provider), ("$m", model), ("$r", r.Reqs), ("$in", r.In), ("$out", r.Out), ("$th", think), ("$tot", r.Tot), ("$ch", r.Chars));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public static ProjectStore Create(string directory, string name, string? source = null, string engine = "auto")
    {
        directory = Path.GetFullPath(directory);
        if (source != null)
        {
            source = Path.GetFullPath(source);
            if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("Source does not exist.", source);
            EnsureSource(directory, source);
        }
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Choose an empty project folder.");
        Directory.CreateDirectory(directory);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "project.db") }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE files(path TEXT PRIMARY KEY,hash TEXT NOT NULL,format TEXT NOT NULL,snapshot BLOB NOT NULL,active INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE units(id TEXT PRIMARY KEY,path TEXT NOT NULL,locator TEXT NOT NULL,source TEXT NOT NULL,target TEXT NOT NULL DEFAULT '',context TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'pending',warning TEXT NOT NULL DEFAULT '',active INTEGER NOT NULL DEFAULT 1,revision INTEGER NOT NULL DEFAULT 0,imported TEXT NOT NULL DEFAULT '');
                CREATE INDEX review ON units(active,status,path,id);
                CREATE TABLE glossary(source TEXT PRIMARY KEY,target TEXT NOT NULL);
                CREATE TABLE attempts(id INTEGER PRIMARY KEY,unit_id TEXT NOT NULL,provider TEXT NOT NULL,state TEXT NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE sources(path TEXT PRIMARY KEY COLLATE NOCASE, scope TEXT NOT NULL UNIQUE);
                """ + "\n" + V3Schema + "\n" + V5Schema + "\n"
                + """
                ALTER TABLE team_runs ADD COLUMN used_input_tokens INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE team_runs ADD COLUMN used_output_tokens INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE team_runs ADD COLUMN used_total_tokens INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version=5;
                """;
            cmd.ExecuteNonQuery();
        }
        var store = new ProjectStore(directory);
        store.Set("name", name);
        if (source != null) store.ChangeSource(source);
        store.Set("engine", engine);
        return store;
    }
    private static void EnsureSource(string directory, string source)
    {
        Identity.RequireNoLinks(directory);
        Identity.RequireNoLinks(source);
        if (Identity.Within(directory, source) || Identity.Within(source, directory))
            throw new ArgumentException("Choose a project workspace outside the source tree.");
    }
    public void ChangeSource(string source, bool relocated = false, string? workflowOrigin = null)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        if (workflowOrigin != null) workflowOrigin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workflowOrigin));
        if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("Source does not exist.", source);
        EnsureSource(DirectoryPath, source);
        Execute("BEGIN IMMEDIATE");
        try
        {
            string old = Get("source");
            if (workflowOrigin != null && !StringComparer.OrdinalIgnoreCase.Equals(old, workflowOrigin))
                throw new InvalidOperationException("تغيّر مصدر المشروع أثناء إعداد نسخة Ren’Py.");
            if (!StringComparer.OrdinalIgnoreCase.Equals(old, source))
            {
                if (relocated)
                {
                    if (old.Length == 0) throw new InvalidOperationException("Select a source before relocating it.");
                    if (Query("SELECT 1 FROM sources WHERE path=$p", r => r.GetInt32(0), ("$p", source)).Count > 0)
                        throw new InvalidOperationException("هذا المسار مرتبط بمصدر آخر في المشروع. افتحه كمصدر مستقل.");
                    Execute("UPDATE sources SET path=$p WHERE path=$old", ("$p", source), ("$old", old));
                }
                else
                    Execute("INSERT OR IGNORE INTO sources(path,scope) VALUES($p,$scope)", ("$p", source), ("$scope", Guid.NewGuid().ToString("N")));
                Set("source_scope", Query("SELECT scope FROM sources WHERE path=$p", r => r.GetString(0), ("$p", source)).Single());
                Set("source", source);
                if (workflowOrigin != null) Set("renpy-origin-for:" + source, workflowOrigin);
                Set("scanned_source", "");
                Set("scan_version", Guid.NewGuid().ToString("N"));
                Execute("UPDATE files SET active=0 WHERE active=1; UPDATE units SET active=0,revision=revision+1 WHERE active IN (1,2)");
            }
            Execute("COMMIT");
        }
        catch { Execute("ROLLBACK"); throw; }
    }
    internal void Execute(string sql, params (string Key, object? Value)[] values)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    internal List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Key, object? Value)[] values)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }
    public string Get(string key) => Query("SELECT value FROM settings WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault() ?? "";
    public void Set(string key, string value) => Execute("INSERT INTO settings VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    public ProjectInfo Info => new(Get("name"), Get("source"), Get("engine"));
    public string LastOutput
    {
        get => Get("last_output:" + Get("source_scope"));
        set => Set("last_output:" + Get("source_scope"), value);
    }
    public IReadOnlyDictionary<string, int> ReviewCounts() => Query("""
        SELECT COUNT(*), COUNT(*) FILTER (WHERE status='pending'),
               COUNT(*) FILTER (WHERE status IN ('translated','imported') AND warning=''),
               COUNT(*) FILTER (WHERE status='conflict' OR warning<>''), COUNT(*) FILTER (WHERE status='approved')
        FROM units WHERE active=1
        """, row => new Dictionary<string, int>
        {
            ["total"] = row.GetInt32(0), ["pending"] = row.GetInt32(1), ["review"] = row.GetInt32(2),
            ["issues"] = row.GetInt32(3), ["approved"] = row.GetInt32(4)
        }).Single();

    public List<Unit> Units(string search = "", int offset = 0, int limit = 100, string status = "")
    {
        if (string.IsNullOrEmpty(search))
            return Query("""
                SELECT id,path,locator,source,target,context,status,warning,revision,imported FROM units
                WHERE active=1
                AND ($status='' OR status=$status OR ($status='issues' AND (status='conflict' OR warning<>''))
                     OR ($status='review' AND status IN ('translated','imported') AND warning=''))
                ORDER BY path,locator LIMIT $n OFFSET $o
                """, ReadUnit, ("$n", limit), ("$o", offset), ("$status", status));

        if (search.EnumerateRunes().Skip(2).Any())
        {
            string fts = "\"" + search.Replace("\"", "\"\"") + "\"";
            return Query("""
                SELECT id,path,locator,source,target,context,status,warning,revision,imported FROM units
                WHERE active=1 AND (id=$id OR rowid IN (SELECT rowid FROM units_fts WHERE units_fts MATCH $fts))
                AND ($status='' OR status=$status OR ($status='issues' AND (status='conflict' OR warning<>''))
                     OR ($status='review' AND status IN ('translated','imported') AND warning=''))
                ORDER BY path,locator LIMIT $n OFFSET $o
                """, ReadUnit, ("$id", search), ("$fts", fts), ("$n", limit), ("$o", offset), ("$status", status));
        }

        return Query("""
            SELECT id,path,locator,source,target,context,status,warning,revision,imported FROM units
            WHERE active=1 AND (source LIKE $s OR target LIKE $s OR path LIKE $s OR context LIKE $s OR id=$id)
            AND ($status='' OR status=$status OR ($status='issues' AND (status='conflict' OR warning<>''))
                 OR ($status='review' AND status IN ('translated','imported') AND warning=''))
            ORDER BY path,locator LIMIT $n OFFSET $o
            """, ReadUnit, ("$id", search), ("$s", "%" + search + "%"), ("$n", limit), ("$o", offset), ("$status", status));
    }
    public Unit FindUnit(string id) => UnitsById(id) ?? throw new InvalidOperationException("Text unit not found.");
    internal static Unit ReadUnit(SqliteDataReader r) => new() { Id = r.GetString(0), Path = r.GetString(1), Locator = r.GetString(2), Source = r.GetString(3), Target = r.GetString(4), Context = r.GetString(5), Status = r.GetString(6), Warning = r.GetString(7), Revision = r.GetInt64(8), ImportedTarget = r.GetString(9) };
    public void Save(Unit unit, bool approve) => Save(unit, approve, false);

    public void SavePage(IReadOnlyList<(Unit Unit, bool Approve)> edits)
    {
        if (edits.Count == 0) return;
        Execute("BEGIN IMMEDIATE");
        try
        {
            var updates = edits.Select(edit => WriteEdit(edit.Unit, edit.Approve, false)).ToList();
            Execute("COMMIT");
            foreach (var update in updates) update();
        }
        catch { Execute("ROLLBACK"); throw; }
    }

    public int ApproveAllValidDrafts()
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            string engine = Get("scanned_engine");
            var candidates = Query("""
                SELECT id,path,locator,source,target,context,status,warning,revision,imported
                FROM units WHERE active=1 AND status IN ('translated','imported') AND target<>'' AND warning=''
                """, ReadUnit);
            int approved = 0;
            foreach (var unit in candidates)
            {
                if (Tokens.Validate(unit.Source, unit.Target, engine).Length != 0) continue;
                Execute("""
                    UPDATE units SET status='approved', revision=revision+1
                    WHERE id=$i AND revision=$rev AND active=1 AND status IN ('translated','imported')
                    """, ("$i", unit.Id), ("$rev", unit.Revision));
                approved += Query("SELECT changes()", r => r.GetInt32(0))[0];
            }
            Execute("COMMIT");
            return approved;
        }
        catch { Execute("ROLLBACK"); throw; }
    }

    internal void Save(Unit unit, bool approve, bool pendingOnly) => WriteEdit(unit, approve, pendingOnly)();

    private Action WriteEdit(Unit unit, bool approve, bool pendingOnly)
    {
        string warning = Tokens.Validate(unit.Source, unit.Target, Get("scanned_engine"));
        bool blankAllowed = string.IsNullOrWhiteSpace(unit.Source) && unit.Target == unit.Source;
        if (approve && (warning.Length > 0 || (string.IsNullOrWhiteSpace(unit.Target) && !blankAllowed))) throw new InvalidDataException("تعذّر الاعتماد. " + warning);
        string status = approve ? "approved" : (string.IsNullOrWhiteSpace(unit.Target) && !blankAllowed) ? "pending" : "translated";
        Execute("""
            UPDATE units SET target=$t,status=$s,warning=$w,revision=revision+1
            WHERE id=$i AND revision=$rev AND source=$o AND active=1 AND ($pending=0 OR status='pending')
            """, ("$t", unit.Target), ("$s", status), ("$w", warning), ("$i", unit.Id), ("$rev", unit.Revision), ("$o", unit.Source), ("$pending", pendingOnly));
        if (Query("SELECT changes()", r => r.GetInt32(0))[0] != 1)
            throw new EditConflictException(unit, UnitsById(unit.Id));
        return () => { unit.Revision++; unit.Status = status; unit.Warning = warning; };
    }

    internal Unit? UnitsById(string id) => Query("""
        SELECT id,path,locator,source,target,context,status,warning,revision,imported FROM units WHERE id=$i AND active=1
        """, ReadUnit, ("$i", id)).SingleOrDefault();
    public void Glossary(string source, string target) => Execute("INSERT INTO glossary VALUES($s,$t) ON CONFLICT(source) DO UPDATE SET target=excluded.target", ("$s", source), ("$t", target));
    public Dictionary<string,string> Glossary() => Query("SELECT source,target FROM glossary", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
    public void RemoveTerm(string source) => Execute("DELETE FROM glossary WHERE source=$s", ("$s", source));

    public List<Unit> MemoryFor(Unit unit) => Query("""
        SELECT id,path,locator,source,target,context,status,warning,revision,imported FROM units
        WHERE active=1 AND status='approved' AND warning='' AND source=$source AND id<>$id
        ORDER BY context=$context DESC, path, locator LIMIT 12
        """, ReadUnit, ("$source", unit.Source), ("$id", unit.Id), ("$context", unit.Context));

    public void RecordUsage(string provider, string model, TokenUsage tokens, int characters = 0, int requests = 1)
    {
        if (requests <= 0 && characters <= 0 && tokens.InputTokens == 0 && tokens.OutputTokens == 0 && tokens.TotalTokens == 0)
            return;
        string p = string.IsNullOrWhiteSpace(provider) ? "api" : provider.Trim();
        string m = string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim();
        long total = tokens.TotalTokens > 0 ? tokens.TotalTokens : tokens.InputTokens + tokens.OutputTokens;
        long thinking = Math.Max(0, total - tokens.InputTokens - tokens.OutputTokens);
        Execute("""
            INSERT INTO usage_ledger(provider,model,requests,input_tokens,output_tokens,thinking_tokens,total_tokens,characters,updated)
            VALUES($p,$m,$r,$in,$out,$th,$tot,$ch,CURRENT_TIMESTAMP)
            ON CONFLICT(provider,model) DO UPDATE SET
                requests = usage_ledger.requests + excluded.requests,
                input_tokens = usage_ledger.input_tokens + excluded.input_tokens,
                output_tokens = usage_ledger.output_tokens + excluded.output_tokens,
                thinking_tokens = usage_ledger.thinking_tokens + excluded.thinking_tokens,
                total_tokens = usage_ledger.total_tokens + excluded.total_tokens,
                characters = usage_ledger.characters + excluded.characters,
                updated = CURRENT_TIMESTAMP
            """, ("$p", p), ("$m", m), ("$r", Math.Max(0, requests)), ("$in", Math.Max(0, tokens.InputTokens)),
            ("$out", Math.Max(0, tokens.OutputTokens)), ("$th", thinking), ("$tot", Math.Max(0, total)),
            ("$ch", Math.Max(0, characters)));
    }

    public List<ModelUsageEntry> ProjectUsage() => Query("""
        SELECT provider, model, requests, input_tokens, output_tokens, thinking_tokens, total_tokens, characters
        FROM usage_ledger
        ORDER BY total_tokens DESC, requests DESC, model ASC
        """, r => new ModelUsageEntry(
            r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3),
            r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7)));

    public void ClearUsageLedger() => Execute("DELETE FROM usage_ledger");

    public void Backup(string destination)
    {
        destination = Path.GetFullPath(destination);
        EnsureSource(destination, Info.Source.Length > 0 ? Info.Source : DirectoryPath);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("اختر اسماً جديداً لمجلد النسخة الاحتياطية.");
        string staging = destination + ".backup-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var options = new SqliteConnectionStringBuilder { DataSource = Path.Combine(staging, "project.db"), Pooling = false };
            using (var copy = new SqliteConnection(options.ToString()))
            {
                copy.Open();
                connection.BackupDatabase(copy);
                using var check = copy.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                if ((string?)check.ExecuteScalar() != "ok") throw new InvalidDataException("تعذّر التحقق من نسخة المشروع.");
            }
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
    public void Dispose() => connection.Dispose();
}

public sealed class EditConflictException(Unit draft, Unit? current)
    : InvalidOperationException($"تغيّر السطر {draft.Locator}. المسودة: {draft.Target} · المحفوظ: {current?.Target ?? "السطر غير متاح"}")
{
    public Unit Draft { get; } = draft;
    public Unit? Current { get; } = current;
}
