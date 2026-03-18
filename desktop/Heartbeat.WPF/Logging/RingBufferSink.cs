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
    /// 使用固定数组 + 写指针实现 O(1) 写入，增量推送 + 节流 UI 刷新。
    /// </summary>
    public class RingBufferSink : ILogEventSink
    {
        private readonly int _capacity;
        private readonly Lock _lock = new();
        private readonly string[] _ring;
        private int _head;   // 下一个写入位置
        private int _count;  // 当前有效元素数
        private long _totalWrites;       // 累计写入次数（单调递增）
        private long _lastNotifiedAt;    // 上次通知时的 _totalWrites
        private readonly MessageTemplateTextFormatter _formatter;

        private DateTime _lastNotify = DateTime.MinValue;
        private bool _pendingNotify;

        /// <summary>
        /// 缓冲区容量
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// 增量日志变更事件（已节流，最快 500ms 触发一次）。
        /// 参数为自上次通知以来的新条目（按时间顺序）。
        /// </summary>
        public event Action<IReadOnlyList<string>>? LogChanged;

        public RingBufferSink(int capacity = 200, string outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        {
            _capacity = capacity;
            _ring = new string[capacity];
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
                // O(1) 写入：直接覆盖最老元素
                _ring[_head] = message;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity) _count++;
                _totalWrites++;

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

        /// <summary>
        /// 获取缓冲区中所有条目（按时间顺序），用于初始加载。
        /// </summary>
        public IReadOnlyList<string> GetAll()
        {
            lock (_lock)
            {
                _lastNotifiedAt = _totalWrites;

                var result = new string[_count];
                if (_count < _capacity)
                {
                    Array.Copy(_ring, 0, result, 0, _count);
                }
                else
                {
                    var tailLen = _capacity - _head;
                    Array.Copy(_ring, _head, result, 0, tailLen);
                    Array.Copy(_ring, 0, result, tailLen, _head);
                }
                return result;
            }
        }

        /// <summary>
        /// 从环形缓冲区末尾提取最近 count 条记录（按时间顺序）。
        /// 调用方需持有 _lock。
        /// </summary>
        private string[] ExtractRecent(int count)
        {
            var result = new string[count];
            var start = ((_head - count) % _capacity + _capacity) % _capacity;
            for (var i = 0; i < count; i++)
            {
                result[i] = _ring[(start + i) % _capacity];
            }
            return result;
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
            IReadOnlyList<string> delta;
            lock (_lock)
            {
                var newCount = (int)Math.Min(_totalWrites - _lastNotifiedAt, _count);
                if (newCount <= 0) return;

                delta = ExtractRecent(newCount);
                _lastNotifiedAt = _totalWrites;
            }

            try
            {
                LogChanged?.Invoke(delta);
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
