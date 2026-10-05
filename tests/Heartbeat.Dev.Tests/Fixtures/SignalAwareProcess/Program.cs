if (args.Contains("--burst-exit", StringComparer.Ordinal))
{
    Console.Out.Write(new string('A', 262144));
    Console.Error.Write(new string('B', 262144));
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.WriteLine("ready");
if (args.Length == 2 && args[0] == "--ready-file") File.WriteAllText(args[1], "ready");
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
}
catch (OperationCanceledException)
{
}
return 0;
