using System.Security.Cryptography.X509Certificates;
using Koras.Connect.Transport;

namespace Koras.Connect.Cli.Cli;

/// <summary>Creates HTTP clients for connector roles. Tests substitute a fake handler; production disables redirects (SDK requirement).</summary>
public interface IHttpClientProvider
{
    HttpClient Create(string role, X509Certificate2? clientCertificate = null);
}

public sealed class DefaultHttpClientProvider : IHttpClientProvider
{
    public HttpClient Create(string role, X509Certificate2? clientCertificate = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (clientCertificate is not null) handler.ClientCertificates.Add(clientCertificate);
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
    }
}

/// <summary>Everything the CLI needs from its host process, injectable for tests.</summary>
public sealed class CliEnvironment
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }
    public required Func<Stream> OpenStandardOutput { get; init; }
    public required Func<string, string?> GetEnvironmentVariable { get; init; }
    public required string HomeDirectory { get; init; }
    public IHttpClientProvider Http { get; init; } = new DefaultHttpClientProvider();
    public TransportOptions? Transport { get; init; }
    public CancellationToken Cancellation { get; init; }
    public Func<Checkpoints.ICheckpointStore, Checkpoints.ICheckpointStore>? WrapCheckpointStore { get; init; }

    public static CliEnvironment Default(CancellationToken cancellation) => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        OpenStandardOutput = Console.OpenStandardOutput,
        GetEnvironmentVariable = Environment.GetEnvironmentVariable,
        HomeDirectory = ResolveHome(Environment.GetEnvironmentVariable),
        Cancellation = cancellation,
    };

    public static string ResolveHome(Func<string, string?> env)
    {
        var explicitHome = env("KORAS_CONNECT_HOME");
        if (!string.IsNullOrWhiteSpace(explicitHome)) return Path.GetFullPath(explicitHome);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrEmpty(user) ? Path.GetTempPath() : user, ".koras-connect");
    }
}
