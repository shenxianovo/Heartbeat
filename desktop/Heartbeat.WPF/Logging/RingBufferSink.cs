using System.IO;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Heartbeat.WPF.Logging
{
    /// <summary>
    /// 环形缓冲区 Serilog Sink，用于 UI 日志显示。
    /// 限制最大条数，节流 UI 刷新。
    /// </summary>
    public class RingBufferSink : ILogEventSink
    {
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly List<string> _buffer;
        private readonly MessageTemplateTextFormatter _formatter;

        private DateTime _lastNotify = DateTime.MinValue;
        private bool _pendingNotify;

        /// <summary>
        /// 日志变更事件（已节流，最快 500ms 触发一次）
        /// </summary>
        public event Action<IReadOnlyList<string>>? LogChanged;

        public RingBufferSink(int capacity = 200, string outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        {
            _capacity = capacity;
            _buffer = new List<string>(capacity);
            _formatter = new MessageTemplateTextFormatter(outputTemplate);
        }

        public void Emit(LogEvent logEvent)
        {
            var writer = new StringWriter();
            _formatter.Format(logEvent, writer);
            var message = writer.ToString().TrimEnd();

            bool shouldNotify;

            lock (_lock)
            {
                _buffer.Add(message);
                if (_buffer.Count > _capacity)
                {
                    _buffer.RemoveAt(0);
                }

                var now = DateTime.UtcNow;
                if ((now - _lastNotify).TotalMilliseconds >= 500)
                {
                    shouldNotify = true;
                    _lastNotify = now;
                    _pendingNotify = false;
                }
                else
                {
                    shouldNotify = false;
                    if (!_pendingNotify)
                    {
                        _pendingNotify = true;
                        ScheduleDelayedNotify();
                    }
                }
            }

            if (shouldNotify)
            {
                NotifyChanged();
            }
        }

        public IReadOnlyList<string> GetAll()
        {
            lock (_lock)
            {
                return new List<string>(_buffer);
            }
        }

        private void ScheduleDelayedNotify()
        {
            Task.Delay(500).ContinueWith(_ =>
            {
                lock (_lock)
                {
                    _pendingNotify = false;
                    _lastNotify = DateTime.UtcNow;
                }
                NotifyChanged();
            });
        }

        private void NotifyChanged()
        {
            try
            {
                LogChanged?.Invoke(GetAll());
            }
            catch
            {
                // 防止 UI 异常传播回日志管线
            }
        }
    }

    public static class RingBufferSinkExtensions
    {
        public static LoggerConfiguration RingBuffer(
            this LoggerSinkConfiguration config,
            RingBufferSink sink,
            LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose)
        {
            return config.Sink(sink, restrictedToMinimumLevel);
        }
    }
}
