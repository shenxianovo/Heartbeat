namespace Heartbeat.Dev;

// Use the same accessibility controls a user operates; never seed Desktop settings or credentials.
internal sealed class MacDesktopDriver(RepositoryContext repository, ScenarioConfiguration configuration, int processId)
{
    public async Task StartCollectionAsync(CancellationToken token)
    {
        await PressAsync("开始采集", token);
        await WaitForCollectionAsync(token);
        // Deliberately observe the controlled app for the witness interval, rather than
        // relying on the automation host/user to leave it foreground after the click.
        await RunAsync("observe", "", null, token);
    }
    public Task WaitForCollectionAsync(CancellationToken token) => WaitAsync("暂停采集", token);
    public async Task PauseCollectionAsync(CancellationToken token)
    {
        await PressAsync("暂停采集", token);
        await WaitAsync("开始采集", token);
    }

    private Task PressAsync(string label, CancellationToken token) => RunAsync("press", label, null, token);
    private Task WaitAsync(string label, CancellationToken token) => RunAsync("wait", label, null, token);
    public Task QuitAsync(CancellationToken token) => RunAsync("quit", "", null, token);

    public async Task<bool> IsForegroundAsync(CancellationToken token)
    {
        // Inspect only the controlled PID. Do not activate it or export the foreground app's identity.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var result = await ProcessRunner.CaptureAsync(repository.Root, "/usr/bin/osascript",
            ["-e", ProcessIdentityScript + "\non run arguments\nset targetPid to (item 1 of arguments) as integer\n" +
                "set processIdentity to my identityFor(targetPid)\ntell application \"System Events\" to get frontmost of application process id processIdentity\nend run",
                processId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            timeout.Token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Cannot inspect the controlled desktop process.");
        return bool.Parse(result.StdOut.Trim());
    }

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
        ScenarioProcessResult result;
        try { result = await process.WaitAsync(TimeSpan.FromSeconds(45), token); }
        catch (TimeoutException exception)
        { throw new TimeoutException($"Native UI {action} timed out for '{label}'.", exception); }
        if (result.ExitCode != 0) throw new InvalidOperationException($"Native UI {action} failed for '{label}'. Check macOS Accessibility/Automation permissions and the desktop window.");
    }

    // Filtered process references can resolve by executable name when multiple builds are running.
    // Read numeric identities through indexed processes, then verify the PID before returning.
    private const string ProcessIdentityScript = """
        on identityFor(targetPid)
            tell application "System Events"
                set processPids to unix id of application processes
                repeat with processIndex from 1 to count of processPids
                    if item processIndex of processPids is targetPid then
                        set processIdentity to id of application process processIndex
                        if unix id of application process id processIdentity is targetPid then return processIdentity
                    end if
                end repeat
            end tell
            error "Controlled native process unavailable"
        end identityFor

        """;

    private const string Script = ProcessIdentityScript + """
        on run arguments
            set operation to item 1 of arguments
            set targetPid to (item 2 of arguments) as integer
            set labelText to item 3 of arguments
            tell application "System Events"
                repeat 120 times
                    try
                        set processIdentity to my identityFor(targetPid)
                        tell application process id processIdentity
                            set frontmost to true
                            if operation is "quit" then
                                keystroke "q" using command down
                                return
                            end if
                            if operation is "observe" then
                                repeat 12 times
                                    set frontmost to true
                                    delay 0.25
                                end repeat
                                return
                            end if
                            repeat with elementIndex from 1 to count of UI elements of window "Heartbeat Dev"
                                set elementName to name of UI element elementIndex of window "Heartbeat Dev"
                                set elementDescription to description of UI element elementIndex of window "Heartbeat Dev"
                                set elementRole to role of UI element elementIndex of window "Heartbeat Dev"
                                if elementName is labelText or elementDescription is labelText then
                                    if operation is "wait" then return
                                    if operation is "press" and (elementRole is "AXButton" or elementRole is "AXCheckBox") and enabled of UI element elementIndex of window "Heartbeat Dev" then
                                        perform action "AXPress" of UI element elementIndex of window "Heartbeat Dev"
                                        return
                                    end if
                                    if operation is "fill" and elementRole is "AXTextField" then
                                        set value of UI element elementIndex of window "Heartbeat Dev" to system attribute "HEARTBEAT_UI_VALUE"
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
