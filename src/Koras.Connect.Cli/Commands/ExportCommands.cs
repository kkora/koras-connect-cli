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

    // ---------- Dataverse (CLI-008) ----------
    public static async Task<int> DataverseRows(CommandContext c)
    {
        var common = ParseCommon(c, "dataverse");
        var table = c.Args.Require("table");
        if (!Identifier().IsMatch(table)) throw new UsageException("--table must be the entity set name, for example 'accounts'.");
        var fields = FieldList.Parse(c.Args.Get("fields"));
        var filter = c.Args.Get("filter"); var pageSize = Args.PositiveInt(c.Args, "page-size", 5000);
        var incremental = c.Args.Flag("incremental"); var reset = c.Args.Flag("reset-checkpoint");
        var scope = Scope(c, "dataverse", common.ProfileName);
        c.Args.RejectUnknown();
        if (incremental && filter is not null) throw new UsageException("--filter cannot be combined with --incremental (Dataverse change tracking does not support $filter).");
        if ((reset) && !incremental) throw new UsageException("--reset-checkpoint requires --incremental.");
        using var ctx = c.Connect(common.ProfileName);
        var client = ctx.Dataverse();
        if (!incremental)
        {
            var query = new DataverseQuery(Select: string.Join(",", fields), Filter: filter);
            var data = new TabularData(fields, JsonRows.Project(client.EnumerateAsync(table, query, pageSize, false, c.Cancellation), fields, c.Cancellation));
            c.Report(await ExportRunner.RunAsync(c.Env, common.Format, common.Destination, data, c.Cancellation));
            return ExitCodes.Success;
        }
        var columns = new[] { "_change", "_id" }.Concat(fields).ToArray();
        var store = c.Checkpoints;
        c.Report(await ExportRunner.RunIncrementalAsync(c.Env, store, scope, reset, common.Format, common.Destination, previous =>
        {
            string? next = null;
            async IAsyncEnumerable<IReadOnlyList<string?>> Rows([EnumeratorCancellation] CancellationToken ct = default)
            {
                ContinuationToken? resume = previous is null ? null : new ContinuationToken("Dataverse", table, previous);
                for (var page = 0; page < 100000; page++)
                {
                    var result = await client.GetChangesAsync(table, string.Join(",", fields), resume, ct);
                    foreach (var row in result.Changed) yield return new[] { "changed", null }.Concat(JsonRows.Row(row, fields)).ToArray();
                    foreach (var gone in result.Deleted) yield return new[] { "deleted", gone.Id.ToString() }.Concat(fields.Select(_ => (string?)null)).ToArray();
                    if (result.Next is null) yield break;
                    // Dataverse returns @odata.nextLink (carrying $skiptoken) until the final page, which carries @odata.deltaLink ($deltatoken).
                    if (result.Next.OpaqueValue.Contains("deltatoken", StringComparison.OrdinalIgnoreCase)) { next = result.Next.OpaqueValue; yield break; }
                    resume = result.Next;
                }
                throw new InvalidDataException("Dataverse change pagination limit exceeded.");
            }
            return new IncrementalSource { Data = new TabularData(columns, Rows(c.Cancellation)), NextCheckpoint = () => next };
        }, c.Cancellation));
        return ExitCodes.Success;
    }

    // ---------- Salesforce (CLI-008) ----------
    public static async Task<int> SalesforceQuery(CommandContext c)
    {
        var common = ParseCommon(c, "salesforce");
        var soql = c.Args.Require("soql");
        var fields = FieldList.Parse(c.Args.Get("fields"));
        var batch = Args.PositiveInt(c.Args, "page-size", 2000);
        var includeDeleted = c.Args.Flag("all");
        c.Args.RejectUnknown();
        if (!soql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) throw new UsageException("--soql must be a SELECT statement.");
        using var ctx = c.Connect(common.ProfileName);
        var client = ctx.Salesforce();
        var data = new TabularData(fields, JsonRows.Project(client.EnumerateQueryAsync<JsonElement>(soql, includeDeleted, batch, c.Cancellation), fields, c.Cancellation));
        c.Report(await ExportRunner.RunAsync(c.Env, common.Format, common.Destination, data, c.Cancellation));
        return ExitCodes.Success;
    }

    public static async Task<int> SalesforceChanges(CommandContext c)
    {
        var common = ParseCommon(c, "salesforce");
        var sobject = c.Args.Require("object");
        if (!Identifier().IsMatch(sobject.Replace("__", "_"))) throw new UsageException("--object must be an sObject API name such as Account or Custom__c.");
        var fields = FieldList.Parse(c.Args.Get("fields"));
        var reset = c.Args.Flag("reset-checkpoint");
        var scope = Scope(c, "salesforce", common.ProfileName);
        c.Args.RejectUnknown();
        if (fields.Any(f => f.Contains('/') )) throw new UsageException("Relationship paths are not supported in incremental Salesforce exports.");
        using var ctx = c.Connect(common.ProfileName);
        var client = ctx.Salesforce();
        c.Report(await ExportRunner.RunIncrementalAsync(c.Env, c.Checkpoints, scope, reset, common.Format, common.Destination, previous =>
        {
            DateTimeOffset max = previous is null ? DateTimeOffset.MinValue : DateTimeOffset.Parse(previous, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            var seen = false;
            var selectFields = fields.Contains("SystemModstamp") ? fields : [.. fields, "SystemModstamp"];
            IAsyncEnumerable<JsonElement> records = previous is null
                ? client.EnumerateQueryAsync<JsonElement>($"SELECT {string.Join(",", selectFields)} FROM {sobject} ORDER BY SystemModstamp", false, null, c.Cancellation)
                : client.EnumerateChangesAsync<JsonElement>(sobject, new Koras.Connect.Salesforce.SalesforceDeltaCheckpoint(max), fields, c.Cancellation);
            async IAsyncEnumerable<IReadOnlyList<string?>> Rows([EnumeratorCancellation] CancellationToken ct = default)
            {
                await foreach (var r in records.WithCancellation(ct))
                {
                    var stamp = JsonRows.Cell(r, "SystemModstamp") ?? throw new InvalidDataException("A Salesforce record did not include SystemModstamp.");
                    var parsed = ParseSalesforceTimestamp(stamp);
                    if (parsed > max) max = parsed;
                    seen = true;
                    yield return JsonRows.Row(r, fields);
                }
            }
            return new IncrementalSource { Data = new TabularData(fields, Rows(c.Cancellation)), NextCheckpoint = () => seen ? max.ToString("o", System.Globalization.CultureInfo.InvariantCulture) : (previous is null ? null : previous) };
        }, c.Cancellation));
        return ExitCodes.Success;
    }

    internal static DateTimeOffset ParseSalesforceTimestamp(string stamp)
    {
        // Salesforce renders offsets as +0000 rather than +00:00.
        var normalized = System.Text.RegularExpressions.Regex.Replace(stamp, @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToUniversalTime() : throw new InvalidDataException("A Salesforce SystemModstamp value could not be parsed.");
    }

    // ---------- Microsoft Graph (CLI-008) ----------
    public static Task<int> GraphUsers(CommandContext c) => GraphCollection(c, "users");
    public static Task<int> GraphGroups(CommandContext c) => GraphCollection(c, "groups");

    private static async Task<int> GraphCollection(CommandContext c, string collection)
    {
        var common = ParseCommon(c, "graph");
        var fields = FieldList.Parse(c.Args.Get("fields"));
        var filter = c.Args.Get("filter"); var pageSize = Args.PositiveInt(c.Args, "page-size", 999);
        var incremental = c.Args.Flag("incremental"); var reset = c.Args.Flag("reset-checkpoint");
        var scope = Scope(c, "graph", common.ProfileName);
        c.Args.RejectUnknown();
        if (reset && !incremental) throw new UsageException("--reset-checkpoint requires --incremental.");
        if (incremental && (filter is not null || pageSize is not null)) throw new UsageException("--filter and --page-size cannot be combined with --incremental.");
        using var ctx = c.Connect(common.ProfileName);
        var client = ctx.Graph();
        if (!incremental)
        {
            var query = new GraphQuery(Select: string.Join(",", fields), Filter: filter, Top: pageSize);
            var data = new TabularData(fields, JsonRows.Project(client.EnumerateAsync<JsonElement>(collection, query, c.Cancellation), fields, c.Cancellation));
            c.Report(await ExportRunner.RunAsync(c.Env, common.Format, common.Destination, data, c.Cancellation));
            return ExitCodes.Success;
        }
        var columns = new[] { "_change" }.Concat(fields).ToArray();
        c.Report(await ExportRunner.RunIncrementalAsync(c.Env, c.Checkpoints, scope, reset, common.Format, common.Destination, previous =>
        {
            string? next = null;
            async IAsyncEnumerable<IReadOnlyList<string?>> Rows([EnumeratorCancellation] CancellationToken ct = default)
            {
                GraphDeltaPage<JsonElement> page;
                try { page = await client.GetDeltaAsync<JsonElement>(collection, previous is null ? null : new ContinuationToken("MicrosoftGraph", collection, previous), ct); }
                catch (GraphDeltaTokenExpiredException) { throw new VendorCliException("The saved Graph delta token has expired. Re-run with --reset-checkpoint to start a new full export."); }
                next = page.DeltaLink?.OpaqueValue;
                foreach (var item in page.Items)
                    yield return new[] { item.TryGetProperty("@removed", out _) ? "deleted" : "changed" }.Concat(JsonRows.Row(item, fields)).ToArray();
            }
            return new IncrementalSource { Data = new TabularData(columns, Rows(c.Cancellation)), NextCheckpoint = () => next };
        }, c.Cancellation));
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
