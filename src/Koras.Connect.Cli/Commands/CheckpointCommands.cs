using Koras.Connect.Cli.Cli;

namespace Koras.Connect.Cli.Commands;

public static class CheckpointCommands
{
    public static Task<int> List(CommandContext c)
    {
        var profile = c.Args.Require("profile");
        c.Args.RejectUnknown();
        foreach (var r in c.Checkpoints.List(profile)) c.Env.Out.WriteLine($"{r.ScopeId}\t{r.Connector}\t{r.UpdatedAt:O}");
        return Task.FromResult(ExitCodes.Success);
    }

    public static Task<int> Reset(CommandContext c)
    {
        var profile = c.Args.Require("profile");
        var scope = c.Args.Get("scope");
        var all = c.Args.Flag("all");
        c.Args.RejectUnknown();
        if (all == (scope is not null)) throw new UsageException("Specify exactly one of --scope ID or --all.");
        var records = c.Checkpoints.List(profile);
        var removed = 0;
        if (all) removed = c.Checkpoints.DeleteAll(profile);
        else
        {
            var match = records.FirstOrDefault(r => r.ScopeId == scope) ?? throw new UsageException($"No checkpoint with scope '{scope}' for profile '{profile}'.");
            removed = c.Checkpoints.DeleteById(match.Connector, match.Profile, match.ScopeId);
        }
        c.Env.Error.WriteLine($"Removed {removed} checkpoint(s). The next incremental run will be a full export.");
        return Task.FromResult(ExitCodes.Success);
    }
}
