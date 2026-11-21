using System.Globalization;
using System.Text;

namespace Locara.Core;

public sealed record ReviewChange(Unit? Row, string Source, string Current, string Proposed, string Issue)
{
    public bool Include { get; set; } = Issue.Length == 0;
}

public sealed class ReviewExchange(ProjectStore store)
{
    private static readonly string[] Header = ["project", "id", "source_hash", "revision", "source", "context", "target"];

    private string ProjectId()
    {
        store.Execute("INSERT OR IGNORE INTO settings(key,value) VALUES('review-project-id',$id)",
            ("$id", Guid.NewGuid().ToString("N")));
        return store.Get("review-project-id");
    }

    public void Export(string file, IEnumerable<Unit> scope)
    {
        string id = ProjectId();
        string path = Path.GetFullPath(file);
        Identity.RequireNoLinks(path);
        string staging = path + ".locara-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var writer = new StreamWriter(staging, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine(string.Join(',', Header));
                foreach (var row in scope)
                    writer.WriteLine(string.Join(',', new[] { id, row.Id, Fingerprint(row.Source),
                        row.Revision.ToString(CultureInfo.InvariantCulture), EscapeFormula(row.Source),
                        EscapeFormula(row.Context), EscapeFormula(row.Target) }.Select(Quote)));
            }
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    public IReadOnlyList<ReviewChange> Preview(string file)
    {
        var rows = DelimitedFormat.Parse(File.ReadAllText(file, Encoding.UTF8), ',');
        if (rows.Count == 0 || !rows[0].SequenceEqual(Header))
            throw new InvalidDataException("اختر ملف مراجعة CSV صادراً من Locara.");
        string id = ProjectId();
        string engine = store.Get("scanned_engine");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<ReviewChange>();
        foreach (var fields in rows.Skip(1))
        {
            if (fields.Count != Header.Length || fields[0] != id || !seen.Add(fields[1])
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long revision))
                throw new InvalidDataException("ملف المراجعة من مشروع مختلف أو يحتوي على صف مكرر أو بيانات تعريف غير صالحة.");
            string proposed = UnescapeFormula(fields[6]);
            var row = store.UnitsById(fields[1]);
            if (string.IsNullOrWhiteSpace(proposed) || row?.Target == proposed) continue;
            string issue = row switch
            {
                null => "السطر غير متاح في المصدر الحالي.",
                _ when Fingerprint(row.Source) != fields[2] || row.Revision != revision =>
                    "تغيّر الأصل أو المسودة منذ تصدير المراجعة.",
                _ => Tokens.Validate(row.Source, proposed, engine)
            };
            changes.Add(new(row, row?.Source ?? UnescapeFormula(fields[4]), row?.Target ?? "", proposed, issue));
        }
        return changes;
    }

    public int Apply(IEnumerable<ReviewChange> changes)
    {
        var edits = new List<(Unit Unit, bool Approve)>();
        foreach (var change in changes.Where(change => change.Include && change.Row != null && change.Issue == ""))
        {
            var snapshot = change.Row!;
            edits.Add((new Unit { Id = snapshot.Id, Locator = snapshot.Locator, Source = snapshot.Source,
                Target = change.Proposed, Revision = snapshot.Revision }, false));
        }
        store.SavePage(edits);
        return edits.Count;
    }

    private static string Fingerprint(string value) => Identity.Hash(Encoding.UTF8.GetBytes(value));
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool Formula(string value) => value.TrimStart() is { Length: > 0 } trimmed
        && trimmed[0] is '=' or '+' or '-' or '@';
    private static string EscapeFormula(string value) => Formula(value) || value.StartsWith('\'') ? "'" + value : value;
    private static string UnescapeFormula(string value) => value.StartsWith('\'') && (value.Length > 1
        && (value[1] == '\'' || Formula(value[1..]))) ? value[1..] : value;
}
