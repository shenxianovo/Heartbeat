using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collection.Hub.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Desktop.Windows.Services
{
    /// <summary>
    /// 输入事件采集服务：钩子生命周期 + 将原始钩子事件翻译为 InputEventBuffer 的语义调用。
    /// 钩子线程由 WindowsLowLevelInputHook 自持（消息泵统一形态），详见 ADR-012。
    /// buffer 为共享单例：本服务只写入，出网侧经 IUploadSource 直接 drain。
    /// </summary>
    public sealed class InputEventCollector(
        ILowLevelInputHook hook,
        IInputActivitySignal inputActivity,
        IInteractionSignalPolicy interactionSettings,
        IInputEventRecordingPolicy recordingSettings,
        InputEventBuffer buffer) : IHostedService, IDisposable
    {
        private readonly ILowLevelInputHook _hook = hook;
        private readonly IInputActivitySignal _inputActivity = inputActivity;
        private readonly IInteractionSignalPolicy _interactionSettings = interactionSettings;
        private readonly IInputEventRecordingPolicy _recordingSettings = recordingSettings;
        private readonly InputEventBuffer _buffer = buffer;
        private bool _started;
        private bool _hookRunning;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Log.Information("输入事件采集服务启动");

            _hook.KeyDown += OnKeyDown;
            _hook.KeyUp += OnKeyUp;
            _hook.MouseButton += OnMouseButton;
            _hook.Scroll += OnScroll;
            _interactionSettings.Changed += OnInteractionChanged;
            _recordingSettings.Changed += OnRecordingChanged;

            _started = true;
            ReconcileHook();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("输入事件采集服务停止");
            _started = false;
            Unsubscribe();
            StopHook();
            return Task.CompletedTask;
        }

        // 回调保持最小工作：仅转发给 buffer（buffer 内部为并发安全的轻量操作）
        private void OnKeyDown(WindowsNativeKeyObservation observation)
        {
            if (!_recordingSettings.Enabled ||
                !WindowsKeyPositionMapper.TryMap(observation, out var position))
                return;

            _buffer.OnKeyDown(position);
        }

        private void OnKeyUp(WindowsNativeKeyObservation observation)
        {
            // 即使录制刚被关闭也要清 held state，避免再次启用后把下一次真实按下误判为 repeat。
            if (WindowsKeyPositionMapper.TryMap(observation, out var position))
                _buffer.OnKeyUp(position);
        }
        private void OnMouseButton(short code)
        {
            // 点击（含触摸板点击）标记输入活动，供标题变化门控使用（ADR-016）。
            if (_interactionSettings.Enabled)
                _inputActivity.MarkClick();
            if (_recordingSettings.Enabled)
                _buffer.OnMouseButton(code);
        }
        private void OnScroll(int delta)
        {
            if (_recordingSettings.Enabled)
                _buffer.OnScroll(delta);
        }

        private void OnRecordingChanged(bool enabled)
        {
            if (!enabled)
                _buffer.ResetTransientState();
            ReconcileHook();
        }

        private void OnInteractionChanged(bool enabled) => ReconcileHook();

        private void ReconcileHook()
        {
            var shouldRun = _started && (_interactionSettings.Enabled || _recordingSettings.Enabled);
            if (shouldRun && !_hookRunning)
            {
                _hook.StartHook();
                _hookRunning = true;
            }
            else if (!shouldRun)
            {
                StopHook();
            }
        }

        private void StopHook()
        {
            if (!_hookRunning) return;
            _hook.StopHook();
            _hookRunning = false;
        }

        private void Unsubscribe()
        {
            _hook.KeyDown -= OnKeyDown;
            _hook.KeyUp -= OnKeyUp;
            _hook.MouseButton -= OnMouseButton;
            _hook.Scroll -= OnScroll;
            _interactionSettings.Changed -= OnInteractionChanged;
            _recordingSettings.Changed -= OnRecordingChanged;
        }

        public void Dispose()
        {
            Unsubscribe();
            StopHook();
            GC.SuppressFinalize(this);
        }
    }
}
