using System.ComponentModel;
using System.Diagnostics;

namespace Fillsign.App.Platform.Signatures;

/// <summary>
/// Keeps the key in the desktop keyring (GNOME Keyring, KWallet) through the Secret Service, using the
/// <c>secret-tool</c> command. Without it there is no keyring to use, and signatures are kept unencrypted.
/// </summary>
internal sealed class LinuxSecretToolVault : ISignatureKeyVault
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    private readonly string[] _attributes;
    private readonly string _label;

    public LinuxSecretToolVault(string service, string account)
    {
        _attributes = ["application", service.ToLowerInvariant(), "purpose", account.ToLowerInvariant().Replace(' ', '-')];
        _label = $"{service} {account.ToLowerInvariant()}";
    }

    public byte[]? FindKey() => Lookup().Key;

    public byte[]? GetOrCreateKey()
    {
        (bool hasStore, byte[]? existing) = Lookup();
        if (!hasStore || existing is not null)
        {
            return existing;
        }

        byte[] key = SignatureKeyVaults.NewKey();
        (int exitCode, string _, string error)? store = Run(["store", $"--label={_label}", .. _attributes], Convert.ToBase64String(key));
        if (store is not { exitCode: 0 })
        {
            throw new SignatureKeyUnavailableException($"The keyring refused to store the key: {store?.error.Trim()}");
        }

        return key;
    }

    /// <summary>Whether secret-tool is there at all, and the key if the keyring has one.</summary>
    private (bool HasStore, byte[]? Key) Lookup()
    {
        (int exitCode, string output, string error)? lookup = Run(["lookup", .. _attributes], input: null);
        if (lookup is null)
        {
            return (false, null);
        }

        if (lookup.Value.exitCode == 0 && lookup.Value.output.Length > 0)
        {
            return (true, Decode(lookup.Value.output));
        }

        // Not found is exit code 1 with nothing on stderr; anything else is the keyring failing.
        if (lookup.Value.error.Length > 0)
        {
            throw new SignatureKeyUnavailableException($"The keyring didn't hand over the key: {lookup.Value.error.Trim()}");
        }

        return (true, null);
    }

    private static byte[] Decode(string text)
    {
        try
        {
            byte[] key = Convert.FromBase64String(text.Trim());
            return key.Length == SignatureKeyVaults.KeyLength
                ? key
                : throw new SignatureKeyUnavailableException("The key in the keyring has the wrong length.");
        }
        catch (FormatException ex)
        {
            throw new SignatureKeyUnavailableException("The key in the keyring isn't readable.", ex);
        }
    }

    /// <summary>Runs secret-tool, or returns null when it isn't installed.</summary>
    private static (int ExitCode, string Output, string Error)? Run(IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(start) ?? throw new SignatureKeyUnavailableException("secret-tool didn't start.");
            if (input is not null)
            {
                process.StandardInput.Write(input);
            }

            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(s_timeout))
            {
                process.Kill();
                throw new SignatureKeyUnavailableException("The keyring didn't answer in time.");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Win32Exception)
        {
            // secret-tool isn't installed, so there's no keyring to use.
            return null;
        }
    }
}
