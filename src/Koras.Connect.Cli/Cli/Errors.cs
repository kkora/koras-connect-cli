namespace Koras.Connect.Cli.Cli;

/// <summary>Process exit codes. Documented in README.md; keep in sync.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;          // unexpected / vendor error not otherwise classified
    public const int Usage = 2;            // bad arguments or options
    public const int Configuration = 3;    // profile missing/invalid, credential reference unresolved
    public const int Authentication = 4;   // credentials rejected (HTTP 401 / token endpoint rejection)
    public const int Authorization = 5;    // authenticated but denied (HTTP 403)
    public const int Connectivity = 6;     // network, TLS, timeout, 5xx
    public const int Destination = 7;      // output refused: exists without --overwrite, not writable
    public const int Vendor = 8;           // throttled, malformed vendor response, export failed
    public const int CheckpointBusy = 9;   // another run holds the checkpoint
    public const int CheckpointSave = 10;  // file published but checkpoint could not be saved
    public const int Cancelled = 130;
}

public abstract class CliException(string message, int exitCode, Exception? inner = null) : Exception(message, inner)
{
    public int ExitCode { get; } = exitCode;
}
public sealed class UsageException(string message) : CliException(message, ExitCodes.Usage);
public sealed class ConfigurationCliException(string message) : CliException(message, ExitCodes.Configuration);
public sealed class DestinationException(string message, Exception? inner = null) : CliException(message, ExitCodes.Destination, inner);
public sealed class CheckpointBusyException(string message) : CliException(message, ExitCodes.CheckpointBusy);
public sealed class CheckpointSaveException(string message, Exception? inner = null) : CliException(message, ExitCodes.CheckpointSave, inner);
public sealed class VendorCliException(string message, Exception? inner = null) : CliException(message, ExitCodes.Vendor, inner);
