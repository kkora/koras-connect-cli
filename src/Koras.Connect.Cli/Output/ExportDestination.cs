using Koras.Connect.Cli.Cli;

namespace Koras.Connect.Cli.Output;

public sealed record PublishResult(string? Path, long Bytes);

/// <summary>
/// A validated export destination. Construct (and therefore validate) before any record is fetched, then call
/// <see cref="PublishAsync"/>. File outputs are written to a hidden temporary sibling and moved into place only after
/// the write succeeds; the temporary file is removed on failure or cancellation. Stdout output is staged in a temp
/// file first so a failed run never leaves partial data on stdout.
/// </summary>
public sealed class ExportDestination
{
    public string? Path { get; }
    public bool Overwrite { get; }
    public bool IsStdout => Path is null;

    private ExportDestination(string? path, bool overwrite) { Path = path; Overwrite = overwrite; }

    public static ExportDestination Resolve(string? output, bool overwrite, string format)
    {
        if (string.IsNullOrWhiteSpace(output) || output == "-")
        {
            if (format == "xlsx") throw new UsageException("xlsx is a binary format and cannot be written to stdout; pass --output FILE.");
            return new ExportDestination(null, overwrite);
        }
        string full;
        try { full = System.IO.Path.GetFullPath(output); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new DestinationException($"Output path '{output}' is not valid.", ex); }
        if (Directory.Exists(full)) throw new DestinationException($"Output path '{output}' is a directory.");
        var dir = System.IO.Path.GetDirectoryName(full)!;
        if (!Directory.Exists(dir)) throw new DestinationException($"Output directory '{dir}' does not exist.");
        if (File.Exists(full) && !overwrite) throw new DestinationException($"Output file '{output}' already exists. Pass --overwrite to replace it.");
        return new ExportDestination(full, overwrite);
    }

    /// <summary>Runs <paramref name="write"/> against a temporary stream and publishes the result.</summary>
    public async Task<PublishResult> PublishAsync(Func<Stream, Task> write, Func<Stream> openStdout, CancellationToken ct)
    {
        string temp;
        FileStream stream;
        var published = false;
        try
        {
            if (IsStdout) temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "koras-connect-" + Guid.NewGuid().ToString("N") + ".tmp");
            else temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path!)!, "." + System.IO.Path.GetFileName(Path!) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try { stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new DestinationException("Cannot create a temporary file for the export; the destination is not writable.", ex); }
            if (!IsStdout && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (DestinationException) { throw; }

        try
        {
            long length;
            await using (stream)
            {
                await write(stream);
                await stream.FlushAsync(ct);
                length = stream.Length;
                ct.ThrowIfCancellationRequested();
                if (IsStdout)
                {
                    stream.Position = 0;
                    await using var stdout = openStdout();
                    await stream.CopyToAsync(stdout, ct);
                    await stdout.FlushAsync(ct);
                    return new PublishResult(null, length);
                }
            }
            try { File.Move(temp, Path!, Overwrite); }
            catch (IOException ex) when (!Overwrite && File.Exists(Path!))
            { throw new DestinationException($"Output file '{Path}' appeared while exporting; refusing to overwrite it. Pass --overwrite to replace.", ex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new DestinationException($"Could not publish the export to '{Path}'.", ex); }
            published = true;
            return new PublishResult(Path, length);
        }
        finally
        {
            if (!published) { try { File.Delete(temp); } catch (Exception) { /* best effort */ } }
        }
    }
}
