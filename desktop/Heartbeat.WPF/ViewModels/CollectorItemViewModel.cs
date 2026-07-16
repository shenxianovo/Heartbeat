using CommunityToolkit.Mvvm.ComponentModel;

namespace Heartbeat.WPF.ViewModels
{
    /// <summary>
    /// 采集器栏条目（ADR-026 §5）：身份 + Active + 零或多个控件。
    /// 可管理性分级：plugin 带 enable 开关；system 只读（不可停用，无开关），
    /// 将来长出采集粒度控件。
    /// </summary>
    public partial class CollectorItemViewModel : ObservableObject
    {
        private readonly Action<string, bool>? _onEnabledChanged;
        private bool _suppressEnabledEvent;

        public string Source { get; }

        /// <summary>system 采集器：恒 Active（Agent 自身在跑）、无开关。</summary>
        public bool IsSystem { get; }

        /// <summary>开关只对 plugin 渲染（IsSystem 时不渲染而非置灰——不可停用是本质）。</summary>
        public bool CanToggle => !IsSystem;

        [ObservableProperty]
        private bool _isActive;

        [ObservableProperty]
        private bool _enabled = true;

        public CollectorItemViewModel(string source, bool isSystem, Action<string, bool>? onEnabledChanged = null)
        {
            Source = source;
            IsSystem = isSystem;
            _onEnabledChanged = onEnabledChanged;
        }

        /// <summary>卡片图标（Segoe Fluent Icons 码位），按 source 约定映射，未知走通用。</summary>
        public string IconGlyph => char.ConvertFromUtf32(Source switch
        {
            "system" => 0xE7F4,   // TVMonitor：系统前台窗口
            "browser" => 0xE774,  // Globe：浏览器
            "vscode" => 0xE943,   // Code
            _ => 0xEA86,          // Puzzle：通用采集器
        });

        /// <summary>卡片副标题。</summary>
        public string Description => Source switch
        {
            "system" => "内置系统采集器，前台窗口与输入，不可停用",
            "browser" => "浏览器扩展，采集标签页活动",
            _ => "外部采集器，经 loopback 汇入",
        };

        /// <summary>从注册表刷新时走此路，不回写配置（区分"用户翻开关"与"读模型同步"）。</summary>
        public void SetEnabledSilently(bool value)
        {
            _suppressEnabledEvent = true;
            Enabled = value;
            _suppressEnabledEvent = false;
        }

        partial void OnEnabledChanged(bool value)
        {
            if (!_suppressEnabledEvent)
                _onEnabledChanged?.Invoke(Source, value);
        }
    }
}
