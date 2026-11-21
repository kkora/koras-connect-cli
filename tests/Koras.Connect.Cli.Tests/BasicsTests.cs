using Koras.Connect.Cli.Cli;
using Koras.Connect.Cli.Profiles;

namespace Koras.Connect.Cli.Tests;

public class CommandParsingTests
{
    [Fact] public void Parses_positionals_options_and_flags()
    {
        var a = ParsedArgs.Parse(["adp", "workers", "export", "--profile=p1", "--fields", "A,B", "--overwrite", "-o", "out.csv"]);
        Assert.Equal(["adp", "workers", "export"], a.Positionals);
        Assert.Equal("p1", a.Get("profile")); Assert.Equal("A,B", a.Get("fields")); Assert.True(a.Flag("overwrite")); Assert.Equal("out.csv", a.Get("output"));
    }
    [Fact] public void Repeatable_options_collect_and_single_options_reject_duplicates()
    {
        Assert.Equal(["a=1", "b=2"], ParsedArgs.Parse(["--set", "a=1", "--set", "b=2"]).GetAll("set"));
        Assert.Throws<UsageException>(() => ParsedArgs.Parse(["--profile", "a", "--profile", "b"]));
    }
    [Fact] public void Missing_value_is_a_usage_error() => Assert.Throws<UsageException>(() => ParsedArgs.Parse(["--profile"]));
    [Fact] public void Unknown_options_are_rejected()
    {
        var a = ParsedArgs.Parse(["--profile", "p", "--bogus", "x"]); a.Get("profile");
        var ex = Assert.Throws<UsageException>(a.RejectUnknown); Assert.Contains("--bogus", ex.Message);
    }

    [Fact] public async Task Unknown_command_exits_2_with_usage_on_stderr()
    {
        using var cli = new TestCli();
        Assert.Equal(ExitCodes.Usage, await cli.Run("nonsense", "thing"));
        Assert.Equal("", cli.Stdout); Assert.Contains("unknown command", cli.Stderr);
    }
    [Fact] public async Task Unknown_option_on_export_exits_2_before_any_request()
    {
        using var cli = new TestCli(); await cli.CreateProfile("a", "adp");
        Assert.Equal(ExitCodes.Usage, await cli.Run("adp", "workers", "export", "--profile", "a", "--fields", "AssociateOid", "--bogus", "1"));
        Assert.Empty(cli.Vendor.Requests);
    }
}

public class VersionAndHelpTests
{
    [Fact] public async Task Version_prints_tool_name_and_version_to_stdout()
    {
        using var cli = new TestCli();
        Assert.Equal(0, await cli.Run("--version"));
        Assert.StartsWith("koras-connect 0.1.0-preview.1", cli.Stdout.Trim()); Assert.Equal("", cli.Stderr);
    }
    [Fact] public async Task Help_lists_every_shipped_command()
    {
        using var cli = new TestCli();
        Assert.Equal(0, await cli.Run("--help"));
        foreach (var c in new[] { "profile init", "doctor", "adp workers export", "sap persons export", "qualtrics responses export" })
            Assert.Contains(c, cli.Stdout);
        Assert.Contains("Exit codes", cli.Stdout);
    }
    [Fact] public async Task Command_help_exits_0() { using var cli = new TestCli(); Assert.Equal(0, await cli.Run("adp", "workers", "export", "--help")); Assert.Contains("adp workers export", cli.Stdout); }
    [Fact] public async Task No_arguments_prints_usage_and_exits_2() { using var cli = new TestCli(); Assert.Equal(2, await cli.Run()); Assert.Contains("Usage", cli.Stderr); Assert.Equal("", cli.Stdout); }
}

public class ProfileTests
{
    [Fact] public async Task Init_list_validate_round_trip()
    {
        using var cli = new TestCli();
        await cli.CreateProfile("adp-one", "adp"); await cli.CreateProfile("qlt", "qualtrics");
        Assert.Equal(0, await cli.Run("profile", "list")); Assert.Equal("adp-one\tadp\nqlt\tqualtrics", cli.Stdout.Replace("\r", "").Trim());
        Assert.Equal(0, await cli.Run("profile", "validate", "adp-one")); Assert.Contains("valid", cli.Stdout);
    }
    [Fact] public async Task Profile_file_stores_references_never_secret_values()
    {
        using var cli = new TestCli(); await cli.CreateProfile("adp-one", "adp");
        var text = File.ReadAllText(Path.Combine(cli.Home, "profiles", "adp-one.json"));
        Assert.Contains("env:T_CLIENT_SECRET", text);
        foreach (var secret in new[] { "synthetic-secret-value-123", "synthetic-cert-password", "synthetic-client-id" }) Assert.DoesNotContain(secret, text);
    }
    [Fact] public async Task Literal_secret_as_credential_is_rejected_without_echoing_it()
    {
        using var cli = new TestCli();
        var code = await cli.Run("profile", "init", "p", "--connector", "qualtrics", "--credential", "apiToken=SuperSecretLiteral999");
        Assert.Equal(ExitCodes.Usage, code); Assert.DoesNotContain("SuperSecretLiteral999", cli.Stderr + cli.Stdout);
        Assert.False(File.Exists(Path.Combine(cli.Home, "profiles", "p.json")));
    }
    [Fact] public async Task Validate_reports_every_missing_setting_and_credential()
    {
        using var cli = new TestCli();
        Assert.Equal(0, await cli.Run("profile", "init", "bad", "--connector", "adp"));
        Assert.Equal(ExitCodes.Configuration, await cli.Run("profile", "validate", "bad"));
        foreach (var m in new[] { "apiRoot", "tokenEndpoint", "certificatePath", "clientId", "clientSecret" }) Assert.Contains(m, cli.Stderr);
    }
    [Fact] public async Task Validate_reports_unresolvable_references_by_name_without_values()
    {
        using var cli = new TestCli(); await cli.CreateProfile("p", "qualtrics"); cli.Env["T_API_TOKEN"] = null;
        Assert.Equal(ExitCodes.Configuration, await cli.Run("profile", "validate", "p"));
        Assert.Contains("T_API_TOKEN", cli.Stderr);
        cli.Env["T_API_TOKEN"] = "synthetic-qualtrics-token-xyz";
        Assert.Equal(0, await cli.Run("profile", "validate", "p")); Assert.DoesNotContain("synthetic-qualtrics-token-xyz", cli.Stderr + cli.Stdout);
    }
    [Fact] public async Task Validate_rejects_http_endpoints_and_urls_with_credentials()
    {
        using var cli = new TestCli();
        await cli.Run("profile", "init", "q", "--connector", "qualtrics", "--set", "apiRoot=http://dc.example.test/API/v3", "--credential", "apiToken=env:T_API_TOKEN");
        Assert.Equal(ExitCodes.Configuration, await cli.Run("profile", "validate", "q")); Assert.Contains("https", cli.Stderr);
    }
    [Fact] public async Task Existing_profile_needs_force() 
    {
        using var cli = new TestCli(); await cli.CreateProfile("p", "qualtrics");
        Assert.Equal(ExitCodes.Usage, await cli.Run("profile", "init", "p", "--connector", "qualtrics"));
        Assert.Equal(0, await cli.Run("profile", "init", "p", "--connector", "qualtrics", "--force"));
    }
    [Theory] [InlineData("../evil")] [InlineData("a/b")] [InlineData(".hidden")] public async Task Unsafe_profile_names_are_rejected(string name)
    { using var cli = new TestCli(); Assert.Equal(ExitCodes.Usage, await cli.Run("profile", "init", name, "--connector", "qualtrics")); }
    [Fact] public async Task Unknown_profile_is_a_configuration_error() { using var cli = new TestCli(); Assert.Equal(ExitCodes.Configuration, await cli.Run("doctor", "--profile", "nope")); }
}

public class RedactionTests
{
    [Fact] public void Redactor_masks_registered_secrets_longest_first()
    {
        var r = new Redactor(); r.Register("secret"); r.Register("secret-longer");
        Assert.Equal("a [REDACTED] b [REDACTED]", r.Redact("a secret-longer b secret"));
    }
    [Fact] public void File_credentials_are_trimmed_and_registered()
    {
        var dir = Directory.CreateTempSubdirectory().FullName; var f = Path.Combine(dir, "s.txt"); File.WriteAllText(f, "file-secret-value\n");
        var r = new Redactor(); var res = new CredentialResolver(_ => null, r);
        Assert.Equal("file-secret-value", res.TryResolve("file:" + f, out _)); Assert.DoesNotContain("file-secret-value", r.Redact("x file-secret-value y"));
        Directory.Delete(dir, true);
    }
    [Fact] public void Missing_file_problem_does_not_contain_content() { var res = new CredentialResolver(_ => null, new Redactor()); Assert.Null(res.TryResolve("file:/nonexistent/x", out var p)); Assert.Contains("cannot be read", p); }
}
