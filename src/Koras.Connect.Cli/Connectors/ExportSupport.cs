using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Koras.Connect.Cli.Checkpoints;
using Koras.Connect.Cli.Cli;
using Koras.Connect.Cli.Output;

namespace Koras.Connect.Cli.Connectors;

public static class FieldList
{
    private static readonly Regex Name = new(@"^[A-Za-z_][A-Za-z0-9_./]*$", RegexOptions.Compiled);

    /// <summary>Parses <c>--fields a,b,c</c>. Selection is always explicit; there is no "all fields" default.</summary>
    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new UsageException("Missing required option --fields (explicit comma-separated field selection).");
        var fields = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0) throw new UsageException("--fields must name at least one field.");
        foreach (var f in fields) if (!Name.IsMatch(f)) throw new UsageException($"Invalid field name '{f}'. Use letters, digits, '_', '.' or '/'.");
        var dup = fields.GroupBy(f => f, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new UsageException($"Field '{dup.Key}' was selected more than once.");
        return fields;
    }
}

public static class Args
{
    public static DateOnly? Date(ParsedArgs args, string name)
    {
        var v = args.Get(name);
        if (v is null) return null;
        return DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new UsageException($"--{name} must be a date like 2025-01-31.");
    }
    public static DateTimeOffset? Timestamp(ParsedArgs args, string name)
    {
        var v = args.Get(name);
        if (v is null) return null;
        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : throw new UsageException($"--{name} must be an ISO-8601 timestamp.");
    }
    public static int? PositiveInt(ParsedArgs args, string name, int max = int.MaxValue)
    {
        var v = args.Get(name);
        if (v is null) return null;
        return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= max ? n : throw new UsageException($"--{name} must be an integer between 1 and {max}.");
    }
}

public static class JsonRows
{
    public static string? Cell(JsonElement item, string field)
    {
        var current = item;
        foreach (var part in field.Split('/'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        }
        return current.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => current.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => current.GetRawText(),
            _ => current.GetRawText(),
        };
    }

    public static IReadOnlyList<string?> Row(JsonElement item, IReadOnlyList<string> fields)
    {
        var row = new string?[fields.Count];
        for (var i = 0; i < fields.Count; i++) row[i] = Cell(item, fields[i]);
        return row;
    }

    public static async IAsyncEnumerable<IReadOnlyList<string?>> Project(IAsyncEnumerable<JsonElement> items, IReadOnlyList<string> fields,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in items.WithCancellation(ct)) yield return Row(item, fields);
    }
}

/// <summary>Incremental source: yields rows and, once fully enumerated, the checkpoint value to commit (null = unchanged).</summary>
public sealed class IncrementalSource
{
    public required TabularData Data { get; init; }
    /// <summary>Valid only after <see cref="TabularData.Rows"/> has been enumerated to completion.</summary>
    public required Func<string?> NextCheckpoint { get; init; }
}

public sealed record ExportOutcome(string? Path, long Records, long Bytes);

/// <summary>Shared run loop: validate destination first, then fetch, publish, and (for incremental runs) commit the checkpoint.</summary>
public static class ExportRunner
{
    public static async Task<ExportOutcome> RunAsync(CliEnvironment env, string format, ExportDestination destination, TabularData data, CancellationToken ct)
    {
        long records = 0;
        var publish = await destination.PublishAsync(async stream => records = await TabularWriter.WriteAsync(format, stream, data, ct), env.OpenStandardOutput, ct);
        return new ExportOutcome(publish.Path, records, publish.Bytes);
    }

    public static async Task<ExportOutcome> RunIncrementalAsync(CliEnvironment env, ICheckpointStore store, CheckpointScope scope, bool reset, string format,
        ExportDestination destination, Func<string?, IncrementalSource> open, CancellationToken ct)
    {
        using var lease = store.Acquire(scope);
        if (reset && store.Delete(scope)) env.Error.WriteLine("Checkpoint reset; running a full export.");
        var previous = store.Load(scope)?.Value;
        env.Error.WriteLine(previous is null ? "No checkpoint found; running an initial full export." : "Resuming from saved checkpoint.");
        var source = open(previous);
        long records = 0;
        var publish = await destination.PublishAsync(async stream => records = await TabularWriter.WriteAsync(format, stream, source.Data, ct), env.OpenStandardOutput, ct);
        var next = source.NextCheckpoint();
        if (next is not null)
        {
            try { store.Save(scope, next, DateTimeOffset.UtcNow); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CheckpointSaveException("The export file was published but the checkpoint could not be saved. The previous checkpoint is unchanged, so the next run will " +
                    "replay the changes included in this file (duplicates are possible). Fix the checkpoint location and re-run.", ex);
            }
        }
        return new ExportOutcome(publish.Path, records, publish.Bytes);
    }
}
