using System.Net;
using System.Text.Json;
using Koras.Connect.Adp;
using Koras.Connect.Cli.Cli;
using Koras.Connect.Cli.Profiles;
using Koras.Connect.Exceptions;
using Koras.Connect.Transport;
using SdkAuthentication = Koras.Connect.Exceptions.AuthenticationException;

namespace Koras.Connect.Cli.Connectors;

public enum FailureKind { Configuration, Authentication, Authorization, Connectivity, Destination, Vendor, Cancelled, Other }

public sealed record Failure(int ExitCode, FailureKind Kind, string Message);

/// <summary>Maps exceptions to the documented exit codes. Messages are redacted and never contain response bodies.</summary>
public static class FailureClassifier
{
    public static Failure Classify(Exception ex, Redactor redactor, CancellationToken cancellation = default)
    {
        string R(string m) => redactor.Redact(m.Split('\n')[0]);
        switch (ex)
        {
            case CliException c:
                return new Failure(c.ExitCode, c.ExitCode switch
                {
                    ExitCodes.Configuration => FailureKind.Configuration, ExitCodes.Destination => FailureKind.Destination,
                    ExitCodes.Usage => FailureKind.Configuration, _ => FailureKind.Other
                }, R(c.Message));
            case DeadlineExceededException:
                return new Failure(ExitCodes.Connectivity, FailureKind.Connectivity, "The request deadline elapsed (timeout).");
            case OperationCanceledException when cancellation.IsCancellationRequested:
                return new Failure(ExitCodes.Cancelled, FailureKind.Cancelled, "Cancelled.");
            case TaskCanceledException or TimeoutException:
                return new Failure(ex is TimeoutException ? ExitCodes.Vendor : ExitCodes.Connectivity, ex is TimeoutException ? FailureKind.Vendor : FailureKind.Connectivity,
                    ex is TimeoutException ? R(ex.Message) : "The request timed out.");
            case OperationCanceledException:
                return new Failure(ExitCodes.Cancelled, FailureKind.Cancelled, "Cancelled.");
            case SdkAuthentication:
                return new Failure(ExitCodes.Authentication, FailureKind.Authentication, "Authentication failed: the vendor rejected the credentials. " + CodeSuffix(ex));
            case AuthorizationException:
                return new Failure(ExitCodes.Authorization, FailureKind.Authorization, "Authorization failed: the credentials are valid but not permitted to read this data. " + CodeSuffix(ex));
            case ThrottleException:
                return new Failure(ExitCodes.Vendor, FailureKind.Vendor, "The vendor throttled the request (HTTP 429) after retries. " + CodeSuffix(ex));
            case TransportException:
                return new Failure(ExitCodes.Connectivity, FailureKind.Connectivity, "Connectivity failure: " + R(ex.Message));
            case IntegrationRequestException raw:
                return ByStatus(raw.ResponseStatus);
            case HttpRequestException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException:
                return new Failure(ExitCodes.Connectivity, FailureKind.Connectivity, "Connectivity failure: could not reach the service (network or TLS error).");
            case AdpApiException adp:
                return adp.Category switch
                {
                    "AuthenticationException" => new Failure(ExitCodes.Authentication, FailureKind.Authentication, "Authentication failed: ADP rejected the credentials. " + CodeSuffix(ex)),
                    "AuthorizationException" => new Failure(ExitCodes.Authorization, FailureKind.Authorization, "Authorization failed: ADP denied the request. " + CodeSuffix(ex)),
                    "TransportException" => new Failure(ExitCodes.Connectivity, FailureKind.Connectivity, "Connectivity failure: ADP is unavailable. " + CodeSuffix(ex)),
                    _ => new Failure(ExitCodes.Vendor, FailureKind.Vendor, R(ex.Message)),
                };
            case IntegrationException integration:
                return ByCode(integration);
            case global::Microsoft.Extensions.Options.OptionsValidationException or ArgumentException or UriFormatException:
                return new Failure(ExitCodes.Configuration, FailureKind.Configuration, "Invalid configuration: " + R(ex.Message));
            case JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException:
                return new Failure(ExitCodes.Vendor, FailureKind.Vendor, "Unexpected or failed vendor response: " + R(ex.Message));
            case IOException or UnauthorizedAccessException:
                return new Failure(ExitCodes.Destination, FailureKind.Destination, "File error: " + R(ex.Message));
            default:
                return new Failure(ExitCodes.Failure, FailureKind.Other, "Unexpected error (" + ex.GetType().Name + "): " + R(ex.Message));
        }
    }

    private static string CodeSuffix(Exception ex) => ex is IntegrationException i ? $"[{i.Details.Code}]" : "";

    private static Failure ByStatus(HttpStatusCode status) => (int)status switch
    {
        401 => new Failure(ExitCodes.Authentication, FailureKind.Authentication, "Authentication failed (HTTP 401)."),
        403 => new Failure(ExitCodes.Authorization, FailureKind.Authorization, "Authorization failed (HTTP 403)."),
        408 or 500 or 502 or 503 or 504 => new Failure(ExitCodes.Connectivity, FailureKind.Connectivity, $"Service unavailable (HTTP {(int)status})."),
        _ => new Failure(ExitCodes.Vendor, FailureKind.Vendor, $"Unexpected vendor response (HTTP {(int)status})."),
    };

    private static Failure ByCode(IntegrationException ex)
    {
        var code = ex.Details.Code ?? "";
        if (code is "http_401" or "oauth_http_401" or "oauth_http_400") return new Failure(ExitCodes.Authentication, FailureKind.Authentication, "Authentication failed. " + CodeSuffix(ex));
        if (code is "http_403" or "oauth_http_403") return new Failure(ExitCodes.Authorization, FailureKind.Authorization, "Authorization failed. " + CodeSuffix(ex));
        if (code is "http_408" or "http_500" or "http_502" or "http_503" or "http_504" || code.StartsWith("transport", StringComparison.Ordinal))
            return new Failure(ExitCodes.Connectivity, FailureKind.Connectivity, "Connectivity failure. " + CodeSuffix(ex));
        return new Failure(ExitCodes.Vendor, FailureKind.Vendor, "Vendor error. " + CodeSuffix(ex));
    }
}
