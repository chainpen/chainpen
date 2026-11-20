using System.Security.Cryptography;

namespace Fillsign.App.Platform.Signatures;

/// <summary>
/// Keeps the key that encrypts saved signatures in the operating system's own secret store (NFR-1, FS-404), so the
/// signatures file is unreadable when copied off the computer or out of a backup.
/// </summary>
public interface ISignatureKeyVault
{
    /// <summary>
    /// Whether this computer has a secret store to keep the key in. Without one (Linux with no keyring), saved
    /// signatures are kept unencrypted, readable only by this user, and the person is told so before keeping one.
    /// </summary>
    bool HasSecretStore { get; }

    /// <summary>
    /// The key if one has been made, without ever making one: reading an encrypted file must not replace a key that's
    /// only out of reach for now. Null when there's no key, or no secret store.
    /// </summary>
    /// <exception cref="SignatureKeyUnavailableException">The secret store exists but refused or failed.</exception>
    byte[]? FindKey();

    /// <summary>
    /// The 32-byte key, created on first use. Null when this computer has no secret store (for example Linux without
    /// a keyring); saved signatures are then kept unencrypted, readable only by this user.
    /// </summary>
    /// <exception cref="SignatureKeyUnavailableException">
    /// The secret store exists but refused or failed (for example the person denied Keychain access). Saved signatures
    /// must then be left untouched, because the key may come back.
    /// </exception>
    byte[]? GetOrCreateKey();
}

/// <summary>The secret store exists but couldn't hand over the key this time.</summary>
public sealed class SignatureKeyUnavailableException : Exception
{
    public SignatureKeyUnavailableException()
    {
    }

    public SignatureKeyUnavailableException(string message)
        : base(message)
    {
    }

    public SignatureKeyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Picks the secret store for this operating system.</summary>
public static class SignatureKeyVaults
{
    public const int KeyLength = 32;

    // Names the key is filed under in the Keychain or keyring; people see these in Keychain Access or Seahorse.
    public const string ServiceName = "Fillsign";
    public const string AccountName = "Saved signatures key";

    public static ISignatureKeyVault ForCurrentUser(string folder)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainVault(ServiceName, AccountName);
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiVault(Path.Combine(folder, "signatures.key"));
        }

        return new LinuxSecretToolVault(ServiceName, AccountName);
    }

    internal static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyLength);
}

/// <summary>No secret store: signatures are kept unencrypted. Used in tests and as the fallback.</summary>
public sealed class NoKeyVault : ISignatureKeyVault
{
    public bool HasSecretStore => false;

    public byte[]? FindKey() => null;

    public byte[]? GetOrCreateKey() => null;
}
