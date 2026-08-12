using System.IO;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Heartbeat.Desktop.UI.Logging;

/// <summary>Bounded Serilog feed used by the shared desktop presentation.</summary>
public sealed class RingBufferSink : ILogEventSink, ILogFeed
{
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private readonly LogEntry[] _ring;
    private readonly MessageTemplateTextFormatter _formatter;
    private int _head;
    private int _count;
    private long _totalWrites;
    private long _lastNotifiedAt;
    private DateTime _lastNotify = DateTime.MinValue;
    private bool _pendingNotify;

    public RingBufferSink(
        int capacity = 200,
        string outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    {
        _capacity = capacity;
        _ring = new LogEntry[capacity];
        _formatter = new MessageTemplateTextFormatter(outputTemplate);
    }

    public int Capacity => _capacity;
    public event Action<IReadOnlyList<LogEntry>>? Changed;

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        var entry = new LogEntry(writer.ToString().TrimEnd(), logEvent.Level);
        var notifyNow = false;

        lock (_gate)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
            _totalWrites++;

            var now = DateTime.UtcNow;
            if (now - _lastNotify >= TimeSpan.FromMilliseconds(500))
            {
                notifyNow = true;
                _lastNotify = now;
                _pendingNotify = false;
            }
            else if (!_pendingNotify)
            {
                _pendingNotify = true;
                _ = NotifyLaterAsync();
            }
        }

        if (notifyNow) NotifyChanged();
    }

    public IReadOnlyList<LogEntry> GetAll()
    {
        lock (_gate)
        {
            _lastNotifiedAt = _totalWrites;
            return ExtractRecent(_count);
        }
    }

    private async Task NotifyLaterAsync()
    {
        await Task.Delay(500).ConfigureAwait(false);
        lock (_gate)
        {
            _pendingNotify = false;
            _lastNotify = DateTime.UtcNow;
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        IReadOnlyList<LogEntry> delta;
        lock (_gate)
        {
            var newCount = (int)Math.Min(_totalWrites - _lastNotifiedAt, _count);
            if (newCount <= 0) return;
            delta = ExtractRecent(newCount);
            _lastNotifiedAt = _totalWrites;
        }

        try { Changed?.Invoke(delta); }
        catch { }
    }

    private LogEntry[] ExtractRecent(int count)
    {
        var result = new LogEntry[count];
        var start = ((_head - count) % _capacity + _capacity) % _capacity;
        for (var index = 0; index < count; index++)
            result[index] = _ring[(start + index) % _capacity];
        return result;
    }
}

public static class RingBufferSinkExtensions
{
    public static LoggerConfiguration RingBuffer(
        this LoggerSinkConfiguration configuration,
        RingBufferSink sink,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose) =>
        configuration.Sink(sink, restrictedToMinimumLevel);
}
