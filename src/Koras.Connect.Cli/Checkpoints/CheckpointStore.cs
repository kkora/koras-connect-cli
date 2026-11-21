using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Koras.Connect.Cli.Cli;

namespace Koras.Connect.Cli.Checkpoints;

/// <summary>Identity of a checkpoint: connector + profile + the effective query. Output options are excluded.</summary>
public sealed record CheckpointScope(string Connector, string Profile, string QueryFingerprint)
{
    public string Id => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Connector}\n{Profile}\n{QueryFingerprint}"))).ToLowerInvariant()[..24];

    /// <summary>Canonical fingerprint: sorted key=value pairs of the options that change which records are produced.</summary>
    public static CheckpointScope Create(string connector, string profile, IEnumerable<KeyValuePair<string, string>> effectiveQuery)
        => new(connector, profile, string.Join("\n", effectiveQuery.OrderBy(k => k.Key, StringComparer.Ordinal).ThenBy(k => k.Value, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")));
}

public sealed record CheckpointRecord(int Version, string Connector, string Profile, string ScopeId, string Value, DateTimeOffset UpdatedAt);

public interface ICheckpointLease : IDisposable { }

public interface ICheckpointStore
{
    /// <summary>Takes the exclusive per-scope lock. Throws <see cref="CheckpointBusyException"/> if another run holds it.</summary>
    ICheckpointLease Acquire(CheckpointScope scope);
    CheckpointRecord? Load(CheckpointScope scope);
    /// <summary>Atomically replaces the saved checkpoint. A failure leaves the previous checkpoint intact.</summary>
    void Save(CheckpointScope scope, string value, DateTimeOffset now);
    bool Delete(CheckpointScope scope);
    int DeleteById(string connector, string profile, string scopeId);
    IReadOnlyList<CheckpointRecord> List(string profile);
    int DeleteAll(string profile);
}

public sealed class FileCheckpointStore(string homeDirectory) : ICheckpointStore
{
    private string DirFor(string connector, string profile) => Path.Combine(homeDirectory, "checkpoints", connector, profile);
    private string FileFor(CheckpointScope s) => Path.Combine(DirFor(s.Connector, s.Profile), s.Id + ".json");

    private sealed class Lease(FileStream stream) : ICheckpointLease { public void Dispose() => stream.Dispose(); }

    public ICheckpointLease Acquire(CheckpointScope scope)
    {
        var dir = DirFor(scope.Connector, scope.Profile);
        Directory.CreateDirectory(dir);
        var lockPath = Path.Combine(dir, scope.Id + ".lock");
        try { return new Lease(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException) { throw new CheckpointBusyException("Another export is already running for this connector, profile and query; refusing to run concurrently. Try again when it finishes."); }
    }

    public CheckpointRecord? Load(CheckpointScope scope)
    {
        var path = FileFor(scope);
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<CheckpointRecord>(File.ReadAllText(path), Json);
            if (record is null || record.ScopeId != scope.Id || string.IsNullOrEmpty(record.Value)) throw new JsonException("incomplete");
            return record;
        }
        catch (JsonException) { throw new ConfigurationCliException($"Checkpoint file {path} is corrupt. Remove it with 'koras-connect checkpoint reset --profile {scope.Profile} --scope {scope.Id}' and re-run."); }
    }

    public void Save(CheckpointScope scope, string value, DateTimeOffset now)
    {
        var path = FileFor(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new CheckpointRecord(1, scope.Connector, scope.Profile, scope.Id, value, now), Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, overwrite: true);
        }
        catch { try { File.Delete(temp); } catch (Exception) { } throw; }
    }

    public int DeleteById(string connector, string profile, string scopeId)
    {
        var p = Path.Combine(DirFor(connector, profile), scopeId + ".json");
        if (!File.Exists(p)) return 0; File.Delete(p); return 1;
    }

    public bool Delete(CheckpointScope scope) { var p = FileFor(scope); if (!File.Exists(p)) return false; File.Delete(p); return true; }

    public IReadOnlyList<CheckpointRecord> List(string profile)
    {
        var root = Path.Combine(homeDirectory, "checkpoints");
        if (!Directory.Exists(root)) return [];
        var found = new List<CheckpointRecord>();
        foreach (var dir in Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, profile)).Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                try { if (JsonSerializer.Deserialize<CheckpointRecord>(File.ReadAllText(file), Json) is { } r) found.Add(r); } catch (JsonException) { }
        return found.OrderBy(r => r.Connector, StringComparer.Ordinal).ThenBy(r => r.ScopeId, StringComparer.Ordinal).ToArray();
    }

    public int DeleteAll(string profile)
    {
        var n = 0;
        var root = Path.Combine(homeDirectory, "checkpoints");
        if (!Directory.Exists(root)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, profile)).Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json")) { File.Delete(file); n++; }
        return n;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
