using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsCredentials : ICredentialStore
{
    public string? Read(string account)
    {
        if (!CredRead(DesktopProfile.CredentialService + "/" + account, 1, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == 1168) return null;
            throw new IOException("Windows 未允许读取系统凭据库。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    public void Write(string account, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = 1, Target = DesktopProfile.CredentialService + "/" + account,
                Blob = blob, BlobSize = (uint)bytes.Length, Persist = 2, UserName = account,
            };
            if (!CredWrite(ref credential, 0)) throw new IOException("Windows 未允许保存到系统凭据库。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string Target;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? Alias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
}
