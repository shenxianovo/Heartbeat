namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 开机自启动管理接口
    /// </summary>
    public interface IAutoStartService
    {
        bool IsEnabled { get; }
        void Enable(string executablePath);
        void Disable();
    }
}
