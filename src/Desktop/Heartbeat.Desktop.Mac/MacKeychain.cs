using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Heartbeat.Desktop.Mac;

/// <summary>API keys stay in the user's Keychain; only a profile-specific account key is stored on disk.</summary>
public sealed class MacKeychain : ICredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly nint SecurityHandle = NativeLibrary.Load(Security);
    private static readonly nint FoundationHandle = NativeLibrary.Load(Foundation);
    private const int NotFound = -25300;

    public string? Read(string account)
    {
        var query = Query(account);
        nint result = 0;
        try
        {
            Set(query, Constant("kSecReturnData"), Marshal.ReadIntPtr(NativeLibrary.GetExport(FoundationHandle, "kCFBooleanTrue")));
            var status = CopyMatching(query, out result);
            if (status == NotFound) return null;
            RequireSuccess(status);
            var length = checked((int)DataLength(result));
            return Marshal.PtrToStringUTF8(DataBytes(result), length);
        }
        finally { if (result != 0) Release(result); Release(query); }
    }

    public void Write(string account, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        var query = Query(account);
        var attributes = Dictionary();
        var data = CreateData(0, bytes, bytes.Length);
        try
        {
            Set(attributes, Constant("kSecValueData"), data);
            var status = Update(query, attributes);
            if (status == NotFound)
            {
                Set(query, Constant("kSecValueData"), data);
                status = Add(query, 0);
            }
            RequireSuccess(status);
        }
        finally { Release(data); Release(attributes); Release(query); CryptographicOperations.ZeroMemory(bytes); }
    }

    private static nint Query(string account)
    {
        var query = Dictionary();
        Set(query, Constant("kSecClass"), Constant("kSecClassGenericPassword"));
        SetString(query, "kSecAttrService", DesktopProfile.CredentialService);
        SetString(query, "kSecAttrAccount", account);
        return query;
    }

    private static nint Dictionary() => CreateDictionary(0, 0,
        NativeLibrary.GetExport(FoundationHandle, "kCFTypeDictionaryKeyCallBacks"),
        NativeLibrary.GetExport(FoundationHandle, "kCFTypeDictionaryValueCallBacks"));
    private static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle, name));
    private static void SetString(nint dictionary, string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var text = CreateString(0, bytes, bytes.Length, 0x08000100, false);
        try { Set(dictionary, Constant(key), text); }
        finally { Release(text); }
    }
    private static void RequireSuccess(int status)
    {
        if (status != 0) throw new IOException($"macOS 未允许访问系统凭据库（{status}）。这不代表 API key 无效，也不一定是钥匙串未解锁。");
    }

    [DllImport(Security, EntryPoint = "SecItemCopyMatching")]
    private static extern int CopyMatching(nint query, out nint result);
    [DllImport(Security, EntryPoint = "SecItemUpdate")]
    private static extern int Update(nint query, nint attributes);
    [DllImport(Security, EntryPoint = "SecItemAdd")]
    private static extern int Add(nint attributes, nint result);
    [DllImport(Foundation, EntryPoint = "CFDictionaryCreateMutable")]
    private static extern nint CreateDictionary(nint allocator, nint capacity, nint keys, nint values);
    [DllImport(Foundation, EntryPoint = "CFDictionarySetValue")]
    private static extern void Set(nint dictionary, nint key, nint value);
    [DllImport(Foundation, EntryPoint = "CFStringCreateWithBytes")]
    private static extern nint CreateString(nint allocator, byte[] bytes, nint length, uint encoding, [MarshalAs(UnmanagedType.I1)] bool external);
    [DllImport(Foundation, EntryPoint = "CFDataCreate")]
    private static extern nint CreateData(nint allocator, byte[] bytes, nint length);
    [DllImport(Foundation, EntryPoint = "CFDataGetLength")]
    private static extern nint DataLength(nint data);
    [DllImport(Foundation, EntryPoint = "CFDataGetBytePtr")]
    private static extern nint DataBytes(nint data);
    [DllImport(Foundation, EntryPoint = "CFRelease")]
    private static extern void Release(nint value);
}
