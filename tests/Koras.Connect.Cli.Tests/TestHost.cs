using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Koras.Connect.Cli.Checkpoints;
using Koras.Connect.Cli.Cli;
using Koras.Connect.Transport;

namespace Koras.Connect.Cli.Tests;

/// <summary>Synthetic vendor: routes every outbound request to a test-supplied function. No real network, tenants or customer data.</summary>
public sealed class FakeVendor : HttpMessageHandler, IHttpClientProvider
{
    public List<string> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage?> Route { get; set; } = _ => null;
    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;
    public int Count(string method, string contains) => Requests.Count(r => r.StartsWith(method + " ", StringComparison.Ordinal) && r.Contains(contains, StringComparison.Ordinal));

    public HttpClient Create(string role, X509Certificate2? clientCertificate = null) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add($"{request.Method} {request.RequestUri}");
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            return Task.FromResult(TokenStatus == HttpStatusCode.OK ? Json("""{"access_token":"synthetic-access-token","token_type":"Bearer","expires_in":3600}""") : Status(TokenStatus));
        return Task.FromResult(Route(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
}

public sealed class FailingSaveStore(ICheckpointStore inner) : ICheckpointStore
{
    public ICheckpointLease Acquire(CheckpointScope scope) => inner.Acquire(scope);
    public CheckpointRecord? Load(CheckpointScope scope) => inner.Load(scope);
    public void Save(CheckpointScope scope, string value, DateTimeOffset now) => throw new IOException("simulated disk failure");
    public bool Delete(CheckpointScope scope) => inner.Delete(scope);
    public int DeleteById(string connector, string profile, string scopeId) => inner.DeleteById(connector, profile, scopeId);
    public IReadOnlyList<CheckpointRecord> List(string profile) => inner.List(profile);
    public int DeleteAll(string profile) => inner.DeleteAll(profile);
}

public sealed class TestCli : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "koras-cli-test-" + Guid.NewGuid().ToString("N"));
    public string Home => Path.Combine(Root, "home");
    public string Work => Path.Combine(Root, "work");
    public FakeVendor Vendor { get; } = new();
    public Dictionary<string, string?> Env { get; } = new()
    {
        ["T_CLIENT_ID"] = "synthetic-client-id", ["T_CLIENT_SECRET"] = "synthetic-secret-value-123", ["T_CERT_PASSWORD"] = "synthetic-cert-password",
        ["T_ASSERTION"] = "synthetic-saml-assertion-abc", ["T_API_TOKEN"] = "synthetic-qualtrics-token-xyz",
    };
    public string Stdout { get; private set; } = "";
    public string Stderr { get; private set; } = "";
    public byte[] StdoutBytes { get; private set; } = [];

    public TestCli() { Directory.CreateDirectory(Home); Directory.CreateDirectory(Work); }

    public string Path_(string name) => Path.Combine(Work, name);

    public async Task<int> RunWith(string[] args, CancellationToken ct = default, Func<ICheckpointStore, ICheckpointStore>? wrap = null)
    {
        var out_ = new StringWriter(); var err = new StringWriter(); var bytes = new MemoryStream();
        var env = new CliEnvironment
        {
            Out = out_, Error = err, OpenStandardOutput = () => new NonClosingStream(bytes),
            GetEnvironmentVariable = n => Env.TryGetValue(n, out var v) ? v : null,
            HomeDirectory = Home, Http = Vendor, Cancellation = ct, WrapCheckpointStore = wrap,
            Transport = new TransportOptions { MaxRetries = 0, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) },
        };
        var code = await CliApp.RunAsync(args, env);
        Stdout = out_.ToString(); Stderr = err.ToString(); StdoutBytes = bytes.ToArray();
        return code;
    }

    public Task<int> Run(params string[] args) => RunWith(args);

    public string DataStdout => Encoding.UTF8.GetString(StdoutBytes);
    public IEnumerable<string> Leftovers(string dir) => Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories);

    public string MakeCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic-test-client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path_("synthetic-client.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "synthetic-cert-password"));
        return path;
    }

    public async Task<string> CreateProfile(string name, string connector)
    {
        string[] args = connector switch
        {
            "adp" => ["profile", "init", name, "--connector", "adp", "--set", "apiRoot=https://api.example.test", "--set", "tokenEndpoint=https://accounts.example.test/auth/oauth/v2/token",
                "--set", "certificatePath=" + MakeCertificate(), "--set", "pageSize=2", "--credential", "clientId=env:T_CLIENT_ID", "--credential", "clientSecret=env:T_CLIENT_SECRET",
                "--credential", "certificatePassword=env:T_CERT_PASSWORD"],
            "sap" => ["profile", "init", name, "--connector", "sap", "--set", "odataRoot=https://sap.example.test/odata/v2", "--set", "tokenEndpoint=https://sap.example.test/oauth/token",
                "--set", "companyId=ACME", "--set", "clientId=registered-client", "--credential", "assertion=env:T_ASSERTION"],
            "qualtrics" => ["profile", "init", name, "--connector", "qualtrics", "--set", "apiRoot=https://dc.qualtrics.example.test/API/v3", "--credential", "apiToken=env:T_API_TOKEN"],
            "dataverse" => ["profile", "init", name, "--connector", "dataverse", "--set", "environmentUrl=https://org.crm.example.test", "--set", "tokenEndpoint=https://login.example.test/tenant/oauth2/v2.0/token",
                "--set", "clientId=app-id", "--credential", "clientSecret=env:T_CLIENT_SECRET"],
            "salesforce" => ["profile", "init", name, "--connector", "salesforce", "--set", "instanceRoot=https://sf.example.test", "--set", "tokenEndpoint=https://sf.example.test/services/oauth2/token",
                "--set", "clientId=consumer-key", "--credential", "clientSecret=env:T_CLIENT_SECRET"],
            "graph" => ["profile", "init", name, "--connector", "graph", "--set", "tenantId=tenant-1", "--set", "clientId=app-id", "--credential", "clientSecret=env:T_CLIENT_SECRET"],
            _ => throw new ArgumentOutOfRangeException(nameof(connector)),
        };
        var code = await Run(args);
        Assert.Equal(0, code);
        return name;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

internal sealed class NonClosingStream(MemoryStream inner) : Stream
{
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { inner.Write(buffer.Span); return ValueTask.CompletedTask; }
    protected override void Dispose(bool disposing) { }
}
