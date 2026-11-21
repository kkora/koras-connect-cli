namespace Koras.Connect.Cli.Profiles;

public enum SettingKind { String, HttpsUrl, ExistingFile, Boolean, Integer }

public sealed record SettingSpec(string Name, SettingKind Kind, bool Required, string Description, string Example);
public sealed record CredentialSpec(string Name, bool Required, string Description);

/// <summary>Static description of what each connector's profile must contain.</summary>
public sealed record ConnectorSpec(string Name, string Product, IReadOnlyList<SettingSpec> Settings, IReadOnlyList<CredentialSpec> Credentials)
{
    public static readonly IReadOnlyDictionary<string, ConnectorSpec> All = new Dictionary<string, ConnectorSpec>(StringComparer.Ordinal)
    {
        ["adp"] = new("adp", "ADP Workforce Now",
        [
            new("apiRoot", SettingKind.HttpsUrl, true, "ADP API root", "https://api.adp.com"),
            new("tokenEndpoint", SettingKind.HttpsUrl, true, "OAuth token endpoint (mTLS)", "https://accounts.adp.com/auth/oauth/v2/token"),
            new("certificatePath", SettingKind.ExistingFile, true, "PKCS#12 client certificate for mTLS", "/secure/adp-client.pfx"),
            new("scope", SettingKind.String, false, "Optional OAuth scope", "api"),
            new("pageSize", SettingKind.Integer, false, "Workers per page (1-100)", "100"),
        ],
        [
            new("clientId", true, "OAuth client id"),
            new("clientSecret", true, "OAuth client secret"),
            new("certificatePassword", false, "Password for the PKCS#12 file, if any"),
        ]),
        ["sap"] = new("sap", "SAP SuccessFactors",
        [
            new("odataRoot", SettingKind.HttpsUrl, true, "OData v2 API root", "https://api-server.example.com/odata/v2"),
            new("tokenEndpoint", SettingKind.HttpsUrl, true, "OAuth token endpoint ending in /oauth/token", "https://api-server.example.com/oauth/token"),
            new("companyId", SettingKind.String, true, "SuccessFactors company id", "ACME"),
            new("clientId", SettingKind.String, true, "Registered OAuth client id (not a secret)", "registered-client-id"),
            new("pageSize", SettingKind.Integer, false, "Records per page", "100"),
        ],
        [new("assertion", true, "Pre-generated SAML bearer assertion (generation/signing stays outside the CLI)")]),
        ["qualtrics"] = new("qualtrics", "Qualtrics",
        [new("apiRoot", SettingKind.HttpsUrl, true, "Datacenter API v3 root", "https://yourdatacenter.qualtrics.com/API/v3")],
        [new("apiToken", true, "Qualtrics API token (X-API-TOKEN)")]),
        ["dataverse"] = new("dataverse", "Microsoft Dataverse",
        [
            new("environmentUrl", SettingKind.HttpsUrl, true, "Dataverse environment URL", "https://org.crm.dynamics.com"),
            new("tokenEndpoint", SettingKind.HttpsUrl, true, "Entra tenant token endpoint", "https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token"),
            new("clientId", SettingKind.String, true, "Entra application (client) id", "00000000-0000-0000-0000-000000000000"),
        ],
        [new("clientSecret", true, "Entra client secret")]),
        ["salesforce"] = new("salesforce", "Salesforce",
        [
            new("instanceRoot", SettingKind.HttpsUrl, true, "Instance URL (API version is appended by the SDK)", "https://example.my.salesforce.com"),
            new("apiVersion", SettingKind.String, false, "REST API version, default v68.0", "v68.0"),
            new("tokenEndpoint", SettingKind.HttpsUrl, true, "OAuth token endpoint", "https://example.my.salesforce.com/services/oauth2/token"),
            new("clientId", SettingKind.String, true, "Connected app consumer key", "consumer-key"),
        ],
        [new("clientSecret", true, "Connected app consumer secret")]),
        ["graph"] = new("graph", "Microsoft Graph",
        [
            new("tenantId", SettingKind.String, true, "Entra tenant id", "00000000-0000-0000-0000-000000000000"),
            new("clientId", SettingKind.String, true, "Entra application (client) id", "00000000-0000-0000-0000-000000000000"),
            new("beta", SettingKind.Boolean, false, "Use the beta endpoint", "false"),
        ],
        [new("clientSecret", true, "Entra client secret")]),
    };
}
