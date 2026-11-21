namespace Koras.Connect.Cli.Cli;

/// <summary>Minimal, dependency-free argument parser: positionals, <c>--name value</c>, <c>--name=value</c> and boolean flags.</summary>
public sealed class ParsedArgs
{
    /// <summary>Options that never take a value.</summary>
    public static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    { "help", "version", "overwrite", "no-compress", "incremental", "reset-checkpoint", "all", "beta", "no-resolve", "force" };
    /// <summary>Options that may repeat.</summary>
    public static readonly HashSet<string> Repeatable = new(StringComparer.Ordinal) { "set", "credential" };

    public List<string> Positionals { get; } = [];
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    public static ParsedArgs Parse(IReadOnlyList<string> args)
    {
        var result = new ParsedArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "-h") arg = "--help";
            if (arg == "-o") arg = "--output";
            if (arg == "-p") arg = "--profile";
            if (arg == "--") { result.Positionals.AddRange(args.Skip(i + 1)); break; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { result.Positionals.Add(arg); continue; }
            var body = arg[2..];
            string? value = null;
            var eq = body.IndexOf('=');
            if (eq >= 0) { value = body[(eq + 1)..]; body = body[..eq]; }
            if (body.Length == 0) throw new UsageException("Empty option name.");
            if (Flags.Contains(body))
            {
                if (value is not null)
                {
                    if (!bool.TryParse(value, out var on)) throw new UsageException($"Option --{body} is a flag and takes no value.");
                    if (!on) continue;
                }
                result.Add(body, "true"); continue;
            }
            if (value is null)
            {
                if (i + 1 >= args.Count) throw new UsageException($"Option --{body} requires a value.");
                value = args[++i];
            }
            if (!Repeatable.Contains(body) && result._options.ContainsKey(body)) throw new UsageException($"Option --{body} was given more than once.");
            result.Add(body, value);
        }
        return result;
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var list)) _options[name] = list = [];
        list.Add(value);
    }

    public bool Has(string name) { _consumed.Add(name); return _options.ContainsKey(name); }
    public bool Flag(string name) => Has(name);
    public string? Get(string name) { _consumed.Add(name); return _options.TryGetValue(name, out var l) ? l[^1] : null; }
    public IReadOnlyList<string> GetAll(string name) { _consumed.Add(name); return _options.TryGetValue(name, out var l) ? l : []; }
    public string Require(string name) => Get(name) is { Length: > 0 } v ? v : throw new UsageException($"Missing required option --{name}.");

    /// <summary>Throws if an option was supplied that the command never read.</summary>
    public void RejectUnknown()
    {
        var unknown = _options.Keys.Where(k => !_consumed.Contains(k) && k is not "help").OrderBy(k => k, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw new UsageException("Unknown option(s): " + string.Join(", ", unknown.Select(u => "--" + u)));
    }

    /// <summary>Option names (excluding the given ones) with their values, for building a checkpoint scope.</summary>
    public IEnumerable<KeyValuePair<string, string>> Snapshot(params string[] exclude)
        => _options.Where(o => !exclude.Contains(o.Key)).OrderBy(o => o.Key, StringComparer.Ordinal)
            .SelectMany(o => o.Value.Select(v => new KeyValuePair<string, string>(o.Key, v)));
}
