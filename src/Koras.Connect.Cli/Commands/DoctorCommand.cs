using System.Text.Json;
using Koras.Connect.Cli.Cli;
using Koras.Connect.Cli.Connectors;
using Koras.Connect.Cli.Profiles;
using Koras.Connect.Microsoft.Graph;
using Koras.Connect.Sap.SuccessFactors;

namespace Koras.Connect.Cli.Commands;

/// <summary>CLI-003: validate the profile, then run one minimal SDK-backed read. All diagnostics go to stderr.</summary>
public static class DoctorCommand
{
    public static async Task<int> Run(CommandContext c)
    {
        var name = c.Args.Require("profile");
        c.Args.RejectUnknown();
        var err = c.Env.Error;
        var profile = c.Profiles.Load(name);
        err.WriteLine($"doctor: profile '{profile.Name}' ({profile.Connector})");
        var issues = ProfileValidator.Validate(profile, c.Resolver);
        if (issues.Count > 0)
        {
            err.WriteLine("[fail] configuration");
            foreach (var i in issues) err.WriteLine("  - " + c.Redactor.Redact(i));
            return ExitCodes.Configuration;
        }
        err.WriteLine("[ok]   configuration and credential references");
        using var ctx = new ConnectorContext(profile, c.Env, c.Resolver);
        try
        {
            var what = await ProbeAsync(ctx, profile.Connector, c.Cancellation);
            err.WriteLine($"[ok]   connectivity, authentication and authorization ({what})");
            err.WriteLine("doctor: all checks passed");
            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !c.Cancellation.IsCancellationRequested)
        {
            var failure = FailureClassifier.Classify(ex, c.Redactor, c.Cancellation);
            err.WriteLine($"[fail] {failure.Kind.ToString().ToLowerInvariant()}: {failure.Message}");
            return failure.ExitCode;
        }
    }

    private static async Task<string> ProbeAsync(ConnectorContext ctx, string connector, CancellationToken ct)
    {
        switch (connector)
        {
            case "adp": { using var d = await ctx.Adp().GetWorkersPageAsync(0, 1, null, ct); return "read 1 ADP workers page"; }
            case "sap": { await foreach (var _ in ctx.Sap().EnumeratePersonsAsync(new ODataQuery(Select: "personIdExternal"), ct)) break; return "read SuccessFactors PerPerson"; }
            case "qualtrics": { await ctx.Qualtrics().ListSurveysPageAsync(null, ct); return "listed Qualtrics surveys"; }
            case "dataverse": { await ctx.Dataverse().WhoAmIAsync(ct); return "Dataverse WhoAmI"; }
            case "salesforce": { await ctx.Salesforce().GetResourceMapAsync(ct); return "Salesforce resource map"; }
            case "graph": { await foreach (var _ in ctx.Graph().EnumerateAsync<JsonElement>("users", new GraphQuery(Select: "id", Top: 1), ct)) break; return "read 1 Graph user"; }
            default: throw new ConfigurationCliException($"Unsupported connector '{connector}'.");
        }
    }
}
