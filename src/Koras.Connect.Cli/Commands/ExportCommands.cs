using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Koras.Connect.Abstractions;
using Koras.Connect.Adp.Export;
using Koras.Connect.Adp.Workers;
using Koras.Connect.Cli.Checkpoints;
using Koras.Connect.Cli.Cli;
using Koras.Connect.Cli.Connectors;
using Koras.Connect.Cli.Output;
using Koras.Connect.Dataverse;
using Koras.Connect.Microsoft.Graph;
using Koras.Connect.Qualtrics;
using Koras.Connect.Sap.SuccessFactors;

namespace Koras.Connect.Cli.Commands;

public static partial class ExportCommands
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")] private static partial Regex Identifier();
    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")] private static partial Regex SafeId();

    private sealed record Common(string Format, ExportDestination Destination, string ProfileName);

    private static Common ParseCommon(CommandContext c, string connector)
    {
        var format = ExportFormats.Normalize(c.Args.Get("format"));
        var destination = ExportDestination.Resolve(c.Args.Get("output"), c.Args.Flag("overwrite"), format);
        var profile = c.LoadProfile(connector);
        return new Common(format, destination, profile.Name);
    }

    private static CheckpointScope Scope(CommandContext c, string connector, string profile)
        => CheckpointScope.Create(connector, profile, c.Args.Snapshot("output", "format", "overwrite", "reset-checkpoint", "incremental", "profile", "page-size", "help"));

    // ---------- ADP (CLI-004) ----------
    public static async Task<int> AdpWorkers(CommandContext c)
    {
        var common = ParseCommon(c, "adp");
        var fields = FieldList.Parse(c.Args.Get("fields"));
        var asOf = Args.Date(c.Args, "as-of");
        c.Args.RejectUnknown();
        var unknown = fields.Where(f => !WorkerExportProjection.AllColumnNames.Contains(f)).ToArray();
        if (unknown.Length > 0)
            throw new UsageException($"Unknown ADP worker field(s): {string.Join(", ", unknown)}. Available: {string.Join(", ", WorkerExportProjection.AllColumnNames)}.");
        var schema = WorkerExportProjection.CreateSchema(fields, asOf);
        using var ctx = c.Connect(common.ProfileName);
        var client = ctx.Adp();
        async IAsyncEnumerable<IReadOnlyList<string?>> Rows([EnumeratorCancellation] CancellationToken ct = default)
        { await foreach (var w in client.EnumerateTypedWorkersAsync(ct)) yield return schema.Project(w); }
        var data = new TabularData(schema.ColumnNames, Rows(c.Cancellation));
        c.Report(await ExportRunner.RunAsync(c.Env, common.Format, common.Destination, data, c.Cancellation));
        return ExitCodes.Success;
    }

    // ---------- SAP (CLI-005) ----------
    public static async Task<int> SapPersons(CommandContext c)
    {
        var common = ParseCommon(c, "sap");
        var fields = FieldList.Parse(c.Args.Get("fields"));
        var asOf = Args.Date(c.Args, "as-of"); var from = Args.Date(c.Args, "from"); var to = Args.Date(c.Args, "to");
        var filter = c.Args.Get("filter");
        c.Args.RejectUnknown();
        if (asOf is not null && (from is not null || to is not null)) throw new UsageException("--as-of cannot be combined with --from/--to.");
        if (from > to) throw new UsageException("--from must not be after --to.");
        using var ctx = c.Connect(common.ProfileName);
        var client = ctx.Sap();
        var query = new ODataQuery(Filter: filter, Select: string.Join(",", fields), AsOfDate: asOf, FromDate: from, ToDate: to);
        var data = new TabularData(fields, JsonRows.Project(client.EnumerateAsync<JsonElement>("PerPerson", query, c.Cancellation), fields, c.Cancellation));
        c.Report(await ExportRunner.RunAsync(c.Env, common.Format, common.Destination, data, c.Cancellation));
        return ExitCodes.Success;
    }

    // ---------- Qualtrics (CLI-006) ----------
    public static readonly IReadOnlyList<string> QualtricsFormats = ["csv", "tsv", "json", "ndjson", "xml", "spss"];

    public static async Task<int> QualtricsResponses(CommandContext c)
    {
        var survey = c.Args.Require("survey");
        if (!SafeId().IsMatch(survey)) throw new UsageException("--survey must be a Qualtrics survey id such as SV_abc123.");
        var progressId = c.Args.Get("progress-id");
        if (progressId is not null && !SafeId().IsMatch(progressId)) throw new UsageException("--progress-id is not a valid Qualtrics progress id.");
        var format = (c.Args.Get("format") ?? "csv").ToLowerInvariant();
        if (!QualtricsFormats.Contains(format)) throw new UsageException($"Unsupported Qualtrics export format '{format}'. Supported: {string.Join(", ", QualtricsFormats)}.");
        var compress = !c.Args.Flag("no-compress");
        var filterId = c.Args.Get("filter-id");
        var start = Args.Timestamp(c.Args, "start-date"); var end = Args.Timestamp(c.Args, "end-date");
        var pollSeconds = c.Args.Get("poll-interval") is { } p ? (double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0 && s <= 300 ? s : throw new UsageException("--poll-interval must be 0-300 seconds.")) : 2.0;
        var maxPolls = Args.PositiveInt(c.Args, "max-polls", 10000) ?? 120;
        // Vendor download bytes are written as-is; a ZIP stays a ZIP. "xlsx" is not a Qualtrics export format.
        var destination = ExportDestination.Resolve(c.Args.Get("output"), c.Args.Flag("overwrite"), "vendor");
        var profile = c.LoadProfile("qualtrics");
        c.Args.RejectUnknown();
        if (start > end) throw new UsageException("--start-date must not be after --end-date.");
        using var ctx = c.Connect(profile.Name);
        var client = ctx.Qualtrics();
        long bytes = 0;
        var publish = await destination.PublishAsync(async stream =>
        {
            if (progressId is null)
            {
                progressId = await client.StartResponseExportAsync(survey, new ResponseExportOptions(format, compress, filterId, start, end), c.Cancellation);
                c.Env.Error.WriteLine($"progress-id: {progressId}");
            }
            else c.Env.Error.WriteLine($"Resuming export {progressId}; no new export started.");
            ExportProgress ready;
            try { ready = await client.WaitForExportAsync(survey, progressId, TimeSpan.FromSeconds(pollSeconds), maxPolls, c.Cancellation); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { c.Env.Error.WriteLine($"Export not downloaded. Resume with: --progress-id {progressId}"); throw; }
            await client.DownloadExportAsync(survey, ready.FileId!, stream, c.Cancellation);
            bytes = stream.Length;
        }, c.Env.OpenStandardOutput, c.Cancellation);
        c.Env.Error.WriteLine(publish.Path is null ? $"Downloaded {bytes} bytes to stdout (format: {format}{(compress ? ", compressed" : "")})." : $"Downloaded {bytes} bytes to {publish.Path} (format: {format}{(compress ? ", compressed" : "")}).");
        if (publish.Path is not null) c.Env.Out.WriteLine(publish.Path);
        return ExitCodes.Success;
    }
}
