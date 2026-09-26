namespace Heartbeat.Dev;

// Use the same accessibility controls a user operates; never seed Desktop settings or credentials.
internal sealed class MacDesktopDriver(RepositoryContext repository, ScenarioConfiguration configuration, int processId)
{
    public Task StartCollectionAsync(CancellationToken token) => PressAsync("开始采集", token);
    public Task WaitForCollectionAsync(CancellationToken token) => WaitAsync("暂停采集", token);
    public async Task PauseCollectionAsync(CancellationToken token)
    {
        await PressAsync("暂停采集", token);
        await WaitAsync("开始采集", token);
    }

    private Task PressAsync(string label, CancellationToken token) => RunAsync("press", label, null, token);
    private Task WaitAsync(string label, CancellationToken token) => RunAsync("wait", label, null, token);
    public Task QuitAsync(CancellationToken token) => RunAsync("quit", "", null, token);

    public async Task ConfigureAsync(Uri web, CancellationToken token)
    {
        await PressAsync("连接设置", token);
        await RunAsync("fill", "后端地址", web.AbsoluteUri, token);
        await RunAsync("fill", "Auth 地址", configuration.Authority, token);
        await RunAsync("fill", "Web 时间线地址", web.AbsoluteUri, token);
        await RunAsync("fill", "API key", configuration.ApiKey, token);
        await PressAsync("验证并保存", token);
        await WaitAsync("已验证", token);
        await PressAsync("采集状态", token);
    }

    private async Task RunAsync(string action, string label, string? value, CancellationToken token)
    {
        await using var process = new ScenarioProcess(repository.Root, "/usr/bin/osascript",
            ["-e", Script, action, processId.ToString(System.Globalization.CultureInfo.InvariantCulture), label],
            new Dictionary<string, string?> { ["HEARTBEAT_UI_VALUE"] = value });
        var result = await process.WaitAsync(TimeSpan.FromSeconds(45), token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Native UI {action} failed for '{label}'. Check macOS Accessibility/Automation permissions and the desktop window.");
    }

    private const string Script = """
        on run arguments
            set operation to item 1 of arguments
            set targetPid to (item 2 of arguments) as integer
            set labelText to item 3 of arguments
            tell application "System Events"
                repeat 120 times
                    try
                        set processIdentity to id of (first application process whose unix id is targetPid)
                        tell application process id processIdentity
                            set frontmost to true
                            if operation is "quit" then
                                keystroke "q" using command down
                                return
                            end if
                            repeat with elementIndex from 1 to count of UI elements of window 1
                                set elementName to name of UI element elementIndex of window 1
                                set elementDescription to description of UI element elementIndex of window 1
                                set elementRole to role of UI element elementIndex of window 1
                                if elementName is labelText or elementDescription is labelText then
                                    if operation is "wait" then return
                                    if operation is "press" and (elementRole is "AXButton" or elementRole is "AXCheckBox") and enabled of UI element elementIndex of window 1 then
                                        perform action "AXPress" of UI element elementIndex of window 1
                                        return
                                    end if
                                    if operation is "fill" and elementRole is "AXTextField" then
                                        set value of UI element elementIndex of window 1 to system attribute "HEARTBEAT_UI_VALUE"
                                        return
                                    end if
                                end if
                            end repeat
                        end tell
                    end try
                    delay 0.25
                end repeat
            end tell
            error "Native control unavailable"
        end run
        """;
}
