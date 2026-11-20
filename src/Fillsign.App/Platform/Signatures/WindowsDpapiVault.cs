using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fillsign.App.Platform.Signatures;

/// <summary>
/// Keeps the key in a file protected with Windows DPAPI for the current user: only this Windows account, signed in,
/// can unprotect it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsDpapiVault : ISignatureKeyVault
{
    private const int UiForbidden = 0x1;

    private readonly string _keyFile;

    public WindowsDpapiVault(string keyFile)
    {
        _keyFile = keyFile;
    }

    public byte[]? FindKey()
    {
        try
        {
            if (!File.Exists(_keyFile))
            {
                return null;
            }

            byte[] key = Unprotect(File.ReadAllBytes(_keyFile));
            return key.Length == SignatureKeyVaults.KeyLength
                ? key
                : throw new SignatureKeyUnavailableException("The saved signatures key has the wrong length.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new SignatureKeyUnavailableException("The saved signatures key couldn't be read.", ex);
        }
    }

    public byte[]? GetOrCreateKey()
    {
        if (FindKey() is { } existing)
        {
            return existing;
        }

        try
        {
            byte[] created = SignatureKeyVaults.NewKey();
            Directory.CreateDirectory(Path.GetDirectoryName(_keyFile)!);
            File.WriteAllBytes(_keyFile, Protect(created));
            return created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new SignatureKeyUnavailableException("The saved signatures key couldn't be stored.", ex);
        }
    }

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true);

    private static byte[] Unprotect(byte[] protectedBytes) => Transform(protectedBytes, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        fixed (byte* bytes = input)
        {
            var inBlob = new DataBlob { Length = input.Length, Data = (IntPtr)bytes };
            var outBlob = default(DataBlob);
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                return new ReadOnlySpan<byte>((void*)outBlob.Data, outBlob.Length).ToArray();
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
