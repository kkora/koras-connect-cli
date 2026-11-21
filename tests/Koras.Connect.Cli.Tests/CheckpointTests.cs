using System.Net;
using System.Text.Json;
using Koras.Connect.Cli.Checkpoints;
using Koras.Connect.Cli.Cli;

namespace Koras.Connect.Cli.Tests;

public class IncrementalGraphTests
{
    private const string Delta1 = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D1";
    private const string Delta2 = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2";

    private static string Item(string id, string name) => "{\"id\":\"" + id + "\",\"displayName\":\"" + name + "\"}";

    /// <summary>First call: initial delta (two pages). Calls carrying a deltatoken return incremental changes.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> Serve(Action<string>? onRequest = null) => r =>
    {
        var url = r.RequestUri!.ToString(); onRequest?.Invoke(url);
        if (!url.Contains("/v1.0/users/delta")) return null;
        if (url.Contains("deltatoken=D1")) return FakeVendor.Json("{\"value\":[" + Item("u2", "Two Updated") + ",{\"id\":\"u1\",\"@removed\":{\"reason\":\"deleted\"}}],\"@odata.deltaLink\":\"" + Delta2 + "\"}");
        if (url.Contains("deltatoken=D2")) return FakeVendor.Json("{\"value\":[],\"@odata.deltaLink\":\"" + Delta2 + "\"}");
        if (url.Contains("skiptoken")) return FakeVendor.Json("{\"value\":[" + Item("u2", "Two") + "],\"@odata.deltaLink\":\"" + Delta1 + "\"}");
        return FakeVendor.Json("{\"value\":[" + Item("u1", "One") + "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=s1\"}");
    };

    private static string[] Args(TestCli cli, string file, params string[] extra)
        => ["graph", "users", "export", "--profile", "g", "--fields", "id,displayName", "--incremental", "--output", file, .. extra];

    [Fact] public async Task First_run_is_full_export_then_second_run_resumes_from_the_saved_delta_link()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); cli.Vendor.Route = Serve();
        var f1 = cli.Path_("run1.csv"); var f2 = cli.Path_("run2.csv");
        Assert.Equal(0, await cli.Run(Args(cli, f1)));
        Assert.Equal(["\"_change\",\"id\",\"displayName\"", "\"changed\",\"u1\",\"One\"", "\"changed\",\"u2\",\"Two\""], File.ReadAllLines(f1));
        Assert.Contains("initial full export", cli.Stderr);
        Assert.Equal(0, await cli.Run("checkpoint", "list", "--profile", "g")); Assert.Single(cli.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        cli.Vendor.Requests.Clear();
        Assert.Equal(0, await cli.Run(Args(cli, f2)));
        Assert.Contains("deltatoken=D1", cli.Vendor.Requests.First(x => x.Contains("/users/delta")));
        Assert.Equal(["\"_change\",\"id\",\"displayName\"", "\"changed\",\"u2\",\"Two Updated\"", "\"deleted\",\"u1\",\"\""], File.ReadAllLines(f2));
        cli.Vendor.Requests.Clear();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("run3.csv")))); Assert.Contains("deltatoken=D2", cli.Vendor.Requests.First(x => x.Contains("/users/delta")));
    }

    [Fact] public async Task Failure_before_publication_preserves_the_prior_checkpoint_and_leaves_no_file()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); cli.Vendor.Route = Serve();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("run1.csv"))));
        var before = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(cli.Home, "checkpoints"), "*.json", SearchOption.AllDirectories).Single());
        cli.Vendor.Route = r => r.RequestUri!.ToString().Contains("deltatoken") ? FakeVendor.Status(HttpStatusCode.InternalServerError) : null;
        var failed = cli.Path_("failed.csv");
        Assert.Equal(ExitCodes.Connectivity, await cli.Run(Args(cli, failed)));
        Assert.False(File.Exists(failed));
        Assert.Equal(before, File.ReadAllText(Directory.EnumerateFiles(Path.Combine(cli.Home, "checkpoints"), "*.json", SearchOption.AllDirectories).Single()));
        Assert.Empty(Directory.EnumerateFiles(cli.Work, ".*"));
    }

    [Fact] public async Task Cancellation_before_publication_preserves_the_checkpoint()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); cli.Vendor.Route = Serve();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("run1.csv"))));
        var path = Directory.EnumerateFiles(Path.Combine(cli.Home, "checkpoints"), "*.json", SearchOption.AllDirectories).Single(); var before = File.ReadAllText(path);
        using var cts = new CancellationTokenSource(); var inner = Serve(_ => cts.Cancel()); cli.Vendor.Route = inner;
        var file = cli.Path_("cancelled.csv");
        Assert.Equal(ExitCodes.Cancelled, await cli.RunWith(Args(cli, file), cts.Token));
        Assert.False(File.Exists(file)); Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact] public async Task Failure_between_publication_and_checkpoint_save_keeps_the_old_checkpoint_and_replays_next_time()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); cli.Vendor.Route = Serve();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("run1.csv"))));            // checkpoint = D1
        var path = Directory.EnumerateFiles(Path.Combine(cli.Home, "checkpoints"), "*.json", SearchOption.AllDirectories).Single(); var before = File.ReadAllText(path);

        var published = cli.Path_("published.csv");
        Assert.Equal(ExitCodes.CheckpointSave, await cli.RunWith(Args(cli, published), default, s => new FailingSaveStore(s)));
        Assert.True(File.Exists(published));                                         // file WAS published
        Assert.Equal(before, File.ReadAllText(path));                                // checkpoint NOT advanced
        Assert.Contains("replay", cli.Stderr);

        cli.Vendor.Requests.Clear(); var replay = cli.Path_("replay.csv");
        Assert.Equal(0, await cli.Run(Args(cli, replay)));                           // next run replays from D1 => same changes again
        Assert.Contains("deltatoken=D1", cli.Vendor.Requests.First(x => x.Contains("/users/delta")));
        Assert.Equal(File.ReadAllText(published), File.ReadAllText(replay));
    }

    [Fact] public async Task Concurrent_runs_for_the_same_scope_cannot_overwrite_each_others_checkpoint()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); cli.Vendor.Route = Serve();
        var store = new FileCheckpointStore(cli.Home);
        var scope = CheckpointScope.Create("graph", "g", new Dictionary<string, string> { ["fields"] = "id,displayName" });
        using (store.Acquire(scope))
        {
            var file = cli.Path_("blocked.csv");
            Assert.Equal(ExitCodes.CheckpointBusy, await cli.Run(Args(cli, file)));
            Assert.False(File.Exists(file)); Assert.Empty(cli.Vendor.Requests.Where(r => r.Contains("/users/delta")));
            Assert.Contains("already running", cli.Stderr);
        }
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("after.csv"))));          // lock released => runs
    }

    [Fact] public void Lock_blocks_a_second_acquire_in_the_same_process_and_releases_on_dispose()
    {
        var dir = Directory.CreateTempSubdirectory().FullName; var store = new FileCheckpointStore(dir); var scope = new CheckpointScope("graph", "p", "q");
        var l1 = store.Acquire(scope); Assert.Throws<CheckpointBusyException>(() => store.Acquire(scope));
        l1.Dispose(); store.Acquire(scope).Dispose(); Directory.Delete(dir, true);
    }

    [Fact] public async Task Checkpoints_are_scoped_by_connector_profile_and_effective_query()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); await cli.CreateProfile("g2", "graph"); cli.Vendor.Route = Serve();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("a.csv"))));
        Assert.Equal(0, await cli.Run("graph", "users", "export", "--profile", "g", "--fields", "id", "--incremental", "--output", cli.Path_("b.csv")));   // different query
        Assert.Equal(0, await cli.Run("graph", "users", "export", "--profile", "g2", "--fields", "id,displayName", "--incremental", "--output", cli.Path_("c.csv"))); // different profile
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(cli.Home, "checkpoints", "graph", "g"), "*.json").Count());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(cli.Home, "checkpoints", "graph", "g2"), "*.json"));
        // changing only output options does NOT create a new scope
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("d.json"), "--format", "json")));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(cli.Home, "checkpoints", "graph", "g"), "*.json").Count());
    }

    [Fact] public async Task Reset_flag_and_reset_command_restart_with_a_full_export()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); cli.Vendor.Route = Serve();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("a.csv"))));
        cli.Vendor.Requests.Clear();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("b.csv"), "--reset-checkpoint")));
        Assert.Contains("Checkpoint reset", cli.Stderr); Assert.DoesNotContain("deltatoken", cli.Vendor.Requests.First(x => x.Contains("/users/delta")));
        Assert.Equal(0, await cli.Run("checkpoint", "reset", "--profile", "g", "--all")); Assert.Contains("Removed 1", cli.Stderr);
        Assert.Equal(0, await cli.Run("checkpoint", "list", "--profile", "g")); Assert.Equal("", cli.Stdout);
        var id = ""; Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("c.csv"))));
        await cli.Run("checkpoint", "list", "--profile", "g"); id = cli.Stdout.Split('\t')[0];
        Assert.Equal(0, await cli.Run("checkpoint", "reset", "--profile", "g", "--scope", id)); Assert.Contains("Removed 1", cli.Stderr);
        Assert.Equal(ExitCodes.Usage, await cli.Run("checkpoint", "reset", "--profile", "g"));
    }

    [Fact] public async Task Expired_delta_token_tells_the_user_to_reset()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); cli.Vendor.Route = Serve();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("a.csv"))));
        cli.Vendor.Route = r => r.RequestUri!.ToString().Contains("deltatoken") ? FakeVendor.Status(HttpStatusCode.Gone) : null;
        Assert.Equal(ExitCodes.Vendor, await cli.Run(Args(cli, cli.Path_("b.csv")))); Assert.Contains("--reset-checkpoint", cli.Stderr);
    }

    [Fact] public async Task Incremental_rejects_options_that_cannot_apply()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph");
        Assert.Equal(ExitCodes.Usage, await cli.Run("graph", "users", "export", "--profile", "g", "--fields", "id", "--incremental", "--filter", "x eq 1"));
        Assert.Equal(ExitCodes.Usage, await cli.Run("graph", "users", "export", "--profile", "g", "--fields", "id", "--reset-checkpoint"));
    }

    [Fact] public async Task Corrupt_checkpoint_is_reported_without_losing_data()
    {
        using var cli = new TestCli(); await cli.CreateProfile("g", "graph"); cli.Vendor.Route = Serve();
        Assert.Equal(0, await cli.Run(Args(cli, cli.Path_("a.csv"))));
        var path = Directory.EnumerateFiles(Path.Combine(cli.Home, "checkpoints"), "*.json", SearchOption.AllDirectories).Single(); File.WriteAllText(path, "{ not json");
        var file = cli.Path_("b.csv");
        Assert.Equal(ExitCodes.Configuration, await cli.Run(Args(cli, file))); Assert.False(File.Exists(file)); Assert.Contains("corrupt", cli.Stderr);
    }
}

public class IncrementalDataverseTests
{
    private const string Base = "https://org.crm.example.test/api/data/v9.2/accounts";
    private static Func<HttpRequestMessage, HttpResponseMessage?> Serve() => r =>
    {
        var url = r.RequestUri!.ToString();
        if (!url.Contains("/accounts")) return null;
        Assert.Contains("odata.track-changes", string.Join(",", r.Headers.GetValues("Prefer")));
        if (url.Contains("deltatoken=DV1")) return FakeVendor.Json("{\"value\":[{\"name\":\"Changed\"},{\"reason\":\"deleted\",\"id\":\"11111111-1111-1111-1111-111111111111\"}],\"@odata.deltaLink\":\"" + Base + "?$deltatoken=DV2\"}");
        if (url.Contains("skiptoken")) return FakeVendor.Json("{\"value\":[{\"name\":\"B\"}],\"@odata.deltaLink\":\"" + Base + "?$deltatoken=DV1\"}");
        return FakeVendor.Json("{\"value\":[{\"name\":\"A\"}],\"@odata.nextLink\":\"" + Base + "?$skiptoken=p2\"}");
    };

    [Fact] public async Task Change_tracking_walks_pages_to_the_delta_link_then_resumes_and_reports_deletions()
    {
        using var cli = new TestCli(); await cli.CreateProfile("dv", "dataverse"); cli.Vendor.Route = Serve();
        string[] Args(string f) => ["dataverse", "rows", "export", "--profile", "dv", "--table", "accounts", "--fields", "name", "--incremental", "--output", f];
        Assert.Equal(0, await cli.Run(Args(cli.Path_("1.csv"))));
        Assert.Equal(["\"_change\",\"_id\",\"name\"", "\"changed\",\"\",\"A\"", "\"changed\",\"\",\"B\""], File.ReadAllLines(cli.Path_("1.csv")));
        cli.Vendor.Requests.Clear();
        Assert.Equal(0, await cli.Run(Args(cli.Path_("2.csv"))));
        Assert.Contains("deltatoken=DV1", cli.Vendor.Requests.First(x => x.Contains("/accounts")));
        Assert.Equal(["\"_change\",\"_id\",\"name\"", "\"changed\",\"\",\"Changed\"", "\"deleted\",\"11111111-1111-1111-1111-111111111111\",\"\""], File.ReadAllLines(cli.Path_("2.csv")));
    }
    [Fact] public async Task Filter_is_rejected_with_change_tracking()
    {
        using var cli = new TestCli(); await cli.CreateProfile("dv", "dataverse");
        Assert.Equal(ExitCodes.Usage, await cli.Run("dataverse", "rows", "export", "--profile", "dv", "--table", "accounts", "--fields", "name", "--incremental", "--filter", "x eq 1"));
    }
}

public class IncrementalSalesforceTests
{
    [Fact] public async Task First_run_queries_everything_then_later_runs_use_the_saved_SystemModstamp_with_queryAll()
    {
        using var cli = new TestCli(); await cli.CreateProfile("sf", "salesforce");
        var queries = new List<string>();
        cli.Vendor.Route = r =>
        {
            var url = Uri.UnescapeDataString(r.RequestUri!.ToString());
            if (!url.Contains("/query")) return null; queries.Add(url);
            return url.Contains("queryAll")
                ? FakeVendor.Json("{\"totalSize\":1,\"done\":true,\"records\":[{\"Id\":\"001B\",\"Name\":\"Beta2\",\"SystemModstamp\":\"2025-02-01T10:00:00.000+0000\"}]}")
                : FakeVendor.Json("{\"totalSize\":2,\"done\":true,\"records\":[{\"Id\":\"001A\",\"Name\":\"Alpha\",\"SystemModstamp\":\"2025-01-01T08:00:00.000+0000\"},{\"Id\":\"001B\",\"Name\":\"Beta\",\"SystemModstamp\":\"2025-01-05T09:30:00.000+0000\"}]}");
        };
        string[] Args(string f) => ["salesforce", "changes", "export", "--profile", "sf", "--object", "Account", "--fields", "Id,Name", "--output", f];
        Assert.Equal(0, await cli.Run(Args(cli.Path_("1.csv"))));
        Assert.Equal(["\"Id\",\"Name\"", "\"001A\",\"Alpha\"", "\"001B\",\"Beta\""], File.ReadAllLines(cli.Path_("1.csv")));
        Assert.Contains("SELECT Id,Name,SystemModstamp FROM Account ORDER BY SystemModstamp", queries[0]); Assert.DoesNotContain("queryAll", queries[0]);
        Assert.Equal(0, await cli.Run(Args(cli.Path_("2.csv"))));
        Assert.Contains("queryAll", queries[1]); Assert.Contains("SystemModstamp >= 2025-01-05T09:30:00.000Z", queries[1]);
        Assert.Equal(["\"Id\",\"Name\"", "\"001B\",\"Beta2\""], File.ReadAllLines(cli.Path_("2.csv")));
        Assert.Equal(0, await cli.Run(Args(cli.Path_("3.csv")))); Assert.Contains("SystemModstamp >= 2025-02-01T10:00:00.000Z", queries[2]);
    }
    [Fact] public void Salesforce_timestamps_with_compact_offsets_parse()
        => Assert.Equal(new DateTimeOffset(2025, 1, 5, 9, 30, 0, TimeSpan.Zero), Koras.Connect.Cli.Commands.ExportCommands.ParseSalesforceTimestamp("2025-01-05T09:30:00.000+0000"));
}
