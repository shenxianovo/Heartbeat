using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace Orisumi.Dev;

internal enum Outcome
{
    Passed,
    Failed,
    Cancelled,
    Skipped,
    Running,
    Attention,
    Waiting,
}

/// 所有命令共用的终端写法：状态记号、标题、对齐、相对路径和耗时。
/// 颜色只加强记号；输出被重定向或设置了 NO_COLOR 时不着色，也不显示动画。
internal static class Terminal
{
    public const string Dot = " [grey]·[/] ";

    public static IAnsiConsole Create(TextWriter writer)
    {
        var terminal = IsTerminal(writer);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(terminal ? writer : new TrailingSpaceTrimmer(writer)),
            Ansi = terminal ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = terminal && !NoColor ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = terminal ? InteractionSupport.Detect : InteractionSupport.No,
        });
        // 重定向到文件或管道时，不按默认的 80 列折断路径。
        if (!terminal) console.Profile.Width = 240;
        return console;
    }

    private static bool NoColor => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    /// 只有真正的终端才着色和刷新；TERM=dumb 和 CI 环境按普通文本输出，日志里不会留下重绘的行。
    private static bool IsTerminal(TextWriter writer) =>
        Environment.GetEnvironmentVariable("TERM") != "dumb"
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
        && (ReferenceEquals(writer, Console.Out) ? !Console.IsOutputRedirected
            : ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected);

    public static string Esc(string text) => Markup.Escape(text);

    public static string Mark(Outcome outcome) => outcome switch
    {
        Outcome.Passed => "[green]✓[/]",
        Outcome.Failed => "[red]✗[/]",
        Outcome.Cancelled => "[yellow]⊘[/]",
        Outcome.Skipped => "[grey]⊘[/]",
        Outcome.Running => "[green]●[/]",
        Outcome.Attention => "[yellow]●[/]",
        _ => "[grey]○[/]",
    };

    public static Outcome FromExitCode(int exitCode) => exitCode switch
    {
        0 => Outcome.Passed,
        130 => Outcome.Cancelled,
        _ => Outcome.Failed,
    };

    public static string Verdict(Outcome outcome, string text) => outcome switch
    {
        Outcome.Passed => $"[green]{Esc(text)}[/]",
        Outcome.Failed => $"[red]{Esc(text)}[/]",
        Outcome.Cancelled or Outcome.Attention => $"[yellow]{Esc(text)}[/]",
        _ => $"[grey]{Esc(text)}[/]",
    };

    /// 第一行：主题 · 范围 · 结论。各部分已是 markup。
    public static void Heading(IAnsiConsole console, string subject, params string[] details)
    {
        console.MarkupLine(string.Join(Dot, [$"[bold]{Esc(subject)}[/]", .. details.Where(part => part.Length > 0)]));
        console.WriteLine();
    }

    /// 左边标签、右边值的两列；值是 markup，可以有多行。
    public static void Fields(IAnsiConsole console, IEnumerable<(string Label, string Value)> rows)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().PadLeft(2).PadRight(2).NoWrap())
            .AddColumn(new GridColumn().PadLeft(0).PadRight(0));
        foreach (var (label, value) in rows) grid.AddRow(new Markup($"[grey]{Esc(label)}[/]"), new Markup(value));
        console.Write(grid);
    }

    /// 无边框、无表头的对齐表格。单元格是 markup；<paramref name="rightAligned"/> 是右对齐的列。
    public static void Rows(IAnsiConsole console, IEnumerable<string[]> rows, params int[] rightAligned)
    {
        var materialized = rows.ToArray();
        if (materialized.Length == 0) return;
        var grid = new Grid();
        for (var column = 0; column < materialized.Max(row => row.Length); column++)
        {
            var definition = new GridColumn().PadLeft(column == 0 ? 2 : 0).PadRight(3).NoWrap();
            if (rightAligned.Contains(column)) definition.RightAligned();
            grid.AddColumn(definition);
        }
        foreach (var row in materialized) grid.AddRow(row.Select(cell => new Markup(cell)).ToArray<Spectre.Console.Rendering.IRenderable>());
        console.Write(grid);
    }

    public static void Hint(IAnsiConsole console, string command) =>
        console.MarkupLine($"  [grey]→[/] [cyan]{Esc(command)}[/]");

    public static void Error(TextWriter writer, string message) =>
        Create(writer).MarkupLine($"[red]✗[/] {Esc(message)}");

    /// 仓库内的路径写成相对路径，主目录写成 ~。
    public static string Path(RepositoryContext repository, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var relative = System.IO.Path.GetRelativePath(repository.Root, full);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
            return relative == "." ? "." : relative.Replace('\\', '/');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && full.StartsWith(home + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "~" + full[home.Length..] : full;
    }

    public static string Duration(TimeSpan elapsed) => elapsed.TotalSeconds switch
    {
        < 1 => "<1s",
        < 60 => $"{(int)elapsed.TotalSeconds}s",
        < 3600 => $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s",
        _ => $"{(int)elapsed.TotalHours}h{elapsed.Minutes:00}m",
    };

    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Delta(int value) => value switch
    {
        > 0 => "+" + Number(value),
        < 0 => Number(value),
        _ => "±0",
    };

    /// 支持 ANSI 的交互终端里原地刷新转圈和已用时间；其余情况直接运行，不输出进度。
    /// 不支持 ANSI 时 Spectre 会把每次刷新另起一行，日志里因此会重复很多遍。
    public static async Task<T> SpinAsync<T>(IAnsiConsole console, string status, Func<Task<T>> action)
    {
        if (!console.Profile.Capabilities.Interactive || !console.Profile.Capabilities.Ansi) return await action();
        var clock = Stopwatch.StartNew();
        // 每秒只重画一次：读原始字节的伪终端里，每秒最多多出一帧。
        return await console.Status()
            .AutoRefresh(false)
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey))
            .StartAsync(status, async context =>
            {
                var task = action();
                context.Refresh();
                while (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1))) != task)
                {
                    context.Status($"{status} [grey]{Duration(clock.Elapsed)}[/]");
                    context.Refresh();
                }
                return await task;
            });
    }

    public static async Task SpinAsync(IAnsiConsole console, string status, Func<Task> action) =>
        await SpinAsync(console, status, async () => { await action(); return 0; });
}

/// 表格按列宽补齐空格；写到文件或管道时去掉行尾的这些空格。
internal sealed class TrailingSpaceTrimmer(TextWriter inner) : TextWriter
{
    private int pendingSpaces;

    public override System.Text.Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        if (value == ' ')
        {
            pendingSpaces++;
            return;
        }
        if (value is not ('\n' or '\r') && pendingSpaces > 0) inner.Write(new string(' ', pendingSpaces));
        pendingSpaces = 0;
        inner.Write(value);
    }

    public override void Write(string? value)
    {
        foreach (var character in value ?? "") Write(character);
    }

    public override void Flush() => inner.Flush();
}
