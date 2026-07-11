using Heartbeat.Agent.Utils;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 输入事件采集服务：钩子生命周期 + 将原始钩子事件翻译为 InputEventBuffer 的语义调用。
    /// 钩子线程由 WindowsLowLevelInputHook 自持（消息泵统一形态），详见 ADR-012。
    /// buffer 为共享单例：本服务只写入，出网侧经 IUploadSource 直接 drain。
    /// </summary>
    public sealed class InputEventCollector(ILowLevelInputHook hook, IInputActivitySignal inputActivity, InputEventBuffer buffer) : IHostedService, IDisposable
    {
        private readonly ILowLevelInputHook _hook = hook;
        private readonly IInputActivitySignal _inputActivity = inputActivity;
        private readonly InputEventBuffer _buffer = buffer;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Log.Information("输入事件采集服务启动");

            _hook.KeyDown += OnKeyDown;
            _hook.KeyUp += OnKeyUp;
            _hook.MouseButton += OnMouseButton;
            _hook.Scroll += OnScroll;

            _hook.StartHook();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("输入事件采集服务停止");
            Unsubscribe();
            _hook.StopHook();
            return Task.CompletedTask;
        }

        // 回调保持最小工作：仅转发给 buffer（buffer 内部为并发安全的轻量操作）
        private void OnKeyDown(int vk) => _buffer.OnKeyDown(vk);
        private void OnKeyUp(int vk) => _buffer.OnKeyUp(vk);
        private void OnMouseButton(short code)
        {
            // 点击（含触摸板点击）标记输入活动，供标题变化门控使用（ADR-016）。
            _inputActivity.MarkClick();
            _buffer.OnMouseButton(code);
        }
        private void OnScroll(int delta) => _buffer.OnScroll(delta);

        private void Unsubscribe()
        {
            _hook.KeyDown -= OnKeyDown;
            _hook.KeyUp -= OnKeyUp;
            _hook.MouseButton -= OnMouseButton;
            _hook.Scroll -= OnScroll;
        }

        public void Dispose()
        {
            Unsubscribe();
            _hook.StopHook();
            GC.SuppressFinalize(this);
        }
    }
}
