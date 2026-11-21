using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Koras.Connect.Adp.Workers;
using Koras.Connect.Authentication;
using Koras.Connect.Cli.Cli;
using Koras.Connect.Cli.Profiles;
using Koras.Connect.Dataverse;
using Koras.Connect.Microsoft.Graph;
using Koras.Connect.Qualtrics;
using Koras.Connect.Salesforce;
using Koras.Connect.Sap.SuccessFactors;
using Koras.Connect.Transport;

namespace Koras.Connect.Cli.Connectors;

internal sealed class StaticTokenSource(string token) : IAccessTokenSource
{
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(token);
}

/// <summary>Builds SDK clients for one profile and owns everything it disposes (certificates, HTTP clients, token sources).
/// Credential values are resolved here, at run time, and registered with the redactor.</summary>
public sealed class ConnectorContext : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    public Profile Profile { get; }
    public CliEnvironment Env { get; }
    public CredentialResolver Credentials { get; }

    public ConnectorContext(Profile profile, CliEnvironment env, CredentialResolver credentials)
    { Profile = profile; Env = env; Credentials = credentials; }

    private string Setting(string name) => Profile.Settings.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
        ? v : throw new ConfigurationCliException($"Profile '{Profile.Name}' is missing setting '{name}'.");
    private string? OptionalSetting(string name) => Profile.Settings.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    private Uri UrlSetting(string name) => new(Setting(name));
    private T Own<T>(T d) where T : IDisposable { _owned.Add(d); return d; }
    private IntegrationTransport Transport(HttpClient http, IAccessTokenSource tokens) => new(http, tokens, Env.Transport);

    private void Require(string connector)
    {
        if (Profile.Connector != connector)
            throw new UsageException($"Profile '{Profile.Name}' is a '{Profile.Connector}' profile; this command needs a '{connector}' profile.");
        var issues = ProfileValidator.Validate(Profile, null);
        if (issues.Count > 0) throw new ConfigurationCliException($"Profile '{Profile.Name}' is invalid: {string.Join("; ", issues)}. Run: koras-connect profile validate {Profile.Name}");
    }

    private static T Wrap<T>(Func<T> build, string what)
    {
        try { return build(); }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or global::Microsoft.Extensions.Options.OptionsValidationException)
        { throw new ConfigurationCliException($"Invalid {what} configuration: {ex.Message.Split('\n')[0]}"); }
    }

    public AdpWorkersClient Adp()
    {
        Require("adp");
        var id = Credentials.Resolve(Profile, "clientId");
        var secret = Credentials.Resolve(Profile, "clientSecret");
        var password = Credentials.ResolveOptional(Profile, "certificatePassword");
        X509Certificate2 cert;
        try { cert = Own(ClientCertificateTransport.LoadPkcs12(Setting("certificatePath"), password)); }
        catch (CryptographicException) { throw new ConfigurationCliException("The ADP client certificate could not be loaded (wrong password or not a PKCS#12 file)."); }
        if (!cert.HasPrivateKey) throw new ConfigurationCliException("The ADP client certificate has no private key.");
        var tokens = Own(new ClientCredentialsTokenSource(Own(Env.Http.Create("adp-oauth", cert)),
            Wrap(() => new ClientCredentialsOptions(UrlSetting("tokenEndpoint"), id, secret, OptionalSetting("scope")), "ADP OAuth")));
        var page = int.TryParse(OptionalSetting("pageSize"), out var p) ? p : 100;
        return Wrap(() => new AdpWorkersClient(Transport(Own(Env.Http.Create("adp-api", cert)), tokens), new AdpWorkersOptions(UrlSetting("apiRoot"), page)), "ADP");
    }

    public SuccessFactorsClient Sap()
    {
        Require("sap");
        var assertion = Credentials.Resolve(Profile, "assertion");
        var tokens = Own(Wrap(() => new SamlBearerTokenSource(Own(Env.Http.Create("sap-oauth")), UrlSetting("tokenEndpoint"), Setting("companyId"), Setting("clientId"),
            _ => ValueTask.FromResult(assertion)), "SAP OAuth"));
        var page = int.TryParse(OptionalSetting("pageSize"), out var p) ? p : 100;
        return Wrap(() => new SuccessFactorsClient(Transport(Own(Env.Http.Create("sap-api")), tokens), new SuccessFactorsOptions(UrlSetting("odataRoot"), page)), "SAP");
    }

    public QualtricsClient Qualtrics()
    {
        Require("qualtrics");
        var token = Credentials.Resolve(Profile, "apiToken");
        return Wrap(() => QualtricsClient.Create(Own(Env.Http.Create("qualtrics-api")), new StaticTokenSource(token), new QualtricsOptions(UrlSetting("apiRoot")), Env.Transport), "Qualtrics");
    }

    public DataverseClient Dataverse()
    {
        Require("dataverse");
        var secret = Credentials.Resolve(Profile, "clientSecret");
        var tokens = Wrap(() => DataverseAuthentication.CreateTokenSource(Own(Env.Http.Create("dataverse-oauth")),
            new DataverseClientCredentialsOptions(UrlSetting("tokenEndpoint"), Setting("clientId"), Setting("environmentUrl"), ClientSecret: secret)), "Dataverse OAuth");
        if (tokens is IDisposable d) Own(d);
        return Wrap(() => new DataverseClient(Transport(Own(Env.Http.Create("dataverse-api")), tokens), new DataverseOptions(UrlSetting("environmentUrl"))), "Dataverse");
    }

    public SalesforceClient Salesforce()
    {
        Require("salesforce");
        var secret = Credentials.Resolve(Profile, "clientSecret");
        var tokens = Wrap(() => SalesforceAuthentication.CreateClientCredentialsTokenSource(Own(Env.Http.Create("salesforce-oauth")),
            new SalesforceClientCredentialsOptions(UrlSetting("tokenEndpoint"), Setting("clientId"), secret)), "Salesforce OAuth");
        if (tokens is IDisposable d) Own(d);
        var version = OptionalSetting("apiVersion") ?? "v68.0";
        return Wrap(() => new SalesforceClient(Transport(Own(Env.Http.Create("salesforce-api")), tokens), new SalesforceOptions(UrlSetting("instanceRoot"), version)), "Salesforce");
    }

    public GraphClient Graph()
    {
        Require("graph");
        var secret = Credentials.Resolve(Profile, "clientSecret");
        var tokens = Wrap(() => GraphAuthentication.CreateClientCredentialsTokenSource(Own(Env.Http.Create("graph-oauth")),
            new GraphClientCredentialsOptions(Setting("tenantId"), Setting("clientId"), secret)), "Graph OAuth");
        if (tokens is IDisposable d) Own(d);
        var beta = bool.TryParse(OptionalSetting("beta"), out var b) && b;
        return Wrap(() => new GraphClient(Transport(Own(Env.Http.Create("graph-api")), tokens), new GraphOptions(UseBetaEndpoint: beta)), "Graph");
    }

    public void Dispose() { foreach (var d in Enumerable.Reverse(_owned)) { try { d.Dispose(); } catch (Exception) { } } _owned.Clear(); }
}
