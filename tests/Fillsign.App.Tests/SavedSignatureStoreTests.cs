using System.Collections.Immutable;
using System.Text;
using Fillsign.App.Platform.Signatures;
using Fillsign.App.ViewModels;
using Fillsign.Core.Model;

namespace Fillsign.App.Tests;

/// <summary>FS-404: saved signatures and initials on this computer, encrypted when there's a secret store.</summary>
public sealed class SavedSignatureStoreTests : IDisposable
{
    private static readonly DateTimeOffset s_monday = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fillsign-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Nothing_is_saved_until_something_is_kept()
    {
        var store = new SavedSignatureStore(_folder, new NoKeyVault());
        Assert.Empty(store.Load());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Kept_signatures_come_back_in_a_new_session_with_their_details()
    {
        var key = new FixedKeyVault();
        SavedSignature kept = Saved(SignatureKind.Initials, SignatureMethod.Typed, s_monday);
        new SavedSignatureStore(_folder, key).Keep(kept);

        SavedSignature loaded = Assert.Single(new SavedSignatureStore(_folder, key).Load());
        Assert.Equal(kept.Id, loaded.Id);
        Assert.Equal(SignatureKind.Initials, loaded.Kind);
        Assert.Equal(SignatureMethod.Typed, loaded.Method);
        Assert.Equal(s_monday, loaded.LastUsed);
        Assert.Equal(kept.Image.Png, loaded.Image.Png, ImmutableArrayComparer);
        Assert.Equal((kept.Image.PixelWidth, kept.Image.PixelHeight), (loaded.Image.PixelWidth, loaded.Image.PixelHeight));
    }

    [Fact]
    public void With_a_secret_store_the_file_is_encrypted()
    {
        SavedSignature kept = Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday);
        new SavedSignatureStore(_folder, new FixedKeyVault()).Keep(kept);

        byte[] file = File.ReadAllBytes(FilePath);
        string asText = Encoding.Latin1.GetString(file);
        Assert.StartsWith("FSSIG\u0001\u0001", asText, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", asText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(kept.Image.Png.AsSpan())[..12], asText, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_secret_store_the_file_is_plain_but_only_this_user_can_read_it()
    {
        new SavedSignatureStore(_folder, new NoKeyVault()).Keep(Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday));

        Assert.StartsWith("FSSIG\u0001\u0000", Encoding.Latin1.GetString(File.ReadAllBytes(FilePath)), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
        }
    }

    [Fact]
    public void A_plain_file_is_encrypted_once_a_secret_store_appears()
    {
        new SavedSignatureStore(_folder, new NoKeyVault()).Keep(Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday));
        var key = new FixedKeyVault();
        var store = new SavedSignatureStore(_folder, key);
        store.Keep(Saved(SignatureKind.Signature, SignatureMethod.Typed, s_monday.AddDays(1)));

        Assert.Equal(2, new SavedSignatureStore(_folder, key).Load().Count);
        Assert.StartsWith("FSSIG\u0001\u0001", Encoding.Latin1.GetString(File.ReadAllBytes(FilePath)), StringComparison.Ordinal);
    }

    [Fact]
    public void Six_of_each_are_kept_and_the_one_used_longest_ago_makes_room()
    {
        var store = new SavedSignatureStore(_folder, new FixedKeyVault());
        var signatures = Enumerable.Range(0, SavedSignatureStore.MaxPerKind)
            .Select(day => Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday.AddDays(day)))
            .ToList();
        signatures.ForEach(store.Keep);
        SavedSignature initials = Saved(SignatureKind.Initials, SignatureMethod.Drawn, s_monday.AddDays(-30));
        store.Keep(initials);

        // Using the oldest makes the second-oldest the one to go.
        store.MarkUsed(signatures[0].Id, s_monday.AddDays(10));
        store.Keep(Saved(SignatureKind.Signature, SignatureMethod.Typed, s_monday.AddDays(11)));

        IReadOnlyList<SavedSignature> kept = store.Of(SignatureKind.Signature);
        Assert.Equal(SavedSignatureStore.MaxPerKind, kept.Count);
        Assert.DoesNotContain(kept, saved => saved.Id == signatures[1].Id);
        Assert.Contains(kept, saved => saved.Id == signatures[0].Id);
        Assert.Equal(initials.Id, Assert.Single(store.Of(SignatureKind.Initials)).Id);
    }

    [Fact]
    public void The_most_recently_used_comes_first()
    {
        var store = new SavedSignatureStore(_folder, new NoKeyVault());
        SavedSignature older = Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday);
        SavedSignature newer = Saved(SignatureKind.Signature, SignatureMethod.Typed, s_monday.AddDays(1));
        store.Keep(older);
        store.Keep(newer);
        Assert.Equal(newer.Id, store.Of(SignatureKind.Signature)[0].Id);

        store.MarkUsed(older.Id, s_monday.AddDays(2));
        Assert.Equal(older.Id, new SavedSignatureStore(_folder, new NoKeyVault()).Of(SignatureKind.Signature)[0].Id);
    }

    [Fact]
    public void Removing_one_deletes_it_from_the_file()
    {
        var store = new SavedSignatureStore(_folder, new FixedKeyVault());
        SavedSignature first = Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday);
        SavedSignature second = Saved(SignatureKind.Signature, SignatureMethod.Photo, s_monday);
        store.Keep(first);
        store.Keep(second);
        store.Remove(first.Id);

        Assert.Equal(second.Id, Assert.Single(new SavedSignatureStore(_folder, new FixedKeyVault()).Load()).Id);
    }

    [Fact]
    public void A_locked_secret_store_leaves_the_file_alone()
    {
        new SavedSignatureStore(_folder, new FixedKeyVault()).Keep(Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday));
        byte[] before = File.ReadAllBytes(FilePath);

        var locked = new SavedSignatureStore(_folder, new LockedVault());
        Assert.Empty(locked.Load());
        Assert.Equal(SavedSignaturesState.Unavailable, locked.State);
        Assert.Throws<InvalidOperationException>(() => locked.Keep(Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday)));
        Assert.Equal(before, File.ReadAllBytes(FilePath));

        // Once the key is back, nothing was lost.
        Assert.Single(new SavedSignatureStore(_folder, new FixedKeyVault()).Load());
    }

    [Fact]
    public void An_encrypted_file_whose_key_is_missing_is_kept_and_no_new_key_is_made()
    {
        new SavedSignatureStore(_folder, new FixedKeyVault()).Keep(Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday));
        byte[] before = File.ReadAllBytes(FilePath);

        // The secret store says it has no key (or a Linux keyring stayed locked): never make a new one over the file.
        var missing = new MissingKeyVault();
        var store = new SavedSignatureStore(_folder, missing);
        Assert.Empty(store.Load());
        Assert.Equal(SavedSignaturesState.Unavailable, store.State);
        Assert.False(missing.HasCreatedKey);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.False(File.Exists(FilePath + ".unreadable"));

        // A computer with no secret store at all can't read it either, and leaves it alone too.
        Assert.Equal(SavedSignaturesState.Unavailable, LoadedState(new NoKeyVault()));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Single(new SavedSignatureStore(_folder, new FixedKeyVault()).Load());

        // If the key never comes back, the person can choose to start again: the old file is kept to one side.
        store.StartAgain();
        Assert.Equal(SavedSignaturesState.Ready, store.State);
        Assert.Equal(before, File.ReadAllBytes(FilePath + ".unreadable"));
        store.Keep(Saved(SignatureKind.Initials, SignatureMethod.Typed, s_monday));
        Assert.Single(new SavedSignatureStore(_folder, missing).Load());
    }

    [Fact]
    public async Task Settings_offers_a_way_out_when_saved_signatures_stay_locked()
    {
        new SavedSignatureStore(_folder, new FixedKeyVault()).Keep(Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday));
        byte[] before = File.ReadAllBytes(FilePath);
        var store = new SavedSignatureStore(_folder, new MissingKeyVault());
        bool willConfirm = false;
        string? asked = null;
        var settings = new SettingsViewModel(store, (title, _, _) =>
        {
            asked = title;
            return Task.FromResult(willConfirm);
        });

        // Locked: it says so, and doesn't claim there are none.
        settings.LoadSignatures();
        Assert.True(settings.AreSignaturesLocked);
        Assert.False(settings.ShowsNoneSaved);

        // Starting again asks first; saying no changes nothing.
        await settings.StartSignaturesAgainCommand.ExecuteAsync(null);
        Assert.Equal("Start again without your saved signatures?", asked);
        Assert.True(settings.AreSignaturesLocked);
        Assert.Equal(before, File.ReadAllBytes(FilePath));

        // Saying yes sets the old file aside and unlocks, with nothing saved yet.
        willConfirm = true;
        await settings.StartSignaturesAgainCommand.ExecuteAsync(null);
        Assert.False(settings.AreSignaturesLocked);
        Assert.True(settings.ShowsNoneSaved);
        Assert.Equal(before, File.ReadAllBytes(FilePath + ".unreadable"));
        Assert.Equal("You can keep new signatures and initials again.", settings.Message);
    }

    [Fact]
    public void A_file_no_key_can_open_is_set_aside_and_saving_starts_again()
    {
        new SavedSignatureStore(_folder, new FixedKeyVault(1)).Keep(Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday));

        var store = new SavedSignatureStore(_folder, new FixedKeyVault(2));
        Assert.Empty(store.Load());
        Assert.Equal(SavedSignaturesState.Ready, store.State);
        Assert.True(File.Exists(FilePath + ".unreadable"));
        store.Keep(Saved(SignatureKind.Signature, SignatureMethod.Typed, s_monday));
        Assert.Single(new SavedSignatureStore(_folder, new FixedKeyVault(2)).Load());
    }

    [Fact]
    public void Damaged_files_and_records_are_ignored()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "not a signatures file");
        Assert.Empty(new SavedSignatureStore(_folder, new NoKeyVault()).Load());

        // One good record and one with an unknown kind: only the good one loads.
        SavedSignature good = Saved(SignatureKind.Signature, SignatureMethod.Drawn, s_monday);
        new SavedSignatureStore(_folder, new NoKeyVault()).Keep(good);
        string json = Encoding.UTF8.GetString(File.ReadAllBytes(FilePath)[7..]);
        string bad = json.Replace("\"items\":[", "\"items\":[{\"id\":\"00000000000000000000000000000001\",\"kind\":\"stamp\",\"method\":\"drawn\",\"png\":\"AA==\",\"pixelWidth\":1,\"pixelHeight\":1,\"lastUsed\":\"2026-11-16T09:00:00+00:00\"},", StringComparison.Ordinal);
        File.WriteAllBytes(FilePath, [.. "FSSIG\u0001\u0000"u8, .. Encoding.UTF8.GetBytes(bad)]);
        Assert.Equal(good.Id, Assert.Single(new SavedSignatureStore(_folder, new NoKeyVault()).Load()).Id);
    }

    /// <summary>
    /// Writes a real key to the login Keychain and deletes it again, so it only runs when asked:
    /// FILLSIGN_KEYCHAIN_TESTS=1.
    /// </summary>
    [Fact]
    public void The_macOS_Keychain_keeps_the_key()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("FILLSIGN_KEYCHAIN_TESTS") == "1", "Set FILLSIGN_KEYCHAIN_TESTS=1 on macOS to run.");
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var vault = new MacKeychainVault("Fillsign tests", $"Test key {Guid.NewGuid():N}");
        try
        {
            byte[] created = vault.GetOrCreateKey()!;
            Assert.Equal(SignatureKeyVaults.KeyLength, created.Length);
            Assert.Equal(created, vault.GetOrCreateKey());
        }
        finally
        {
            vault.Delete();
        }
    }

    /// <summary>On Windows: DPAPI protects the key file for this user, and it reads back the same key.</summary>
    [Fact]
    public void Windows_DPAPI_keeps_the_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string keyFile = Path.Combine(_folder, "signatures.key");
        var vault = new WindowsDpapiVault(keyFile);
        byte[] created = vault.GetOrCreateKey()!;
        Assert.Equal(SignatureKeyVaults.KeyLength, created.Length);
        Assert.Equal(created, new WindowsDpapiVault(keyFile).GetOrCreateKey());

        // What's on disk is protected, not the key itself.
        byte[] onDisk = File.ReadAllBytes(keyFile);
        Assert.False(onDisk.AsSpan().IndexOf(created) >= 0, "the key file holds the key in plain form");
    }

    private string FilePath => Path.Combine(_folder, SavedSignatureStore.FileName);

    private static IEqualityComparer<ImmutableArray<byte>> ImmutableArrayComparer { get; } =
        EqualityComparer<ImmutableArray<byte>>.Create((a, b) => a.AsSpan().SequenceEqual(b.AsSpan()), array => array.Length);

    private static SavedSignature Saved(SignatureKind kind, SignatureMethod method, DateTimeOffset lastUsed)
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', .. Guid.NewGuid().ToByteArray()];
        return new SavedSignature(new SignatureImage(Guid.NewGuid(), [.. png], 40, 12), kind, method, lastUsed);
    }

    private SavedSignaturesState LoadedState(ISignatureKeyVault vault)
    {
        var store = new SavedSignatureStore(_folder, vault);
        store.Load();
        return store.State;
    }

    private sealed class FixedKeyVault(byte seed = 7) : ISignatureKeyVault
    {
        public bool HasSecretStore => true;

        public byte[]? FindKey() => GetOrCreateKey();

        public byte[]? GetOrCreateKey() => [.. Enumerable.Repeat(seed, SignatureKeyVaults.KeyLength)];
    }

    private sealed class LockedVault : ISignatureKeyVault
    {
        public bool HasSecretStore => true;

        public byte[]? FindKey() => throw new SignatureKeyUnavailableException("denied");

        public byte[]? GetOrCreateKey() => throw new SignatureKeyUnavailableException("denied");
    }

    /// <summary>A secret store with no key in it, which makes one when asked to.</summary>
    private sealed class MissingKeyVault : ISignatureKeyVault
    {
        public bool HasSecretStore => true;

        public bool HasCreatedKey { get; private set; }

        public byte[]? FindKey() => HasCreatedKey ? Key : null;

        public byte[]? GetOrCreateKey()
        {
            HasCreatedKey = true;
            return Key;
        }

        private static byte[] Key => [.. Enumerable.Repeat((byte)9, SignatureKeyVaults.KeyLength)];
    }
}
