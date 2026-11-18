using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fillsign.App.Platform.Signatures;

/// <summary>
/// Keeps the key as a generic password in the login Keychain. Only Fillsign can read it without asking; after an
/// update macOS may ask once whether the new version may use it.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacKeychainVault : ISignatureKeyVault
{
    private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;

    private readonly string _service;
    private readonly string _account;

    public MacKeychainVault(string service, string account)
    {
        _service = service;
        _account = account;
    }

    public byte[]? GetOrCreateKey()
    {
        byte[]? existing = Find();
        if (existing is not null)
        {
            return existing;
        }

        byte[] key = SignatureKeyVaults.NewKey();
        int status = Add(key);
        if (status == DuplicateItem)
        {
            // Another window created it a moment ago.
            return Find() ?? throw new SignatureKeyUnavailableException("The Keychain reported a key that then couldn't be read.");
        }

        if (status != Success)
        {
            throw new SignatureKeyUnavailableException($"The Keychain refused to store the key (status {status}).");
        }

        return key;
    }

    /// <summary>Removes the key. Only tests use this, to clean up after themselves.</summary>
    internal void Delete()
    {
        using var query = new CFDictionary(
            (Constants.SecClass, Constants.SecClassGenericPassword),
            (Constants.SecAttrService, CFString(_service)),
            (Constants.SecAttrAccount, CFString(_account)));
        _ = SecItemDelete(query.Handle);
    }

    private byte[]? Find()
    {
        using var query = new CFDictionary(
            (Constants.SecClass, Constants.SecClassGenericPassword),
            (Constants.SecAttrService, CFString(_service)),
            (Constants.SecAttrAccount, CFString(_account)),
            (Constants.SecReturnData, Constants.BooleanTrue),
            (Constants.SecMatchLimit, Constants.SecMatchLimitOne));
        int status = SecItemCopyMatching(query.Handle, out IntPtr data);
        if (status == ItemNotFound)
        {
            return null;
        }

        if (status != Success)
        {
            throw new SignatureKeyUnavailableException($"The Keychain didn't hand over the key (status {status}).");
        }

        try
        {
            int length = checked((int)CFDataGetLength(data));
            var key = new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(data), length).ToArray();
            if (key.Length != SignatureKeyVaults.KeyLength)
            {
                throw new SignatureKeyUnavailableException("The key in the Keychain has the wrong length.");
            }

            return key;
        }
        finally
        {
            CFRelease(data);
        }
    }

    private int Add(byte[] key)
    {
        IntPtr data;
        fixed (byte* bytes = key)
        {
            data = CFDataCreate(IntPtr.Zero, bytes, key.Length);
        }

        using var attributes = new CFDictionary(
            (Constants.SecClass, Constants.SecClassGenericPassword),
            (Constants.SecAttrService, CFString(_service)),
            (Constants.SecAttrAccount, CFString(_account)),
            (Constants.SecAttrLabel, CFString($"{_service} {_account.ToLowerInvariant()}")),
            (Constants.SecValueData, data));
        return SecItemAdd(attributes.Handle, IntPtr.Zero);
    }

    /// <summary>A new CFString. The dictionary that receives it releases it.</summary>
    private static IntPtr CFString(string value)
    {
        fixed (char* chars = value)
        {
            return CFStringCreateWithCharacters(IntPtr.Zero, chars, value.Length);
        }
    }

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemDelete(IntPtr query);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, char* characters, nint length);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial void CFRelease(IntPtr value);

    /// <summary>
    /// A CFDictionary built from key/value pairs. Values created for it (strings, data) are released with it; the
    /// framework's own constants are never released.
    /// </summary>
    private sealed class CFDictionary : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public CFDictionary(params (IntPtr Key, IntPtr Value)[] pairs)
        {
            IntPtr[] keys = [.. pairs.Select(pair => pair.Key)];
            IntPtr[] values = [.. pairs.Select(pair => pair.Value)];
            _owned.AddRange(values.Where(value => !Constants.IsConstant(value)));
            fixed (IntPtr* keyPointer = keys)
            fixed (IntPtr* valuePointer = values)
            {
                Handle = CFDictionaryCreate(IntPtr.Zero, keyPointer, valuePointer, pairs.Length, Constants.TypeDictionaryKeyCallBacks, Constants.TypeDictionaryValueCallBacks);
            }
        }

        public IntPtr Handle { get; }

        public void Dispose()
        {
            CFRelease(Handle);
            foreach (IntPtr value in _owned)
            {
                CFRelease(value);
            }
        }
    }

    /// <summary>Exported framework constants, read once.</summary>
    private static class Constants
    {
        private static readonly IntPtr s_security = NativeLibrary.Load(SecurityLibrary);
        private static readonly IntPtr s_coreFoundation = NativeLibrary.Load(CoreFoundationLibrary);

        public static readonly IntPtr SecClass = Read(s_security, "kSecClass");
        public static readonly IntPtr SecClassGenericPassword = Read(s_security, "kSecClassGenericPassword");
        public static readonly IntPtr SecAttrService = Read(s_security, "kSecAttrService");
        public static readonly IntPtr SecAttrAccount = Read(s_security, "kSecAttrAccount");
        public static readonly IntPtr SecAttrLabel = Read(s_security, "kSecAttrLabel");
        public static readonly IntPtr SecValueData = Read(s_security, "kSecValueData");
        public static readonly IntPtr SecReturnData = Read(s_security, "kSecReturnData");
        public static readonly IntPtr SecMatchLimit = Read(s_security, "kSecMatchLimit");
        public static readonly IntPtr SecMatchLimitOne = Read(s_security, "kSecMatchLimitOne");
        public static readonly IntPtr BooleanTrue = Read(s_coreFoundation, "kCFBooleanTrue");

        // These two are structs, so their address is what CFDictionaryCreate wants.
        public static readonly IntPtr TypeDictionaryKeyCallBacks = NativeLibrary.GetExport(s_coreFoundation, "kCFTypeDictionaryKeyCallBacks");
        public static readonly IntPtr TypeDictionaryValueCallBacks = NativeLibrary.GetExport(s_coreFoundation, "kCFTypeDictionaryValueCallBacks");

        public static bool IsConstant(IntPtr value) =>
            value == SecClassGenericPassword || value == SecMatchLimitOne || value == BooleanTrue;

        private static IntPtr Read(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }
}
