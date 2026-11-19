using System.Text;
using System.Text.Json.Nodes;
using Fillsign.Core.Editing;
using Fillsign.Core.Model;
using Fillsign.Core.Session;

namespace Fillsign.Core.Tests.Session;

public sealed class SessionJsonTests
{
    private static SessionRecord Sample()
    {
        DocumentState document = TestData.FourPagesWithItems();
        document = document.WithPages(document.Pages.SetItem(1, document.Pages[1] with { Rotation = Rotation.FromDegrees(270) }));
        document = ItemEdits.Add(document, new ShapeItem(ItemId.New(), document.Pages[2].Id, 1, 2, ShapeKind.Highlight, 80, 14, InkColor.Blue, 2, true, false));
        document = ItemEdits.Add(document, new TextItem(ItemId.New(), document.Pages[2].Id, 5, 6, "Bold\nright", 14, InkColor.Purple, IsBold: true, Align: TextAlign.Right));
        document = ItemEdits.Add(document, new PictureItem(ItemId.New(), document.Pages[3].Id, 50, 60, TestData.Png(), 150, 50));
        document = document.WithField("A.NOM", "Lee").WithField("R.CM", "Non").WithField("Notes", "two\nlines");
        return new SessionRecord(ByteStamp.Of("original"u8), ByteStamp.Of("original plus edits"u8), 4, document);
    }

    [Fact]
    public void Round_trips_every_item_type()
    {
        SessionRecord record = Sample();
        SessionRecord back = SessionJson.Read(SessionJson.Write(record));
        Assert.Equal(record.Base, back.Base);
        Assert.Equal(record.Content, back.Content);
        Assert.Equal(record.SourcePageCount, back.SourcePageCount);
        Assert.Equal(record.Document, back.Document);
        Assert.Equal(
            record.Document.Items.OfType<SignatureItem>().Single().Image.Png,
            back.Document.Items.OfType<SignatureItem>().Single().Image.Png);
        Assert.Equal(
            record.Document.Items.OfType<PictureItem>().Single().Image.Png,
            back.Document.Items.OfType<PictureItem>().Single().Image.Png);
    }

    [Fact]
    public void Plain_text_leaves_out_bold_and_alignment_so_older_records_read_the_same()
    {
        string json = System.Text.Encoding.UTF8.GetString(SessionJson.Write(Sample()));
        Assert.Equal(1, CountOf(json, "\"bold\""));
        Assert.Equal(1, CountOf(json, "\"align\":\"right\""));

        static int CountOf(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
    }

    [Fact]
    public void Signatures_sharing_an_image_store_it_once()
    {
        SessionRecord record = Sample();
        SignatureItem signature = record.Document.Items.OfType<SignatureItem>().Single();
        DocumentState twice = ItemEdits.Add(record.Document, signature with { Id = ItemId.New() });
        JsonNode json = JsonNode.Parse(SessionJson.Write(record with { Document = twice }))!;

        // The signature's image once, plus the sample's pasted picture.
        Assert.Equal(2, json["images"]!.AsArray().Count);
    }

    public static TheoryData<string, string> Tampering() => new()
    {
        { "unknown property", """{"surprise":1}""" },
        { "wrong format", """{"format":"something-else"}""" },
        { "future version", """{"version":2}""" },
        { "page count out of range", """{"sourcePageCount":0}""" },
        { "bad fingerprint", """{"base":{"length":8,"sha256":"XYZ"}}""" },
        { "original longer than content", """{"base":{"length":99999,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}""" },
        { "no pages", """{"pages":[]}""" },
        { "wrong type", """{"pages":"none"}""" },
        { "nested too deep", """{"items":[[[[[[[[[[[]]]]]]]]]]]}""" },
    };

    [Theory]
    [MemberData(nameof(Tampering))]
    public void Rejects_tampered_records(string why, string patch)
    {
        JsonObject json = JsonNode.Parse(SessionJson.Write(Sample()))!.AsObject();
        foreach (var (key, value) in JsonNode.Parse(patch)!.AsObject())
        {
            json[key] = value?.DeepClone();
        }

        var ex = Assert.Throws<SessionFormatException>(() => SessionJson.Read(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.False(string.IsNullOrEmpty(ex.Message), why);
    }

    public static TheoryData<string, Action<JsonObject>> BadItems() => new()
    {
        { "item on a missing page", item => item["page"] = Guid.NewGuid().ToString("N") },
        { "coordinate is NaN text", item => item["x"] = "NaN" },
        { "coordinate far off the page", item => item["x"] = 1e9 },
        { "unknown type", item => item["type"] = "sticker" },
        { "missing id", item => item.Remove("id") },
        { "extra property", item => item["script"] = "alert(1)" },
    };

    [Theory]
    [MemberData(nameof(BadItems))]
    public void Rejects_bad_items(string why, Action<JsonObject> spoil)
    {
        JsonObject json = JsonNode.Parse(SessionJson.Write(Sample()))!.AsObject();
        spoil(json["items"]![0]!.AsObject());
        Assert.Throws<SessionFormatException>(() => SessionJson.Read(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.NotEmpty(why);
    }

    [Fact]
    public void Rejects_duplicate_ids_bad_ink_and_non_png_images()
    {
        JsonObject json = JsonNode.Parse(SessionJson.Write(Sample()))!.AsObject();
        JsonArray items = json["items"]!.AsArray();

        var duplicate = (JsonObject)json.DeepClone();
        duplicate["items"]![1]!["id"] = items[0]!["id"]!.GetValue<string>();
        Assert.Throws<SessionFormatException>(() => SessionJson.Read(Encoding.UTF8.GetBytes(duplicate.ToJsonString())));

        var ink = (JsonObject)json.DeepClone();
        JsonObject inkItem = ink["items"]!.AsArray().Single(i => i!["type"]!.GetValue<string>() == "ink")!.AsObject();
        inkItem["strokes"] = JsonNode.Parse("[[0.5, 2]]");
        Assert.Throws<SessionFormatException>(() => SessionJson.Read(Encoding.UTF8.GetBytes(ink.ToJsonString())));
        inkItem["strokes"] = JsonNode.Parse("[[0.5]]");
        Assert.Throws<SessionFormatException>(() => SessionJson.Read(Encoding.UTF8.GetBytes(ink.ToJsonString())));

        var image = (JsonObject)json.DeepClone();
        image["images"]![0]!["png"] = Convert.ToBase64String("<svg/>"u8);
        Assert.Throws<SessionFormatException>(() => SessionJson.Read(Encoding.UTF8.GetBytes(image.ToJsonString())));
    }

    /// <summary>
    /// Mutates a valid record thousands of ways (flipped bytes, truncation, spliced bytes). Every result must either
    /// read cleanly or throw <see cref="SessionFormatException"/>; nothing else may escape.
    /// </summary>
    [Fact]
    public void Fuzzed_records_never_throw_anything_else()
    {
        byte[] valid = SessionJson.Write(Sample());
        var random = new Random(20261118);
        int rejected = 0;
        for (int i = 0; i < 5000; i++)
        {
            byte[] mutated = Mutate(valid, random);
            try
            {
                SessionJson.Read(mutated);
            }
            catch (SessionFormatException)
            {
                rejected++;
            }
        }

        Assert.True(rejected > 4000, $"only {rejected} of 5000 mutations were rejected");
    }

    internal static byte[] Mutate(byte[] valid, Random random)
    {
        byte[] data = (byte[])valid.Clone();
        switch (random.Next(4))
        {
            case 0:
                for (int n = random.Next(1, 4); n > 0; n--)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }

                return data;
            case 1:
                return data[..random.Next(data.Length)];
            case 2:
            {
                int at = random.Next(data.Length);
                string[] tokens = ["{", "}", "[", "]", ",", "\"", "-1e999", "null", "9999999999999999999999"];
                byte[] junk = Encoding.ASCII.GetBytes(tokens[random.Next(tokens.Length)]);
                return [.. data[..at], .. junk, .. data[at..]];
            }

            default:
            {
                // Swap two token-ish characters (often turns numbers into strings or breaks nesting).
                int a = random.Next(data.Length), b = random.Next(data.Length);
                (data[a], data[b]) = (data[b], data[a]);
                return data;
            }
        }
    }
}
