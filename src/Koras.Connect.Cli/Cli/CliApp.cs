using System.Reflection;
using Koras.Connect.Cli.Checkpoints;
using Koras.Connect.Cli.Commands;
using Koras.Connect.Cli.Connectors;
using Koras.Connect.Cli.Profiles;

namespace Koras.Connect.Cli.Cli;

public static class CliApp
{
    public static string Version
    {
        get
        {
            var info = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    public static async Task<int> RunAsync(string[] argv, CliEnvironment env)
    {
        var redactor = new Redactor();
        ParsedArgs args;
        try { args = ParsedArgs.Parse(argv); }
        catch (UsageException ex) { env.Error.WriteLine("error: " + ex.Message); env.Error.WriteLine("Run 'koras-connect --help' for usage."); return ex.ExitCode; }

        try
        {
            if (args.Flag("version") && args.Positionals.Count == 0) { env.Out.WriteLine($"koras-connect {Version}"); return ExitCodes.Success; }
            if (args.Positionals.Count == 0)
            {
                if (args.Flag("help")) { env.Out.WriteLine(Help.Main); return ExitCodes.Success; }
                env.Error.WriteLine(Help.Main); return ExitCodes.Usage;
            }
            if (args.Flag("help")) { env.Out.WriteLine(Help.For(args.Positionals)); return ExitCodes.Success; }

            var store = new ProfileStore(env.HomeDirectory);
            ICheckpointStore checkpoints = new FileCheckpointStore(env.HomeDirectory);
            if (env.WrapCheckpointStore is not null) checkpoints = env.WrapCheckpointStore(checkpoints);
            var ctx = new CommandContext { Env = env, Args = args, Redactor = redactor, Profiles = store, Checkpoints = checkpoints };

            var handler = Route(args.Positionals);
            if (handler is null)
            {
                env.Error.WriteLine("error: unknown command '" + string.Join(' ', args.Positionals.Take(3)) + "'.");
                env.Error.WriteLine("Run 'koras-connect --help' for usage.");
                return ExitCodes.Usage;
            }
            return await handler(ctx);
        }
        catch (Exception ex)
        {
            var failure = FailureClassifier.Classify(ex, redactor, env.Cancellation);
            env.Error.WriteLine((failure.ExitCode == ExitCodes.Cancelled ? "" : "error: ") + failure.Message);
            if (failure.ExitCode == ExitCodes.Usage) env.Error.WriteLine("Run 'koras-connect --help' for usage.");
            return failure.ExitCode;
        }
    }

    private static Func<CommandContext, Task<int>>? Route(IReadOnlyList<string> p)
    {
        static bool Is(IReadOnlyList<string> p, params string[] words) => p.Count >= words.Length && words.Select((w, i) => p[i] == w).All(x => x);
        if (Is(p, "profile", "init")) return ProfileCommands.Init;
        if (Is(p, "profile", "list")) return ProfileCommands.List;
        if (Is(p, "profile", "validate")) return ProfileCommands.Validate;
        if (Is(p, "doctor")) return DoctorCommand.Run;
        if (Is(p, "adp", "workers", "export")) return ExportCommands.AdpWorkers;
        if (Is(p, "sap", "persons", "export")) return ExportCommands.SapPersons;
        if (Is(p, "qualtrics", "responses", "export")) return ExportCommands.QualtricsResponses;
        return null;
    }
}

public static class Help
{
    public const string Main = """
koras-connect - export data from enterprise systems with the Koras Connect SDK

Usage: koras-connect <command> [options]

Commands:
  profile init NAME        Create or replace a named profile (--connector, --set, --credential, --force)
  profile list             List profiles (name and connector)
  profile validate NAME    Check a profile locally; credential references are resolved but never printed
  doctor --profile NAME    Validate, then run one minimal read to diagnose auth/connectivity
  adp workers export       Export ADP workers          (--fields required)
  sap persons export       Export SAP SuccessFactors persons (--fields required)
  qualtrics responses export  Start/resume a Qualtrics response export and download the vendor file

Common export options:
  --profile NAME           Profile to use (required)
  --output FILE|-          Destination file, or '-' (default) for stdout; never overwrites unless --overwrite
  --format csv|json|xml|xlsx  Output format (default csv; xlsx needs --output FILE)
  --overwrite              Replace an existing output file
  --fields a,b,c           Explicit field selection

Global options:
  -h, --help               Show help (also: koras-connect <command> --help)
  --version                Show the version

Exit codes: 0 ok, 1 error, 2 usage, 3 configuration, 4 authentication, 5 authorization,
            6 connectivity, 7 destination, 8 vendor, 9 checkpoint busy, 10 checkpoint save, 130 cancelled.
Data goes to stdout (or --output); diagnostics and progress go to stderr.
""";

    public static string For(IReadOnlyList<string> command) => command.Count switch
    {
        0 => Main,
        _ => $"Usage: koras-connect {string.Join(' ', command)} [options]\n\nSee 'koras-connect --help' and the README for the options each command accepts.\n"
    };
}
