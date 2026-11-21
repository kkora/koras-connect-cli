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

    }
