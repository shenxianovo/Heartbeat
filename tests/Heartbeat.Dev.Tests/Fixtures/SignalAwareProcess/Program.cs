using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.WriteLine("ready");
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
}
catch (OperationCanceledException)
{
}
return 0;
