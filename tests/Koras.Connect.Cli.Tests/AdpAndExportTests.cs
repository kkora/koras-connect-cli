using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Koras.Connect.Cli.Cli;

namespace Koras.Connect.Cli.Tests;

public class DoctorTests
{
    [Fact] public async Task Doctor_succeeds_and_keeps_diagnostics_on_stderr()
    {
        using var cli = new TestCli(); await cli.CreateProfile("a", "adp"); cli.Vendor.Route = Fixtures.AdpWorkers(3);
        Assert.Equal(0, await cli.Run("doctor", "--profile", "a"));
        Assert.Equal("", cli.Stdout); Assert.Contains("[ok]", cli.Stderr); Assert.Contains("all checks passed", cli.Stderr);
        Assert.Equal(1, cli.Vendor.Count("GET", "/hr/v2/workers"));
        Assert.DoesNotContain("synthetic-secret-value-123", cli.Stderr);
    }
    [Fact] public async Task Doctor_distinguishes_authentication_failure_at_token_endpoint()
    {
        using var cli = new TestCli(); await cli.CreateProfile("a", "adp"); cli.Vendor.TokenStatus = HttpStatusCode.Unauthorized;
        Assert.Equal(ExitCodes.Authentication, await cli.Run("doctor", "--profile", "a"));
        Assert.Contains("[fail] authentication", cli.Stderr); Assert.Equal("", cli.Stdout);
    }
    [Fact] public async Task Doctor_reports_authentication_when_api_returns_401()
    {
        using var cli = new TestCli(); await cli.CreateProfile("a", "adp"); cli.Vendor.Route = _ => FakeVendor.Status(HttpStatusCode.Unauthorized);
        Assert.Equal(ExitCodes.Authentication, await cli.Run("doctor", "--profile", "a")); Assert.Contains("[fail] authentication", cli.Stderr);
    }
    [Fact] public async Task Doctor_reports_authorization_when_api_returns_403()
    {
        using var cli = new TestCli(); await cli.CreateProfile("a", "adp"); cli.Vendor.Route = _ => FakeVendor.Status(HttpStatusCode.Forbidden);
        Assert.Equal(ExitCodes.Authorization, await cli.Run("doctor", "--profile", "a")); Assert.Contains("[fail] authorization", cli.Stderr);
    }
    [Fact] public async Task Doctor_reports_connectivity_when_the_network_fails()
    {
        using var cli = new TestCli(); await cli.CreateProfile("a", "adp"); cli.Vendor.Route = _ => throw new HttpRequestException("connection refused");
        Assert.Equal(ExitCodes.Connectivity, await cli.Run("doctor", "--profile", "a")); Assert.Contains("[fail] connectivity", cli.Stderr);
    }
    [Fact] public async Task Doctor_reports_connectivity_for_server_errors()
    {
        using var cli = new TestCli(); await cli.CreateProfile("a", "adp"); cli.Vendor.Route = _ => FakeVendor.Status(HttpStatusCode.ServiceUnavailable);
        Assert.Equal(ExitCodes.Connectivity, await cli.Run("doctor", "--profile", "a"));
    }
    [Fact] public async Task Doctor_stops_at_configuration_failure_without_any_request()
    {
        using var cli = new TestCli(); await cli.CreateProfile("a", "adp"); cli.Env["T_CLIENT_SECRET"] = null;
        Assert.Equal(ExitCodes.Configuration, await cli.Run("doctor", "--profile", "a"));
        Assert.Empty(cli.Vendor.Requests); Assert.Contains("T_CLIENT_SECRET", cli.Stderr);
    }
}

public class AdpExportTests
{
    private static async Task<(TestCli cli, string profile)> Arrange(int total = 5, int? failAtSkip = null)
    {
        var cli = new TestCli(); await cli.CreateProfile("adp1", "adp"); cli.Vendor.Route = Fixtures.AdpWorkers(total, failAtSkip); return (cli, "adp1");
    }

    [Fact] public async Task Multi_page_export_follows_pagination_and_writes_selected_fields_only()
    {
        var (cli, p) = await Arrange(5); using var _ = cli;
        var file = cli.Path_("workers.csv");
        Assert.Equal(0, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid,GivenName,JobTitle", "--output", file));
        var lines = File.ReadAllLines(file);
        Assert.Equal("\"AssociateOid\",\"GivenName\",\"JobTitle\"", lines[0]);
        Assert.Equal(6, lines.Length); Assert.Equal("\"A5\",\"Given5\",\"Title5\"", lines[5]);
        Assert.Equal(4, cli.Vendor.Count("GET", "/hr/v2/workers"));   // pages of 2,2,1 then the empty terminating page
        Assert.Contains("$skip=5", cli.Vendor.Requests.Last(r => r.Contains("workers")));
        Assert.Equal(file + "\n", cli.Stdout.Replace("\r", "")); Assert.Contains("Exported 5 record(s)", cli.Stderr);
        Assert.DoesNotContain("Family", File.ReadAllText(file));
    }
    [Fact] public async Task Fields_are_required()
    {
        var (cli, p) = await Arrange(); using var _ = cli;
        Assert.Equal(ExitCodes.Usage, await cli.Run("adp", "workers", "export", "--profile", p)); Assert.Contains("--fields", cli.Stderr); Assert.Empty(cli.Vendor.Requests);
    }
    [Theory] [InlineData("Ssn")] [InlineData("AssociateOid,Salary")] public async Task Unknown_or_sensitive_fields_are_rejected_before_any_request(string fields)
    {
        var (cli, p) = await Arrange(); using var _ = cli;
        Assert.Equal(ExitCodes.Usage, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", fields, "--output", cli.Path_("x.csv")));
        Assert.Empty(cli.Vendor.Requests); Assert.Empty(cli.Leftovers(cli.Work).Where(f => !f.EndsWith(".pfx")));
    }
    [Fact] public async Task Json_stdout_contains_only_selected_fields_and_nothing_else_on_stdout()
    {
        var (cli, p) = await Arrange(3); using var _ = cli;
        Assert.Equal(0, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid,WorkerStatus", "--format", "json"));
        using var doc = JsonDocument.Parse(cli.DataStdout);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
        Assert.Equal(["AssociateOid", "WorkerStatus"], doc.RootElement[0].EnumerateObject().Select(x => x.Name));
        Assert.Equal("Active", doc.RootElement[1].GetProperty("WorkerStatus").GetString());
        Assert.Equal("", cli.Stdout); Assert.Contains("Exported 3 record(s) to stdout", cli.Stderr);
    }
    [Fact] public async Task Csv_stdout_is_pure_data()
    {
        var (cli, p) = await Arrange(2); using var _ = cli;
        Assert.Equal(0, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "WorkerId"));
        Assert.Equal("\"WorkerId\"\n\"W1\"\n\"W2\"", cli.DataStdout.Replace("\r", "").Trim());
    }
    [Fact] public async Task Xml_and_xlsx_formats_are_written_by_the_sdk_exporters()
    {
        var (cli, p) = await Arrange(2); using var _ = cli;
        var xml = cli.Path_("w.xml"); var xlsx = cli.Path_("w.xlsx");
        Assert.Equal(0, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid,GivenName", "--format", "xml", "--output", xml));
        Assert.Contains("<field name=\"GivenName\">Given2</field>", File.ReadAllText(xml));
        Assert.Equal(0, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid,GivenName", "--format", "xlsx", "--output", xlsx));
        using var zip = ZipFile.OpenRead(xlsx); Assert.Contains(zip.Entries, e => e.FullName == "xl/worksheets/sheet1.xml");
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()); Assert.Contains("Given2", reader.ReadToEnd());
    }
    [Fact] public async Task Xlsx_to_stdout_is_refused_before_any_request()
    {
        var (cli, p) = await Arrange(); using var _ = cli;
        Assert.Equal(ExitCodes.Usage, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--format", "xlsx"));
        Assert.Empty(cli.Vendor.Requests);
    }
    [Fact] public async Task Unsupported_format_is_a_usage_error()
    {
        var (cli, p) = await Arrange(); using var _ = cli;
        Assert.Equal(ExitCodes.Usage, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--format", "parquet")); Assert.Empty(cli.Vendor.Requests);
    }
    [Fact] public async Task Csv_neutralizes_formula_injection_from_vendor_values()
    {
        var (cli, p) = await Arrange(); using var _ = cli;
        cli.Vendor.Route = r => r.RequestUri!.Query.Contains("$skip=0")
            ? FakeVendor.Json("{\"workers\":[{\"associateOID\":\"=HYPERLINK(1)\"}]}") : FakeVendor.Json("{\"workers\":[]}");
        Assert.Equal(0, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid"));
        Assert.Contains("\"'=HYPERLINK(1)\"", cli.DataStdout);
    }

    // ---- overwrite protection, failure cleanup, cancellation ----
    [Fact] public async Task Existing_file_is_not_overwritten_and_no_request_is_made()
    {
        var (cli, p) = await Arrange(); using var _ = cli; var file = cli.Path_("o.csv"); File.WriteAllText(file, "KEEP");
        Assert.Equal(ExitCodes.Destination, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", file));
        Assert.Equal("KEEP", File.ReadAllText(file)); Assert.Empty(cli.Vendor.Requests); Assert.Contains("--overwrite", cli.Stderr);
    }
    [Fact] public async Task Overwrite_replaces_the_file_atomically_with_no_temp_left_behind()
    {
        var (cli, p) = await Arrange(2); using var _ = cli; var file = cli.Path_("o.csv"); File.WriteAllText(file, "OLD");
        Assert.Equal(0, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", file, "--overwrite"));
        Assert.Contains("\"A2\"", File.ReadAllText(file));
        Assert.Equal([file], cli.Leftovers(cli.Work).Where(f => !f.EndsWith(".pfx")).ToArray());
    }
    [Fact] public async Task Missing_output_directory_is_refused_up_front()
    {
        var (cli, p) = await Arrange(); using var _ = cli;
        Assert.Equal(ExitCodes.Destination, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", Path.Combine(cli.Work, "nope", "x.csv")));
        Assert.Empty(cli.Vendor.Requests);
    }
    [Fact] public async Task Directory_as_output_is_refused()
    {
        var (cli, p) = await Arrange(); using var _ = cli;
        Assert.Equal(ExitCodes.Destination, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", cli.Work));
    }
    [Fact] public async Task Failure_mid_export_leaves_no_partial_file_and_keeps_an_existing_one()
    {
        var (cli, p) = await Arrange(6, failAtSkip: 2); using var _ = cli;
        var fresh = cli.Path_("fresh.csv"); var existing = cli.Path_("existing.csv"); File.WriteAllText(existing, "PRIOR");
        Assert.Equal(ExitCodes.Connectivity, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", fresh));
        Assert.False(File.Exists(fresh));
        Assert.Equal(ExitCodes.Connectivity, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", existing, "--overwrite"));
        Assert.Equal("PRIOR", File.ReadAllText(existing));
        Assert.Equal([existing], cli.Leftovers(cli.Work).Where(f => !f.EndsWith(".pfx")).ToArray());
    }
    [Fact] public async Task Failure_mid_export_writes_nothing_to_stdout()
    {
        var (cli, p) = await Arrange(6, failAtSkip: 2); using var _ = cli;
        Assert.NotEqual(0, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid"));
        Assert.Empty(cli.StdoutBytes); Assert.Equal("", cli.Stdout);
    }
    [Fact] public async Task Cancellation_cleans_up_the_temporary_file_and_exits_130()
    {
        var (cli, p) = await Arrange(10); using var _ = cli; using var cts = new CancellationTokenSource();
        var inner = cli.Vendor.Route;
        cli.Vendor.Route = r => { var res = inner(r); if (r.RequestUri!.Query.Contains("$skip=2")) cts.Cancel(); return res; };
        var file = cli.Path_("c.csv");
        Assert.Equal(ExitCodes.Cancelled, await cli.RunWith(["adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", file], cts.Token));
        Assert.False(File.Exists(file)); Assert.Empty(cli.Leftovers(cli.Work).Where(f => !f.EndsWith(".pfx")));
    }
    [Fact] public async Task Authentication_failure_on_export_exits_4_and_leaves_no_file()
    {
        var (cli, p) = await Arrange(); using var _ = cli; cli.Vendor.Route = _ => FakeVendor.Status(HttpStatusCode.Unauthorized); var file = cli.Path_("a.csv");
        Assert.Equal(ExitCodes.Authentication, await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", file)); Assert.False(File.Exists(file));
    }
    [Fact] public async Task Secrets_never_appear_in_any_output_even_on_failure()
    {
        var (cli, p) = await Arrange(); using var _ = cli; cli.Vendor.Route = _ => FakeVendor.Status(HttpStatusCode.Forbidden);
        await cli.Run("adp", "workers", "export", "--profile", p, "--fields", "AssociateOid", "--output", cli.Path_("s.csv"));
        foreach (var secret in new[] { "synthetic-secret-value-123", "synthetic-cert-password", "synthetic-access-token" }) Assert.DoesNotContain(secret, cli.Stdout + cli.Stderr);
    }
    [Fact] public async Task Wrong_connector_profile_is_a_usage_error()
    {
        using var cli = new TestCli(); await cli.CreateProfile("q", "qualtrics");
        Assert.Equal(ExitCodes.Usage, await cli.Run("adp", "workers", "export", "--profile", "q", "--fields", "AssociateOid"));
    }
}
