using System.Text;

namespace DedupTool;

/// <summary>
/// Writes the load file consumed by the review platform ingest job.
/// Column order is fixed. Do not change without coordinating with the ingest team.
/// </summary>
public static class LoadFileWriter
{
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        // Ingest job expects LF line endings regardless of platform.
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var r in rows)
        {
            sb.Append(Field(r.DocId)).Append(',')
                .Append(Field(r.FamilyId)).Append(',')
                .Append(Field(r.ParentId)).Append(',')
                .Append(Field(r.Custodian)).Append(',')
                .Append(Field(r.DocType)).Append(',')
                .Append(Field(r.FileName)).Append(',')
                .Append(Field(r.SentDate)).Append(',')
                .Append(Field(r.Hash)).Append('\n');
        }
        return sb.ToString();
    }

    public static void Write(string path, IEnumerable<LoadFileRow> rows)
    {
        File.WriteAllText(path, ToCsv(rows));
    }

    /// <summary>
    /// Quote a field only when it contains a comma, quote, or line break.
    /// Internal quotes are doubled. Fields without those characters are unchanged.
    /// </summary>
    private static string Field(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
