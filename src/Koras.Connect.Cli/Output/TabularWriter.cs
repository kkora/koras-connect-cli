using System.Text.Json;
using Koras.Connect.Cli.Cli;
using Koras.Connect.Export;

namespace Koras.Connect.Cli.Output;

public static class ExportFormats
{
    public static readonly IReadOnlyList<string> Tabular = ["csv", "json", "xml", "xlsx"];
    public static string Normalize(string? format)
    {
        var f = (format ?? "csv").Trim().ToLowerInvariant();
        if (f == "excel") f = "xlsx";
        if (!Tabular.Contains(f)) throw new UsageException($"Unsupported --format '{format}'. Supported: {string.Join(", ", Tabular)}.");
        return f;
    }
}

/// <summary>Rows with explicit, caller-selected columns. Values are strings (null stays null).</summary>
public sealed record TabularData(IReadOnlyList<string> Columns, IAsyncEnumerable<IReadOnlyList<string?>> Rows);

/// <summary>Writes tabular data through the SDK's exporters. JSON is written here from the projected rows because the
/// SDK's generic JsonExportProvider serializes raw records and ignores the selected columns.</summary>
public static class TabularWriter
{
    public static async Task<long> WriteAsync(string format, Stream output, TabularData data, CancellationToken ct)
    {
        long count = 0;
        async IAsyncEnumerable<IReadOnlyList<string?>> Counted([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var row in data.Rows.WithCancellation(token)) { count++; yield return row; }
        }
        switch (format)
        {
            case "csv": await TabularExport.WriteCsvAsync(output, data.Columns, Counted(ct), ct); break;
            case "xml": await TabularExport.WriteXmlAsync(output, data.Columns, Counted(ct), ct); break;
            case "xlsx": await TabularExport.WriteExcelAsync(output, data.Columns, Counted(ct), ct); break;
            case "json": await WriteJsonAsync(output, data.Columns, Counted(ct), ct); break;
            default: throw new UsageException($"Unsupported format '{format}'.");
        }
        await output.FlushAsync(ct);
        return count;
    }

    private static async Task WriteJsonAsync(Stream output, IReadOnlyList<string> columns, IAsyncEnumerable<IReadOnlyList<string?>> rows, CancellationToken ct)
    {
        await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartArray();
        var n = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            if (row.Count != columns.Count) throw new ArgumentException("Row width must match columns.");
            writer.WriteStartObject();
            for (var i = 0; i < columns.Count; i++)
            { if (row[i] is null) writer.WriteNull(columns[i]); else writer.WriteString(columns[i], row[i]); }
            writer.WriteEndObject();
            if (++n % 500 == 0) await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
        await writer.FlushAsync(ct);
    }
}
