using System.Text;
using Fillsign.Core.Model;
using Fillsign.Core.Session;

namespace Fillsign.Core.Tests.Session;

public sealed class SessionStoreTests
{
    private static readonly DocumentState s_document = TestData.FourPagesWithItems();

    private static byte[] SavedWorkingCopy(out byte[] original)
    {
        original = TestData.MinimalPdf();
        return SessionStore.Append(original, ByteStamp.Of(original), 4, s_document);
    }

    [Fact]
    public void A_plain_pdf_has_no_record()
    {
        Assert.Equal(SessionStatus.None, SessionStore.Find(TestData.MinimalPdf()).Status);
        Assert.Equal(SessionStatus.None, SessionStore.Find("not a pdf"u8).Status);
        Assert.Equal(SessionStatus.None, SessionStore.Find([]).Status);
    }

    [Fact]
    public void An_untouched_working_copy_restores()
    {
        byte[] saved = SavedWorkingCopy(out byte[] original);
        SessionLookup lookup = SessionStore.Find(saved);
        Assert.Equal(SessionStatus.Intact, lookup.Status);
        Assert.True(lookup.IsOriginalAvailable);
        Assert.Equal(s_document, lookup.Record!.Document);
        Assert.Equal(original.Length, lookup.Record.Base.Length);

        string tail = Encoding.ASCII.GetString(saved[original.Length..]);
        Assert.Contains("/Prev ", tail, StringComparison.Ordinal);
        Assert.Contains("/Root 1 0 R", tail, StringComparison.Ordinal);
        Assert.Contains("/ID [<0123456789abcdef", tail, StringComparison.Ordinal);
        Assert.Contains("/Size 5", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_again_does_not_grow_the_file()
    {
        byte[] saved = SavedWorkingCopy(out byte[] original);
        byte[] again = SessionStore.Append(SessionStore.WithoutRecord(saved), ByteStamp.Of(original), 4, s_document);
        Assert.Equal(saved.Length, again.Length);
        Assert.Equal(SessionStatus.Intact, SessionStore.Find(again).Status);
    }

    [Fact]
    public void A_change_by_another_app_is_detected()
    {
        byte[] saved = SavedWorkingCopy(out byte[] original);

        // Another app edits a byte in the content and keeps our trailer (as PDFium does).
        byte[] edited = (byte[])saved.Clone();
        edited[original.Length - 30] ^= 0x20;
        SessionLookup lookup = SessionStore.Find(edited);
        Assert.Equal(SessionStatus.ChangedElsewhere, lookup.Status);
        Assert.NotNull(lookup.Record);

        // Another app appends its own incremental update that keeps our key.
        string objectPart = "\n5 0 obj\n<<>>\nendobj\n";
        int xref = saved.Length + objectPart.Length;
        string update = objectPart + $"xref\n5 1\n{saved.Length + 1:D10} 00000 n\r\ntrailer\n<</Size 6/Root 1 0 R/FillsignSession 4 0 R/Prev 0>>\nstartxref\n{xref}\n%%EOF\n";
        byte[] appended = [.. saved, .. Encoding.ASCII.GetBytes(update)];
        Assert.Equal(SessionStatus.ChangedElsewhere, SessionStore.Find(appended).Status);
    }

    [Fact]
    public void A_damaged_record_is_reported_not_thrown()
    {
        byte[] saved = SavedWorkingCopy(out byte[] original);
        int streamStart = Encoding.ASCII.GetString(saved).IndexOf("stream\n", original.Length, StringComparison.Ordinal) + 7;
        saved[streamStart + 3] ^= 0xFF;
        SessionLookup lookup = SessionStore.Find(saved);
        Assert.Equal(SessionStatus.Damaged, lookup.Status);
        Assert.NotNull(lookup.Problem);
    }

    [Fact]
    public void Fuzzed_files_never_throw()
    {
        byte[] saved = SavedWorkingCopy(out _);
        var random = new Random(1811);
        var seen = new HashSet<SessionStatus>();
        for (int i = 0; i < 5000; i++)
        {
            // Concentrate damage on the tail, where the parser reads.
            byte[] mutated = (byte[])saved.Clone();
            int tailStart = Math.Max(0, saved.Length - 600);
            byte[] tail = SessionJsonTests.Mutate(mutated[tailStart..], random);
            mutated = [.. mutated[..tailStart], .. tail];
            seen.Add(SessionStore.Find(mutated).Status);
        }

        Assert.Contains(SessionStatus.Damaged, seen);
    }
}
