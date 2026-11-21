using System.Text.Json;
using System.Text.RegularExpressions;
using Koras.Connect.Cli.Cli;

namespace Koras.Connect.Cli.Profiles;

/// <summary>A named profile: non-secret settings plus credential <em>references</em>. Secrets are never stored.</summary>
public sealed record Profile(string Name, string Connector, SortedDictionary<string, string> Settings, SortedDictionary<string, string> Credentials)
{
    public const int CurrentVersion = 1;
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static void ValidateName(string name)
    {
        if (!NamePattern.IsMatch(name)) throw new UsageException("Profile names must be 1-64 characters of letters, digits, '.', '_' or '-', starting with a letter or digit.");
    }

    public string ToJson() => JsonSerializer.Serialize(new Document(CurrentVersion, Name, Connector, Settings, Credentials), Options);
    public static Profile FromJson(string json)
    {
        Document? doc;
        try { doc = JsonSerializer.Deserialize<Document>(json, Options); }
        catch (JsonException) { throw new ConfigurationCliException("Profile file is not valid JSON."); }
        if (doc is null || string.IsNullOrWhiteSpace(doc.Name) || string.IsNullOrWhiteSpace(doc.Connector))
            throw new ConfigurationCliException("Profile file is missing 'name' or 'connector'.");
        return new Profile(doc.Name, doc.Connector, new(doc.Settings ?? new Dictionary<string, string>(), StringComparer.Ordinal), new(doc.Credentials ?? new Dictionary<string, string>(), StringComparer.Ordinal));
    }
    private sealed record Document(int Version, string Name, string Connector, IDictionary<string, string>? Settings, IDictionary<string, string>? Credentials);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

public sealed class ProfileStore(string homeDirectory)
{
    private string Dir => Path.Combine(homeDirectory, "profiles");
    public string PathFor(string name) { Profile.ValidateName(name); return Path.Combine(Dir, name + ".json"); }

    public void Save(Profile profile, bool overwrite)
    {
        var path = PathFor(profile.Name);
        Directory.CreateDirectory(Dir);
        if (!overwrite && File.Exists(path)) throw new UsageException($"Profile '{profile.Name}' already exists. Use --force to replace it.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, profile.ToJson());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite);
    }

    public Profile Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) throw new ConfigurationCliException($"Profile '{name}' was not found. Create it with: koras-connect profile init {name} --connector <name> ...");
        var profile = Profile.FromJson(File.ReadAllText(path));
        if (!string.Equals(profile.Name, name, StringComparison.Ordinal)) throw new ConfigurationCliException($"Profile file for '{name}' declares a different name.");
        return profile;
    }

    public IReadOnlyList<string> ListNames() => !Directory.Exists(Dir) ? []
        : Directory.EnumerateFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
}
