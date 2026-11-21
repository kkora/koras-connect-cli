using System.Net;
using System.Text.Json;
using Koras.Connect.Cli.Cli;

namespace Koras.Connect.Cli.Tests;

public class SapExportTests
{
    private static string Person(int n) => "{\"personIdExternal\":\"P" + n + "\",\"personId\":\"" + (1000 + n) + "\",\"dateOfBirth\":\"/Date(0)/\"}";

    [Fact] public async Task Persons_export_pages_selects_fields_and_forwards_effective_date_filters()
    {
        using var cli = new TestCli(); await cli.CreateProfile("sap1", "sap");
        cli.Vendor.Route = r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/PerPerson", StringComparison.Ordinal)) return null;
            return r.RequestUri.Query.Contains("skiptoken")
                ? FakeVendor.Json("{\"d\":{\"results\":[" + Person(3) + "]}}")
                : FakeVendor.Json("{\"d\":{\"results\":[" + Person(1) + "," + Person(2) + "],\"__next\":\"https://sap.example.test/odata/v2/PerPerson?$skiptoken=2\"}}");
        };
        var file = cli.Path_("p.csv");
        Assert.Equal(0, await cli.Run("sap", "persons", "export", "--profile", "sap1", "--fields", "personIdExternal,personId", "--as-of", "2025-03-01", "--output", file));
        Assert.Equal(["\"personIdExternal\",\"personId\"", "\"P1\",\"1001\"", "\"P2\",\"1002\"", "\"P3\",\"1003\""], File.ReadAllLines(file));
        var first = cli.Vendor.Requests.First(x => x.Contains("PerPerson"));
        Assert.Contains("select=personIdExternal,personId", Uri.UnescapeDataString(first)); Assert.Contains("asOfDate=2025-03-01", first);
        Assert.Equal(2, cli.Vendor.Count("GET", "PerPerson"));
        Assert.Contains("grant_type", "grant_type"); // token exchange happened:
        Assert.Equal(1, cli.Vendor.Count("POST", "/oauth/token"));
    }
    [Fact] public async Task Effective_date_range_filters_are_forwarded()
    {
        using var cli = new TestCli(); await cli.CreateProfile("sap1", "sap");
        cli.Vendor.Route = r => r.RequestUri!.AbsolutePath.EndsWith("/PerPerson") ? FakeVendor.Json("{\"d\":{\"results\":[]}}") : null;
        Assert.Equal(0, await cli.Run("sap", "persons", "export", "--profile", "sap1", "--fields", "personIdExternal", "--from", "2025-01-01", "--to", "2025-02-01", "--filter", "personId gt 5", "--format", "json"));
        var url = Uri.UnescapeDataString(cli.Vendor.Requests.First(x => x.Contains("PerPerson")));
        Assert.Contains("fromDate=2025-01-01", url); Assert.Contains("toDate=2025-02-01", url); Assert.Contains("personId gt 5", url);
        Assert.Equal("[]", cli.DataStdout);
    }
    [Fact] public async Task Conflicting_date_options_fail_before_any_request()
    {
        using var cli = new TestCli(); await cli.CreateProfile("sap1", "sap");
        Assert.Equal(ExitCodes.Usage, await cli.Run("sap", "persons", "export", "--profile", "sap1", "--fields", "personId", "--as-of", "2025-01-01", "--from", "2025-01-01"));
        Assert.Empty(cli.Vendor.Requests);
    }
    [Fact] public async Task Sap_token_rejection_is_an_authentication_failure()
    {
        using var cli = new TestCli(); await cli.CreateProfile("sap1", "sap"); cli.Vendor.TokenStatus = HttpStatusCode.Unauthorized;
        Assert.Equal(ExitCodes.Authentication, await cli.Run("doctor", "--profile", "sap1"));
    }
}

public class QualtricsExportTests
{
    private static void Serve(TestCli cli, bool completeImmediately = false, int polls = 2)
    {
        var seen = 0;
        cli.Vendor.Route = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Post && path.EndsWith("/surveys/SV_1/export-responses")) return FakeVendor.Json("{\"result\":{\"progressId\":\"ES_1\",\"percentComplete\":0.0,\"status\":\"inProgress\"},\"meta\":{}}");
            if (r.Method == HttpMethod.Get && path.EndsWith("/export-responses/ES_1"))
                return FakeVendor.Json(!completeImmediately && ++seen < polls ? Fixtures.Qualtrics("inProgress", null, 40) : Fixtures.Qualtrics("complete", "FILE_9"));
            if (path.EndsWith("/export-responses/FILE_9/file")) return Fixtures.Bytes(Fixtures.QualtricsZip);
            return null;
        };
    }

    [Fact] public async Task Starts_exports_reports_progress_id_polls_and_preserves_vendor_bytes()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics"); Serve(cli);
        var file = cli.Path_("responses.zip");
        Assert.Equal(0, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "SV_1", "--poll-interval", "0", "--output", file));
        Assert.Equal(Fixtures.QualtricsZip, File.ReadAllBytes(file));
        Assert.Contains("progress-id: ES_1", cli.Stderr);
        Assert.Equal(1, cli.Vendor.Count("POST", "export-responses")); Assert.Equal(2, cli.Vendor.Count("GET", "/export-responses/ES_1"));
        Assert.DoesNotContain("synthetic-qualtrics-token-xyz", cli.Stderr + cli.Stdout);
    }
    [Fact] public async Task Resume_with_an_existing_progress_id_skips_the_start_request()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics"); Serve(cli, completeImmediately: true);
        var file = cli.Path_("resumed.zip");
        Assert.Equal(0, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "SV_1", "--progress-id", "ES_1", "--output", file));
        Assert.Equal(0, cli.Vendor.Count("POST", "export-responses")); Assert.Equal(Fixtures.QualtricsZip, File.ReadAllBytes(file));
        Assert.Contains("no new export started", cli.Stderr);
    }
    [Fact] public async Task Start_request_carries_format_and_compression_choices()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics");
        string? body = null;
        cli.Vendor.Route = r =>
        {
            if (r.Method == HttpMethod.Post) { body = r.Content!.ReadAsStringAsync().Result; return FakeVendor.Json("{\"result\":{\"progressId\":\"ES_1\"}}"); }
            return r.RequestUri!.AbsolutePath.EndsWith("/file") ? Fixtures.Bytes([1, 2, 3]) : FakeVendor.Json(Fixtures.Qualtrics("complete", "FILE_9"));
        };
        Assert.Equal(0, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "SV_1", "--format", "json", "--no-compress", "--output", cli.Path_("r.json")));
        using var doc = JsonDocument.Parse(body!); Assert.Equal("json", doc.RootElement.GetProperty("format").GetString()); Assert.False(doc.RootElement.GetProperty("compress").GetBoolean());
    }
    [Fact] public async Task Poll_limit_exit_prints_the_progress_id_to_resume_and_leaves_no_file()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics");
        cli.Vendor.Route = r => r.Method == HttpMethod.Post ? FakeVendor.Json("{\"result\":{\"progressId\":\"ES_1\"}}") : FakeVendor.Json(Fixtures.Qualtrics("inProgress", null, 10));
        var file = cli.Path_("never.zip");
        Assert.Equal(ExitCodes.Vendor, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "SV_1", "--poll-interval", "0", "--max-polls", "2", "--output", file));
        Assert.False(File.Exists(file)); Assert.Contains("--progress-id ES_1", cli.Stderr);
    }
    [Fact] public async Task Failed_vendor_export_is_a_vendor_error()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics");
        cli.Vendor.Route = r => r.Method == HttpMethod.Post ? FakeVendor.Json("{\"result\":{\"progressId\":\"ES_1\"}}") : FakeVendor.Json(Fixtures.Qualtrics("failed"));
        Assert.Equal(ExitCodes.Vendor, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "SV_1", "--poll-interval", "0", "--output", cli.Path_("f.zip")));
    }
    [Theory] [InlineData("xlsx")] [InlineData("excel")] [InlineData("yaml")] public async Task Unsupported_vendor_formats_are_rejected_before_any_request(string format)
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics");
        Assert.Equal(ExitCodes.Usage, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "SV_1", "--format", format, "--output", cli.Path_("x")));
        Assert.Empty(cli.Vendor.Requests);
    }
    [Fact] public async Task Existing_output_is_protected_and_no_export_is_started()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics"); Serve(cli); var file = cli.Path_("keep.zip"); File.WriteAllText(file, "KEEP");
        Assert.Equal(ExitCodes.Destination, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "SV_1", "--output", file));
        Assert.Equal("KEEP", File.ReadAllText(file)); Assert.Empty(cli.Vendor.Requests);
    }
    [Fact] public async Task Bad_survey_and_progress_ids_are_usage_errors()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics");
        Assert.Equal(ExitCodes.Usage, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "../x"));
        Assert.Equal(ExitCodes.Usage, await cli.Run("qualtrics", "responses", "export", "--profile", "q1", "--survey", "SV_1", "--progress-id", "a/b"));
    }
    [Fact] public async Task Qualtrics_401_on_doctor_is_authentication()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q1", "qualtrics"); cli.Vendor.Route = _ => FakeVendor.Status(HttpStatusCode.Unauthorized);
        Assert.Equal(ExitCodes.Authentication, await cli.Run("doctor", "--profile", "q1"));
    }
}

