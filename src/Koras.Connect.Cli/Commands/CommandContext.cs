using Koras.Connect.Cli.Checkpoints;
using Koras.Connect.Cli.Cli;
using Koras.Connect.Cli.Connectors;
using Koras.Connect.Cli.Profiles;

namespace Koras.Connect.Cli.Commands;

public sealed class CommandContext
{
    public required CliEnvironment Env { get; init; }
    public required ParsedArgs Args { get; init; }
    public required Redactor Redactor { get; init; }
    public required ProfileStore Profiles { get; init; }
    public required ICheckpointStore Checkpoints { get; init; }
    public CancellationToken Cancellation => Env.Cancellation;

    public CredentialResolver Resolver => new(Env.GetEnvironmentVariable, Redactor);

    public ConnectorContext Connect(string profileName)
    {
        var profile = Profiles.Load(profileName);
        return new ConnectorContext(profile, Env, Resolver);
    }

    public Profile LoadProfile(string connector)
    {
        var name = Args.Get("profile") ?? throw new UsageException("Missing required option --profile NAME.");
        var profile = Profiles.Load(name);
        if (profile.Connector != connector) throw new UsageException($"Profile '{name}' is a '{profile.Connector}' profile; this command needs a '{connector}' profile.");
        return profile;
    }

    public void Report(ExportOutcome outcome)
    {
        Env.Error.WriteLine(outcome.Path is null ? $"Exported {outcome.Records} record(s) to stdout." : $"Exported {outcome.Records} record(s) ({outcome.Bytes} bytes) to {outcome.Path}.");
        if (outcome.Path is not null) Env.Out.WriteLine(outcome.Path);
    }
}
