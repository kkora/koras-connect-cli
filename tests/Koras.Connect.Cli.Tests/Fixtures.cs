using System.Net;
using System.Text;

namespace Koras.Connect.Cli.Tests;

/// <summary>Synthetic payloads in each vendor's wire format.</summary>
public static class Fixtures
{
    public static string AdpWorker(int n) =>
        "{\"associateOID\":\"A" + n + "\",\"workerID\":{\"idValue\":\"W" + n + "\"},\"person\":{\"legalName\":{\"givenName\":\"Given" + n + "\",\"familyName1\":\"Family" + n + "\"}},"
        + "\"workerStatus\":{\"statusCode\":{\"codeValue\":\"Active\"}},\"workAssignments\":[{\"itemID\":\"1\",\"jobTitle\":\"Title" + n + "\",\"hireDate\":\"2020-01-0" + n + "\",\"primaryIndicator\":true}]}";

    /// <summary>Serves ADP workers in pages according to $skip/$top; <paramref name="total"/> records overall.</summary>
    public static Func<HttpRequestMessage, HttpResponseMessage?> AdpWorkers(int total, int? failAtSkip = null)
        => req =>
        {
            if (!req.RequestUri!.AbsolutePath.EndsWith("/hr/v2/workers", StringComparison.Ordinal)) return null;
            var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
            var skip = int.Parse(q["$skip"] ?? "0"); var top = int.Parse(q["$top"] ?? "100");
            if (failAtSkip == skip) return FakeVendor.Status(HttpStatusCode.InternalServerError);
            var items = Enumerable.Range(skip + 1, Math.Max(0, Math.Min(top, total - skip))).Select(AdpWorker);
            return FakeVendor.Json("{\"workers\":[" + string.Join(",", items) + "]}");
        };

    public static string Qualtrics(string status, string? fileId = null, double pct = 100)
        => "{\"result\":{\"status\":\"" + status + "\",\"percentComplete\":" + pct.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + (fileId is null ? "" : ",\"fileId\":\"" + fileId + "\"") + "},\"meta\":{\"httpStatus\":\"200 - OK\"}}";

    public static readonly byte[] QualtricsZip = [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03, 0xFF, 0x00, 0x7F];
    public static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
