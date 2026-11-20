using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fillsign.Core.Model;

namespace Fillsign.App.Platform.Signatures;

/// <summary>How a saved signature was made, shown next to it ("Drawn", "Typed").</summary>
public enum SignatureMethod
{
    Drawn,
    Typed,
    Photo,
}

/// <summary>A signature or initials kept on this computer for next time (FR-4.5).</summary>
public sealed record SavedSignature(SignatureImage Image, SignatureKind Kind, SignatureMethod Method, DateTimeOffset LastUsed)
{
    public Guid Id => Image.Id;
}

/// <summary>Whether saved signatures could be read.</summary>
public enum SavedSignaturesState
{
    /// <summary>Read (or none yet); keeping new ones works.</summary>
    Ready,

    /// <summary>
    /// The file exists but its key can't be had right now (for example Keychain access was denied). Nothing is shown
    /// and nothing is written, so the saved signatures survive until the key is available again.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Saved signatures and initials (FR-4.5–4.7, FS-404): up to <see cref="MaxPerKind"/> of each, in one file in the app
/// data folder. The file is encrypted with AES-256-GCM using a key from the system's secret store when there is one
/// (<see cref="ISignatureKeyVault"/>), and is always readable only by this user. Keeping is opt-in; the dialog never
/// saves anything unless asked.
/// </summary>
public sealed class SavedSignatureStore
{
    public const int MaxPerKind = 6;
    public const string FileName = "signatures.dat";

    // File layout: magic and format version, a mode byte, then the JSON (plain) or nonce + tag + ciphertext.
    private static readonly byte[] s_magic = "FSSIG\u0001"u8.ToArray();
    private const byte PlainMode = 0;
    private const byte EncryptedMode = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    // Matches the session format's limit for one image, so anything saved can also be placed and saved in a file.
    private const int MaxPngBytes = 8 * 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly ISignatureKeyVault _vault;
    private ImmutableList<SavedSignature> _items = [];
    private bool _isLoaded;

    // The key is fetched once per run: every Keychain or keyring call can show the person a prompt.
    private byte[]? _key;
    private bool _hasKey;

    public SavedSignatureStore(string folder, ISignatureKeyVault vault)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(vault);
        _path = Path.Combine(folder, FileName);
        _vault = vault;
    }

    /// <summary>The store in this user's app data folder, encrypted with the system's secret store.</summary>
    public static SavedSignatureStore ForCurrentUser() =>
        new(AppSettings.Folder, SignatureKeyVaults.ForCurrentUser(AppSettings.Folder));

    public SavedSignaturesState State { get; private set; } = SavedSignaturesState.Ready;

    /// <summary>
    /// Reads the file once; later calls return what's in memory. May touch the Keychain or keyring, which can ask
    /// the person, so call it off the UI thread.
    /// </summary>
    public IReadOnlyList<SavedSignature> Load()
    {
        lock (_lock)
        {
            if (!_isLoaded)
            {
                _items = ReadFile();
                _isLoaded = true;
            }

            return _items;
        }
    }

    /// <summary>Saved ones of one kind, most recently used first (FR-4.6).</summary>
    public IReadOnlyList<SavedSignature> Of(SignatureKind kind) =>
        [.. Load().Where(saved => saved.Kind == kind).OrderByDescending(saved => saved.LastUsed)];

    /// <summary>
    /// Keeps a new signature. When there are already <see cref="MaxPerKind"/> of its kind, the one used longest ago
    /// makes room.
    /// </summary>
    /// <exception cref="InvalidOperationException">Saved signatures are <see cref="SavedSignaturesState.Unavailable"/>.</exception>
    public void Keep(SavedSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        lock (_lock)
        {
            Load();
            ThrowIfUnavailable();
            ImmutableList<SavedSignature> sameKind = [.. _items.Where(saved => saved.Kind == signature.Kind).OrderBy(saved => saved.LastUsed)];
            ImmutableList<SavedSignature> items = _items;
            for (int i = 0; i <= sameKind.Count - MaxPerKind; i++)
            {
                items = items.Remove(sameKind[i]);
            }

            Write(items.Add(signature));
        }
    }

    /// <summary>Records that a saved signature was just placed, so it's preselected next time (FR-4.6).</summary>
    public void MarkUsed(Guid id, DateTimeOffset when)
    {
        lock (_lock)
        {
            Load();
            if (State == SavedSignaturesState.Unavailable || _items.Find(saved => saved.Id == id) is not { } found)
            {
                return;
            }

            Write(_items.Replace(found, found with { LastUsed = when }));
        }
    }

    /// <summary>Removes one from this computer (FR-4.7). Documents that already contain it keep it.</summary>
    public void Remove(Guid id)
    {
        lock (_lock)
        {
            Load();
            ThrowIfUnavailable();
            if (_items.Find(saved => saved.Id == id) is { } found)
            {
                Write(_items.Remove(found));
            }
        }
    }

    /// <summary>
    /// For saved signatures that stay locked because their key never comes back: sets the file aside (kept, in case it
    /// does) and starts again with none, so new ones can be kept. Only when the person chooses it in Settings.
    /// </summary>
    public void StartAgain()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".unreadable", overwrite: true);
            }

            _items = [];
            _isLoaded = true;
            _key = null;
            _hasKey = false;
            State = SavedSignaturesState.Ready;
        }
    }

    private void ThrowIfUnavailable()
    {
        if (State == SavedSignaturesState.Unavailable)
        {
            throw new InvalidOperationException("Saved signatures can't be read right now, so they can't be changed.");
        }
    }

    private ImmutableList<SavedSignature> ReadFile()
    {
        byte[] file;
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            file = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Saved signatures could not be read. {ex.Message}");
            State = SavedSignaturesState.Unavailable;
            return [];
        }

        if (file.Length < s_magic.Length + 1 || !file.AsSpan(0, s_magic.Length).SequenceEqual(s_magic))
        {
            return SetAsideUnreadable("it isn't a saved signatures file");
        }

        byte mode = file[s_magic.Length];
        ReadOnlySpan<byte> body = file.AsSpan(s_magic.Length + 1);
        byte[] json;
        if (mode == PlainMode)
        {
            json = body.ToArray();
        }
        else if (mode == EncryptedMode)
        {
            byte[]? key;
            try
            {
                key = ExistingKey();
            }
            catch (SignatureKeyUnavailableException ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Saved signatures are locked: {ex.Message}");
                State = SavedSignaturesState.Unavailable;
                return [];
            }

            // No key could mean it's gone for good, or that the keyring is only out of reach (a Linux keyring left
            // locked looks the same as an empty one). Making a new key here would lose the file for certain, so it's
            // kept and the store stays locked until the key is back or the person chooses Start again in Settings.
            if (key is null)
            {
                System.Diagnostics.Trace.TraceWarning("Saved signatures are locked: the secret store has no key for them.");
                State = SavedSignaturesState.Unavailable;
                return [];
            }

            if (body.Length < NonceLength + TagLength)
            {
                return SetAsideUnreadable("it's too short");
            }

            json = new byte[body.Length - NonceLength - TagLength];
            try
            {
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(body[..NonceLength], body[(NonceLength + TagLength)..], body.Slice(NonceLength, TagLength), json, Header(EncryptedMode));
            }
            catch (AuthenticationTagMismatchException)
            {
                // A different key, or a damaged file: nothing can ever read it, so start again.
                return SetAsideUnreadable("it was made with a different key or is damaged");
            }
        }
        else
        {
            return SetAsideUnreadable($"mode {mode} is unknown");
        }

        try
        {
            SavedFile? saved = JsonSerializer.Deserialize(json, SavedSignatureJson.Default.SavedFile);
            return [.. (saved?.Items ?? []).Select(FromRecord).OfType<SavedSignature>()];
        }
        catch (JsonException ex)
        {
            return SetAsideUnreadable(ex.Message);
        }
    }

    /// <summary>Moves a file nothing can read out of the way (kept for inspection) and starts with none.</summary>
    private ImmutableList<SavedSignature> SetAsideUnreadable(string reason)
    {
        System.Diagnostics.Trace.TraceWarning($"Saved signatures could not be read ({reason}); starting with none.");
        try
        {
            File.Move(_path, _path + ".unreadable", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"The unreadable file could not be moved aside. {ex.Message}");
            State = SavedSignaturesState.Unavailable;
        }

        return [];
    }

    private void Write(ImmutableList<SavedSignature> items)
    {
        var record = new SavedFile([.. items.Select(ToRecord)]);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, SavedSignatureJson.Default.SavedFile);

        byte[]? key = Key();
        byte[] file;
        if (key is null)
        {
            file = [.. Header(PlainMode), .. json];
        }
        else
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var tag = new byte[TagLength];
            var cipher = new byte[json.Length];
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Encrypt(nonce, json, cipher, tag, Header(EncryptedMode));
            }

            file = [.. Header(EncryptedMode), .. nonce, .. tag, .. cipher];
        }

        WriteOwnerOnly(file);
        _items = items;
    }

    /// <summary>Writes to a temporary file and swaps it in, so a crash never leaves half a file.</summary>
    private void WriteOwnerOnly(byte[] contents)
    {
        string folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        string temporary = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>The key an encrypted file was made with, never a new one.</summary>
    private byte[]? ExistingKey()
    {
        if (!_hasKey)
        {
            _key = _vault.FindKey();
            _hasKey = _key is not null;
        }

        return _key;
    }

    private byte[]? Key()
    {
        if (!_hasKey)
        {
            _key = _vault.GetOrCreateKey();
            _hasKey = true;
        }

        return _key;
    }

    private static byte[] Header(byte mode) => [.. s_magic, mode];

    private static SavedRecord ToRecord(SavedSignature saved) => new(
        saved.Id.ToString("N"),
        saved.Kind == SignatureKind.Initials ? "initials" : "signature",
        saved.Method.ToString().ToLowerInvariant(),
        Convert.ToBase64String(saved.Image.Png.AsSpan()),
        saved.Image.PixelWidth,
        saved.Image.PixelHeight,
        saved.LastUsed.ToUniversalTime());

    /// <summary>One saved record, or null (skipped) when it's malformed.</summary>
    private static SavedSignature? FromRecord(SavedRecord record)
    {
        SignatureKind? kind = record.Kind switch
        {
            "signature" => SignatureKind.Signature,
            "initials" => SignatureKind.Initials,
            _ => null,
        };
        bool isMethod = Enum.TryParse(record.Method, ignoreCase: true, out SignatureMethod method) && Enum.IsDefined(method);
        if (kind is null || !isMethod || !Guid.TryParseExact(record.Id, "N", out Guid id) || record.PixelWidth <= 0 || record.PixelHeight <= 0)
        {
            return null;
        }

        byte[] png;
        try
        {
            png = Convert.FromBase64String(record.Png);
        }
        catch (FormatException)
        {
            return null;
        }

        if (png.Length is 0 or > MaxPngBytes)
        {
            return null;
        }

        var image = new SignatureImage(id, [.. png], record.PixelWidth, record.PixelHeight);
        return new SavedSignature(image, kind.Value, method, record.LastUsed);
    }
}

internal sealed record SavedFile(IReadOnlyList<SavedRecord> Items);

internal sealed record SavedRecord(string Id, string Kind, string Method, string Png, int PixelWidth, int PixelHeight, DateTimeOffset LastUsed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedFile))]
internal sealed partial class SavedSignatureJson : JsonSerializerContext;
