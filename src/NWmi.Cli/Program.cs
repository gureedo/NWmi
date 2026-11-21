using NWmi.Cli;

await using var stdout = Console.OpenStandardOutput();
return await CliApp.RunAsync(args, stdout, Console.Out, Console.Error, new DefaultWmiConnector());
