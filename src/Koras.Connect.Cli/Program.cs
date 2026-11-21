using Koras.Connect.Cli.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
return await CliApp.RunAsync(args, CliEnvironment.Default(cts.Token));
