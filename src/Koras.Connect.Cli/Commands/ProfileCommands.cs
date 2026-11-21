using Koras.Connect.Cli.Cli;
using Koras.Connect.Cli.Profiles;

namespace Koras.Connect.Cli.Commands;

public static class ProfileCommands
{
    public static Task<int> Init(CommandContext c)
    {
        if (c.Args.Positionals.Count != 3) throw new UsageException("Usage: koras-connect profile init NAME --connector adp|sap|qualtrics|dataverse|salesforce|graph --set key=value ... --credential key=env:VAR ... [--force]");
        var name = c.Args.Positionals[2];
        Profile.ValidateName(name);
        var connector = c.Args.Require("connector");
        if (!ConnectorSpec.All.ContainsKey(connector)) throw new UsageException($"Unknown connector '{connector}'. Supported: {string.Join(", ", ConnectorSpec.All.Keys)}.");
        var settings = ParsePairs(c.Args.GetAll("set"), "--set");
        var credentials = ParsePairs(c.Args.GetAll("credential"), "--credential");
        var force = c.Args.Flag("force");
        c.Args.RejectUnknown();
        foreach (var (key, reference) in credentials)
            if (CredentialResolver.SyntaxProblem(reference) is { } problem) throw new UsageException($"--credential {key}: {problem}.");
        var profile = new Profile(name, connector, new(settings, StringComparer.Ordinal), new(credentials, StringComparer.Ordinal));
        c.Profiles.Save(profile, force);
        c.Env.Error.WriteLine($"Profile '{name}' saved to {c.Profiles.PathFor(name)}.");
        var issues = ProfileValidator.Validate(profile, null);
        if (issues.Count > 0)
        {
            c.Env.Error.WriteLine("The profile is incomplete:");
            foreach (var i in issues) c.Env.Error.WriteLine("  - " + i);
            c.Env.Error.WriteLine($"Add the missing values with: koras-connect profile init {name} --force ...");
        }
        c.Env.Out.WriteLine(name);
        return Task.FromResult(ExitCodes.Success);
    }

    private static Dictionary<string, string> ParsePairs(IReadOnlyList<string> raw, string option)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            var eq = item.IndexOf('=');
            if (eq <= 0 || eq == item.Length - 1) throw new UsageException($"{option} expects key=value, got '{(option == "--credential" ? item.Split('=')[0] : item)}'.");
            var key = item[..eq];
            if (!map.TryAdd(key, item[(eq + 1)..])) throw new UsageException($"{option} {key} was given more than once.");
        }
        return map;
    }

    public static Task<int> List(CommandContext c)
    {
        c.Args.RejectUnknown();
        foreach (var name in c.Profiles.ListNames())
        {
            string connector;
            try { connector = c.Profiles.Load(name).Connector; } catch (CliException) { connector = "(unreadable)"; }
            c.Env.Out.WriteLine($"{name}\t{connector}");
        }
        return Task.FromResult(ExitCodes.Success);
    }

    public static Task<int> Validate(CommandContext c)
    {
        if (c.Args.Positionals.Count != 3) throw new UsageException("Usage: koras-connect profile validate NAME [--no-resolve]");
        var noResolve = c.Args.Flag("no-resolve");
        c.Args.RejectUnknown();
        var profile = c.Profiles.Load(c.Args.Positionals[2]);
        var issues = ProfileValidator.Validate(profile, noResolve ? null : c.Resolver);
        if (issues.Count == 0)
        {
            c.Env.Error.WriteLine($"Profile '{profile.Name}' ({profile.Connector}) is valid{(noResolve ? " (credential references not resolved)" : "; credential references resolve")}.");
            c.Env.Out.WriteLine("valid");
            return Task.FromResult(ExitCodes.Success);
        }
        c.Env.Error.WriteLine($"Profile '{profile.Name}' has {issues.Count} problem(s):");
        foreach (var i in issues) c.Env.Error.WriteLine("  - " + c.Redactor.Redact(i));
        return Task.FromResult(ExitCodes.Configuration);
    }
}
