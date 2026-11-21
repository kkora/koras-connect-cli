using Koras.Connect.Cli.Cli;

namespace Koras.Connect.Cli.Profiles;

/// <summary>Tracks every resolved secret so that no message the CLI prints can contain one.</summary>
public sealed class Redactor
{
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    public const string Mask = "[REDACTED]";
    public void Register(string? secret) { if (!string.IsNullOrEmpty(secret) && secret.Length >= 3) _secrets.Add(secret); }
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var secret in _secrets.OrderByDescending(s => s.Length)) text = text.Replace(secret, Mask, StringComparison.Ordinal);
        return text;
    }
}

/// <summary>Resolves <c>env:NAME</c> and <c>file:PATH</c> credential references at run time. Literals are rejected.</summary>
public sealed class CredentialResolver(Func<string, string?> getEnv, Redactor redactor)
{
    public static string? SyntaxProblem(string reference)
    {
        if (reference.StartsWith("env:", StringComparison.Ordinal))
            return reference.Length > 4 ? null : "env: reference needs a variable name";
        if (reference.StartsWith("file:", StringComparison.Ordinal))
            return reference.Length > 5 ? null : "file: reference needs a path";
        return "must be 'env:NAME' or 'file:PATH' (secret values are never stored in profiles)";
    }

    /// <summary>Returns null when the reference cannot be resolved; <paramref name="problem"/> never contains a value.</summary>
    public string? TryResolve(string reference, out string? problem)
    {
        problem = SyntaxProblem(reference);
        if (problem is not null) return null;
        if (reference.StartsWith("env:", StringComparison.Ordinal))
        {
            var name = reference[4..];
            var value = getEnv(name);
            if (string.IsNullOrEmpty(value)) { problem = $"environment variable {name} is not set or empty"; return null; }
            redactor.Register(value);
            return value;
        }
        var path = reference[5..];
        try
        {
            var text = File.ReadAllText(path).Trim('\r', '\n');
            if (text.Length == 0) { problem = $"credential file {path} is empty"; return null; }
            redactor.Register(text);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"credential file {path} cannot be read";
            return null;
        }
    }

    public string Resolve(Profile profile, string credentialName)
    {
        if (!profile.Credentials.TryGetValue(credentialName, out var reference))
            throw new ConfigurationCliException($"Profile '{profile.Name}' has no credential '{credentialName}'.");
        return TryResolve(reference, out var problem) ?? throw new ConfigurationCliException($"Credential '{credentialName}' in profile '{profile.Name}': {problem}.");
    }

    public string? ResolveOptional(Profile profile, string credentialName)
        => profile.Credentials.ContainsKey(credentialName) ? Resolve(profile, credentialName) : null;
}

public static class ProfileValidator
{
    /// <summary>Local validation only: no network, no secret values in the returned messages.</summary>
    public static IReadOnlyList<string> Validate(Profile profile, CredentialResolver? resolver)
    {
        var issues = new List<string>();
        if (!ConnectorSpec.All.TryGetValue(profile.Connector, out var spec))
            return [$"connector '{profile.Connector}' is not supported (supported: {string.Join(", ", ConnectorSpec.All.Keys)})"];
        foreach (var s in spec.Settings)
        {
            if (!profile.Settings.TryGetValue(s.Name, out var value) || string.IsNullOrWhiteSpace(value))
            { if (s.Required) issues.Add($"missing setting '{s.Name}' ({s.Description})"); continue; }
            switch (s.Kind)
            {
                case SettingKind.HttpsUrl:
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                        issues.Add($"setting '{s.Name}' must be an absolute https URL without credentials, query or fragment");
                    break;
                case SettingKind.ExistingFile:
                    if (!File.Exists(value)) issues.Add($"setting '{s.Name}' points to a file that does not exist: {value}");
                    break;
                case SettingKind.Boolean:
                    if (!bool.TryParse(value, out _)) issues.Add($"setting '{s.Name}' must be true or false");
                    break;
                case SettingKind.Integer:
                    if (!int.TryParse(value, out var n) || n < 1) issues.Add($"setting '{s.Name}' must be a positive integer");
                    break;
            }
        }
        foreach (var key in profile.Settings.Keys.Where(k => spec.Settings.All(s => s.Name != k))) issues.Add($"unknown setting '{key}'");
        foreach (var c in spec.Credentials)
        {
            if (!profile.Credentials.TryGetValue(c.Name, out var reference) || string.IsNullOrWhiteSpace(reference))
            { if (c.Required) issues.Add($"missing credential reference '{c.Name}' ({c.Description})"); continue; }
            var syntax = CredentialResolver.SyntaxProblem(reference);
            if (syntax is not null) { issues.Add($"credential '{c.Name}': {syntax}"); continue; }
            if (resolver is not null && resolver.TryResolve(reference, out var problem) is null) issues.Add($"credential '{c.Name}': {problem}");
        }
        foreach (var key in profile.Credentials.Keys.Where(k => spec.Credentials.All(c => c.Name != k))) issues.Add($"unknown credential '{key}'");
        if (profile.Connector == "adp" && profile.Settings.TryGetValue("pageSize", out var ps) && int.TryParse(ps, out var page) && page > 100)
            issues.Add("setting 'pageSize' must be 1-100 for ADP");
        return issues;
    }
}
