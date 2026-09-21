using System.Text;

namespace Heartbeat.Dev;

internal interface ISetupInput
{
    Task<string> ReadAsync(string prompt, bool secret, CancellationToken token);
}

internal sealed class SetupConsole(TextWriter output) : ISetupInput
{
    public async Task<string> ReadAsync(string prompt, bool secret, CancellationToken token)
    {
        await output.WriteAsync(prompt + " ");
        await output.FlushAsync(token);
        var value = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable)
            {
                await Task.Delay(50, token);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                await output.WriteLineAsync();
                return value.ToString();
            }
            Edit(value, key, secret);
        }
    }

    private void Edit(StringBuilder value, ConsoleKeyInfo key, bool secret)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length == 0) return;
            value.Length--;
            if (!secret) output.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            value.Append(key.KeyChar);
            if (!secret) output.Write(key.KeyChar);
        }
    }
}
