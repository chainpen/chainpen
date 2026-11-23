using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Chainpen.Core.Model;

namespace Chainpen.Core.Session;

/// <summary>
/// Writes and strictly reads the editable record as JSON (FR-9.5). The reader treats its input as untrusted: unknown
/// or missing properties, wrong types, out-of-range numbers, dangling references and oversized data all reject the
/// whole record, so a damaged or tampered record is ignored rather than half-applied.
/// </summary>
public static class SessionJson
{
    public const string FormatName = "chainpen-session";

    /// <summary>Larger records are rejected before parsing.</summary>
    public const int MaxRecordBytes = 64 * 1024 * 1024;

    public const int MaxPages = 10_000;
    public const int MaxItems = 100_000;
    public const int MaxTextLength = TextItem.MaxLength;
    public const int MaxInkPoints = 200_000;
    public const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The most pixels a picture may decode to, as for opened photos (ImageToPdf.MaxDecodePixels): a few megabytes of
    /// PNG can otherwise claim billions of pixels and use up all the memory when drawn.
    /// </summary>
    public const long MaxImagePixels = 50_000_000;

    // Generous bounds for coordinates and sizes; real pages are at most 14,400 pt (PDF's limit).
    private const double MaxCoordinate = 100_000;

    /// <summary>
    /// Checks the size in the PNG's own header (its first chunk, IHDR), which is what a decoder allocates for, not the
    /// size the record states.
    /// </summary>
    private static void CheckPngSize(byte[] png)
    {
        // Signature (8), then the header chunk: length (4), "IHDR" (4), width (4), height (4).
        if (png.Length < 24 || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new SessionFormatException("A picture's PNG has no header.");
        }

        long width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16));
        long height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
        if (width == 0 || height == 0 || width * height > MaxImagePixels)
        {
            throw new SessionFormatException($"A picture is {width} × {height} pixels, more than Chainpen opens.");
        }
    }

    // Room kept in a record for everything that isn't a picture: pages, text, drawings and field values.
    private const long ReservedForTheRest = 8 * 1024 * 1024;

    /// <summary>
    /// Whether a document could take this picture and still be read back from its working copy. The reader turns away
    /// a picture over <see cref="MaxImageBytes"/> and a record over <see cref="MaxRecordBytes"/>, and pictures (stored
    /// as base64, a third larger than their PNG) are nearly all of a record; so a picture that doesn't fit has to be
    /// refused when it's added, not found out about when the saved file won't open for editing.
    /// </summary>
    public static bool HasRoomFor(DocumentState document, SignatureImage picture)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(picture);
        if (picture.Png.Length > MaxImageBytes)
        {
            return false;
        }

        long pngBytes = picture.Png.Length;
        foreach (SignatureImage image in ImagesOf(document))
        {
            pngBytes += image == picture ? 0 : image.Png.Length;
        }

        return (pngBytes + 2) / 3 * 4 <= MaxRecordBytes - ReservedForTheRest;
    }

    /// <summary>Each signature and picture in the document once, however many times it's placed.</summary>
    private static List<SignatureImage> ImagesOf(DocumentState document) =>
    [
        .. document.Items
            .Select(item => item switch { SignatureItem signature => signature.Image, PictureItem picture => picture.Image, _ => null })
            .OfType<SignatureImage>()
            .Distinct(),
    ];

    private static readonly byte[] s_pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Write(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("format", FormatName);
            json.WriteNumber("version", SessionRecord.CurrentVersion);
            WriteStamp(json, "base", record.Base);
            WriteStamp(json, "content", record.Content);
            json.WriteNumber("sourcePageCount", record.SourcePageCount);

            json.WriteStartArray("pages");
            foreach (PageEntry page in record.Document.Pages)
            {
                json.WriteStartObject();
                json.WriteString("id", page.Id.ToString());
                json.WriteNumber("source", page.SourceIndex);
                json.WriteNumber("rotation", page.Rotation.Degrees);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            List<SignatureImage> images = ImagesOf(record.Document);
            json.WriteStartArray("images");
            foreach (SignatureImage image in images)
            {
                json.WriteStartObject();
                json.WriteString("id", image.Id.ToString("N", CultureInfo.InvariantCulture));
                json.WriteBase64String("png", image.Png.AsSpan());
                json.WriteNumber("pixelWidth", image.PixelWidth);
                json.WriteNumber("pixelHeight", image.PixelHeight);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("items");
            foreach (Item item in record.Document.Items)
            {
                WriteItem(json, item);
            }

            json.WriteEndArray();

            // FS-950: values typed into the PDF's own form fields. Left out when there are none.
            if (!record.Document.Fields.IsEmpty)
            {
                json.WriteStartArray("fields");
                foreach ((string name, string value) in record.Document.Fields)
                {
                    json.WriteStartObject();
                    json.WriteString("name", name);
                    json.WriteString("value", value);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Reads a record, or throws <see cref="SessionFormatException"/> if anything about it is wrong.</summary>
    public static SessionRecord Read(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxRecordBytes)
        {
            throw new SessionFormatException("The record is too large.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException ex)
        {
            throw new SessionFormatException("The record isn't valid JSON.", ex);
        }

        using (document)
        {
            try
            {
                return ReadRecord(document.RootElement);
            }
            catch (InvalidOperationException ex)
            {
                // JsonElement throws this for a value of the wrong kind.
                throw new SessionFormatException("A value has the wrong type.", ex);
            }
            catch (FormatException ex)
            {
                throw new SessionFormatException("A value is badly formed.", ex);
            }
        }
    }

    private static void WriteStamp(Utf8JsonWriter json, string name, ByteStamp stamp)
    {
        json.WriteStartObject(name);
        json.WriteNumber("length", stamp.Length);
        json.WriteString("sha256", stamp.Sha256);
        json.WriteEndObject();
    }

    private static void WriteItem(Utf8JsonWriter json, Item item)
    {
        json.WriteStartObject();
        json.WriteString("type", item switch
        {
            TextItem => "text",
            MarkItem => "mark",
            SignatureItem => "signature",
            PictureItem => "picture",
            ShapeItem => "shape",
            InkItem => "ink",
        });
        json.WriteString("id", item.Id.ToString());
        json.WriteString("page", item.Page.ToString());
        json.WriteNumber("x", item.X);
        json.WriteNumber("y", item.Y);
        switch (item)
        {
            case TextItem text:
                json.WriteString("text", text.Text);
                json.WriteNumber("fontSize", text.FontSize);
                json.WriteString("color", text.Color.ToHex());
                if (text.IsBold)
                {
                    json.WriteBoolean("bold", true);
                }

                if (text.Align != TextAlign.Left)
                {
                    json.WriteString("align", text.Align == TextAlign.Centre ? "centre" : "right");
                }

                break;

            case MarkItem mark:
                json.WriteString("kind", mark.Kind == MarkKind.Tick ? "tick" : "cross");
                json.WriteNumber("size", mark.Size);
                json.WriteString("color", mark.Color.ToHex());
                break;

            case SignatureItem signature:
                json.WriteString("kind", signature.Kind == SignatureKind.Signature ? "signature" : "initials");
                json.WriteString("image", signature.Image.Id.ToString("N", CultureInfo.InvariantCulture));
                json.WriteNumber("width", signature.Width);
                json.WriteNumber("height", signature.Height);
                break;

            case PictureItem picture:
                json.WriteString("image", picture.Image.Id.ToString("N", CultureInfo.InvariantCulture));
                json.WriteNumber("width", picture.Width);
                json.WriteNumber("height", picture.Height);
                break;

            case ShapeItem shape:
                json.WriteString("shape", ShapeName(shape.Kind));
                json.WriteNumber("width", shape.Width);
                json.WriteNumber("height", shape.Height);
                json.WriteString("color", shape.Color.ToHex());
                json.WriteNumber("strokeWidth", shape.StrokeWidth);
                json.WriteBoolean("flipX", shape.FlipX);
                json.WriteBoolean("flipY", shape.FlipY);
                break;

            case InkItem ink:
                json.WriteNumber("width", ink.Width);
                json.WriteNumber("height", ink.Height);
                json.WriteString("color", ink.Color.ToHex());
                json.WriteNumber("strokeWidth", ink.StrokeWidth);
                json.WriteStartArray("strokes");
                foreach (var stroke in ink.Strokes)
                {
                    // Flat [x0, y0, x1, y1, …] keeps the record compact.
                    json.WriteStartArray();
                    foreach (PagePoint point in stroke)
                    {
                        json.WriteNumberValue(Math.Round(point.X, 5));
                        json.WriteNumberValue(Math.Round(point.Y, 5));
                    }

                    json.WriteEndArray();
                }

                json.WriteEndArray();
                break;
        }

        json.WriteEndObject();
    }

    private static string ShapeName(ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => "rectangle",
        ShapeKind.Ellipse => "ellipse",
        ShapeKind.Line => "line",
        ShapeKind.Arrow => "arrow",
        ShapeKind.Highlight => "highlight",
        ShapeKind.Whiteout => "whiteout",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shape."),
    };

    private static SessionRecord ReadRecord(JsonElement root)
    {
        var record = new StrictObject(root, "record");
        if (record.String("format", 64) != FormatName)
        {
            throw new SessionFormatException("Not a Chainpen record.");
        }

        int version = record.Int("version", 1, int.MaxValue);
        if (version != SessionRecord.CurrentVersion)
        {
            throw new SessionFormatException($"Record version {version} isn't supported.");
        }

        ByteStamp baseStamp = ReadStamp(record.Object("base"));
        ByteStamp content = ReadStamp(record.Object("content"));
        if (baseStamp.Length > content.Length)
        {
            throw new SessionFormatException("The original can't be longer than the saved file.");
        }

        int sourcePageCount = record.Int("sourcePageCount", 1, MaxPages);

        var pages = ImmutableArray.CreateBuilder<PageEntry>();
        var pageIds = new HashSet<PageId>();
        foreach (JsonElement element in record.Array("pages", 1, MaxPages))
        {
            var page = new StrictObject(element, "page");
            var id = new PageId(page.Guid("id"));
            int source = page.Int("source", 0, sourcePageCount - 1);
            int degrees = page.Int("rotation", 0, 270);
            if (degrees % 90 != 0)
            {
                throw new SessionFormatException("Page rotation must be a multiple of 90.");
            }

            page.EnsureNoOtherProperties();
            if (!pageIds.Add(id))
            {
                throw new SessionFormatException("Two pages share an id.");
            }

            pages.Add(new PageEntry(id, source, Rotation.FromDegrees(degrees)));
        }

        var images = new Dictionary<Guid, SignatureImage>();
        foreach (JsonElement element in record.Array("images", 0, MaxItems))
        {
            var image = new StrictObject(element, "image");
            Guid id = image.Guid("id");
            byte[] png = image.Base64("png", MaxImageBytes);
            if (!png.AsSpan().StartsWith(s_pngSignature))
            {
                throw new SessionFormatException("A signature image isn't a PNG.");
            }

            CheckPngSize(png);

            int width = image.Int("pixelWidth", 1, 20_000);
            int height = image.Int("pixelHeight", 1, 20_000);
            image.EnsureNoOtherProperties();
            if (!images.TryAdd(id, new SignatureImage(id, [.. png], width, height)))
            {
                throw new SessionFormatException("Two images share an id.");
            }
        }

        var items = ImmutableList.CreateBuilder<Item>();
        var itemIds = new HashSet<ItemId>();
        foreach (JsonElement element in record.Array("items", 0, MaxItems))
        {
            Item item = ReadItem(new StrictObject(element, "item"), pageIds, images);
            if (!itemIds.Add(item.Id))
            {
                throw new SessionFormatException("Two items share an id.");
            }

            items.Add(item);
        }

        var fields = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (record.Has("fields"))
        {
            foreach (JsonElement element in record.Array("fields", 0, MaxItems))
            {
                var field = new StrictObject(element, "field");
                string name = field.String("name", DocumentState.MaxFieldNameLength);
                string value = field.String("value", DocumentState.MaxFieldValueLength);
                field.EnsureNoOtherProperties();
                if (name.Length == 0 || !fields.TryAdd(name, value))
                {
                    throw new SessionFormatException("A form field is named twice, or not at all.");
                }
            }
        }

        record.EnsureNoOtherProperties();
        return new SessionRecord(baseStamp, content, sourcePageCount, new DocumentState(pages.ToImmutable(), items.ToImmutable()) { Fields = fields.ToImmutable() });
    }

    private static ByteStamp ReadStamp(StrictObject stamp)
    {
        long length = stamp.Long("length", 1, long.MaxValue);
        string sha = stamp.String("sha256", 64);
        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower))
        {
            throw new SessionFormatException("A fingerprint is badly formed.");
        }

        stamp.EnsureNoOtherProperties();
        return new ByteStamp(length, sha);
    }

    private static Item ReadItem(StrictObject item, HashSet<PageId> pages, Dictionary<Guid, SignatureImage> images)
    {
        string type = item.String("type", 16);
        var id = new ItemId(item.Guid("id"));
        var page = new PageId(item.Guid("page"));
        if (!pages.Contains(page))
        {
            throw new SessionFormatException("An item belongs to a page that doesn't exist.");
        }

        double x = item.Number("x", -MaxCoordinate, MaxCoordinate);
        double y = item.Number("y", -MaxCoordinate, MaxCoordinate);
        Item result = type switch
        {
            "text" => new TextItem(
                id,
                page,
                x,
                y,
                item.String("text", MaxTextLength),
                item.Number("fontSize", TextItem.MinFontSize, TextItem.MaxFontSize),
                item.Color("color"),
                item.Has("bold") && item.Bool("bold"),
                item.Has("align")
                    ? item.String("align", 8) switch
                    {
                        "left" => TextAlign.Left,
                        "centre" => TextAlign.Centre,
                        "right" => TextAlign.Right,
                        _ => throw new SessionFormatException("A text alignment isn't known."),
                    }
                    : TextAlign.Left),
            "mark" => new MarkItem(
                id,
                page,
                x,
                y,
                item.String("kind", 16) switch
                {
                    "tick" => MarkKind.Tick,
                    "cross" => MarkKind.Cross,
                    _ => throw new SessionFormatException("Unknown mark."),
                },
                item.Number("size", MarkItem.MinSize, MarkItem.MaxSize),
                item.Color("color")),
            "signature" => new SignatureItem(
                id,
                page,
                x,
                y,
                item.String("kind", 16) switch
                {
                    "signature" => SignatureKind.Signature,
                    "initials" => SignatureKind.Initials,
                    _ => throw new SessionFormatException("Unknown signature kind."),
                },
                images.TryGetValue(item.Guid("image"), out SignatureImage? image) ? image : throw new SessionFormatException("A signature's image is missing."),
                item.Number("width", 0.1, MaxCoordinate),
                item.Number("height", 0.1, MaxCoordinate)),
            "picture" => new PictureItem(
                id,
                page,
                x,
                y,
                images.TryGetValue(item.Guid("image"), out SignatureImage? picture) ? picture : throw new SessionFormatException("A picture's image is missing."),
                item.Number("width", 0.1, MaxCoordinate),
                item.Number("height", 0.1, MaxCoordinate)),
            "shape" => new ShapeItem(
                id,
                page,
                x,
                y,
                item.String("shape", 16) switch
                {
                    "rectangle" => ShapeKind.Rectangle,
                    "ellipse" => ShapeKind.Ellipse,
                    "line" => ShapeKind.Line,
                    "arrow" => ShapeKind.Arrow,
                    "highlight" => ShapeKind.Highlight,
                    "whiteout" => ShapeKind.Whiteout,
                    _ => throw new SessionFormatException("Unknown shape."),
                },
                item.Number("width", 0, MaxCoordinate),
                item.Number("height", 0, MaxCoordinate),
                item.Color("color"),
                item.Number("strokeWidth", InkItem.MinStrokeWidth, InkItem.MaxStrokeWidth),
                item.Bool("flipX"),
                item.Bool("flipY")),
            "ink" => ReadInk(item, id, page, x, y),
            _ => throw new SessionFormatException($"Unknown item type '{type}'."),
        };
        item.EnsureNoOtherProperties();
        return result;
    }

    private static InkItem ReadInk(StrictObject item, ItemId id, PageId page, double x, double y)
    {
        double width = item.Number("width", 0, MaxCoordinate);
        double height = item.Number("height", 0, MaxCoordinate);
        InkColor color = item.Color("color");
        double strokeWidth = item.Number("strokeWidth", InkItem.MinStrokeWidth, InkItem.MaxStrokeWidth);
        var strokes = ImmutableArray.CreateBuilder<ImmutableArray<PagePoint>>();
        int total = 0;
        foreach (JsonElement stroke in item.Array("strokes", 1, MaxInkPoints))
        {
            int length = stroke.GetArrayLength();
            total += length / 2;
            if (length == 0 || length % 2 != 0 || total > MaxInkPoints)
            {
                throw new SessionFormatException("A pen stroke is badly formed.");
            }

            var points = ImmutableArray.CreateBuilder<PagePoint>(length / 2);
            using var values = stroke.EnumerateArray();
            while (values.MoveNext())
            {
                double px = StrictObject.Fraction(values.Current);
                values.MoveNext();
                double py = StrictObject.Fraction(values.Current);
                points.Add(new PagePoint(px, py));
            }

            strokes.Add(points.MoveToImmutable());
        }

        return new InkItem(id, page, x, y, width, height, strokes.ToImmutable(), color, strokeWidth);
    }

    /// <summary>Reads one JSON object, insisting on the expected properties and nothing else.</summary>
    private sealed class StrictObject
    {
        private readonly JsonElement _element;
        private readonly string _what;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public StrictObject(JsonElement element, string what)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new SessionFormatException($"Expected a {what} object.");
            }

            _element = element;
            _what = what;
        }

        public static double Fraction(JsonElement value)
        {
            double number = value.GetDouble();
            return number is >= 0 and <= 1 ? number : throw new SessionFormatException("A pen point is outside its drawing.");
        }

        public void EnsureNoOtherProperties()
        {
            foreach (JsonProperty property in _element.EnumerateObject())
            {
                if (!_seen.Contains(property.Name))
                {
                    throw new SessionFormatException($"Unexpected property '{property.Name}' in {_what}.");
                }
            }
        }

        public StrictObject Object(string name) => new(Get(name), name);

        public JsonElement.ArrayEnumerator Array(string name, int minLength, int maxLength)
        {
            JsonElement value = Get(name);
            int length = value.GetArrayLength();
            if (length < minLength || length > maxLength)
            {
                throw new SessionFormatException($"'{name}' has {length} entries.");
            }

            return value.EnumerateArray();
        }

        public string String(string name, int maxLength)
        {
            string value = Get(name).GetString() ?? throw new SessionFormatException($"'{name}' is missing.");
            return value.Length <= maxLength ? value : throw new SessionFormatException($"'{name}' is too long.");
        }

        public Guid Guid(string name) =>
            System.Guid.TryParseExact(String(name, 32), "N", out Guid id) ? id : throw new SessionFormatException($"'{name}' isn't an id.");

        public int Int(string name, int min, int max) =>
            Get(name).TryGetInt32(out int value) && value >= min && value <= max
                ? value
                : throw new SessionFormatException($"'{name}' is out of range.");

        public long Long(string name, long min, long max) =>
            Get(name).TryGetInt64(out long value) && value >= min && value <= max
                ? value
                : throw new SessionFormatException($"'{name}' is out of range.");

        public double Number(string name, double min, double max)
        {
            double value = Get(name).GetDouble();
            return double.IsFinite(value) && value >= min && value <= max ? value : throw new SessionFormatException($"'{name}' is out of range.");
        }

        public bool Bool(string name) => Get(name).GetBoolean();

        /// <summary>Whether an optional property is there (FS-952: "bold" and "align" are left out when not used).</summary>
        public bool Has(string name) => _element.TryGetProperty(name, out _);

        public InkColor Color(string name) =>
            InkColor.TryParseHex(String(name, 7), out InkColor color) ? color : throw new SessionFormatException($"'{name}' isn't a colour.");

        public byte[] Base64(string name, int maxBytes)
        {
            JsonElement value = Get(name);
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > ((maxBytes / 3) + 1) * 4)
            {
                throw new SessionFormatException($"'{name}' is too large.");
            }

            return value.GetBytesFromBase64();
        }

        private JsonElement Get(string name)
        {
            if (!_element.TryGetProperty(name, out JsonElement value))
            {
                throw new SessionFormatException($"'{name}' is missing from {_what}.");
            }

            _seen.Add(name);
            return value;
        }
    }
}
