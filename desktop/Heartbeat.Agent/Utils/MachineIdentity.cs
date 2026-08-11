using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Heartbeat.Agent.Utils;

/// <summary>平台 head 提供的稳定硬件身份 seam。</summary>
public interface IMachineIdentity
{
    string HardwareId { get; }
}

/// <summary>Windows adapter：从注册表读取 MachineGuid。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineIdentity : IMachineIdentity
{
    private readonly Lazy<string> _machineGuid = new(ReadMachineGuid);

    public string HardwareId => _machineGuid.Value;

    private static string ReadMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string ?? string.Empty;
    }
}
