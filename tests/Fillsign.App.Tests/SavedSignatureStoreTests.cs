using System.Collections.Immutable;
using System.Text;
using Fillsign.App.Platform.Signatures;
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

    private string FilePath => Path.Combine(_folder, SavedSignatureStore.FileName);

    private static IEqualityComparer<ImmutableArray<byte>> ImmutableArrayComparer { get; } =
        EqualityComparer<ImmutableArray<byte>>.Create((a, b) => a.AsSpan().SequenceEqual(b.AsSpan()), array => array.Length);

    private static SavedSignature Saved(SignatureKind kind, SignatureMethod method, DateTimeOffset lastUsed)
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', .. Guid.NewGuid().ToByteArray()];
        return new SavedSignature(new SignatureImage(Guid.NewGuid(), [.. png], 40, 12), kind, method, lastUsed);
    }

    private sealed class FixedKeyVault(byte seed = 7) : ISignatureKeyVault
    {
        public byte[]? GetOrCreateKey() => [.. Enumerable.Repeat(seed, SignatureKeyVaults.KeyLength)];
    }

    private sealed class LockedVault : ISignatureKeyVault
    {
        public byte[]? GetOrCreateKey() => throw new SignatureKeyUnavailableException("denied");
    }
}
