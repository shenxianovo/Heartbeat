using Microsoft.Win32;

namespace Heartbeat.Agent.Utils
{
    public static class MachineIdentity
    {
        private static readonly Lazy<string> _machineGuid = new(ReadMachineGuid);

        public static string MachineGuid => _machineGuid.Value;

        private static string ReadMachineGuid()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? string.Empty;
        }
    }
}
