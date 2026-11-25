using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using static Chainpen.Pdf.Pdfium.PdfiumNative;

namespace Chainpen.Pdf.Pdfium;

public sealed partial class PdfiumDocument
{
    /// <summary>The largest XFA template read for descriptions; real ones are a few hundred KB.</summary>
    private const int MaxXfaTemplateBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The deepest nesting read. Real templates nest a dozen or so elements; one nested thousands deep is made to
    /// run the walk below out of stack (which closes the app) or to take minutes to load.
    /// </summary>
    private const int MaxXfaDepth = 256;

    /// <summary>
    /// Field descriptions from the document's XFA template, by the field's full name as its fields have it
    /// ("topmostSubform[0].Page1[0].f1_01[0]"). Forms made with XFA tools (the IRS's, many governments') keep their
    /// tooltips there rather than on the fields. Empty when there's no template or it can't be read. The XML is
    /// untrusted: no DTDs and nothing outside the file is resolved.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetXfaDescriptions()
    {
        byte[]? template = ReadXfaTemplate();
        return template is null ? new Dictionary<string, string>() : XfaDescriptions(template);
    }

    /// <summary>The descriptions in an XFA template's XML; empty when it isn't XML or is nested too deep to be real.</summary>
    internal static IReadOnlyDictionary<string, string> XfaDescriptions(byte[] template)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXfaTemplateBytes };

            // Checked in a pass of its own, which takes no stack and no time to speak of, before anything is built.
            using (var depths = XmlReader.Create(new MemoryStream(template), settings))
            {
                while (depths.Read())
                {
                    if (depths.Depth > MaxXfaDepth)
                    {
                        return new Dictionary<string, string>();
                    }
                }
            }

            using var reader = XmlReader.Create(new MemoryStream(template), settings);
            XElement root = XElement.Load(reader);
            var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            Collect(root, string.Empty, descriptions);
            return descriptions;
        }
        catch (XmlException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Walks containers the way XFA names them: each named subform, exclusion group (radio group) or field adds
    /// "name[n]", n counting earlier siblings of the same name; unnamed containers and areas add nothing.
    /// </summary>
    private static void Collect(XElement container, string path, Dictionary<string, string> descriptions)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (XElement child in container.Elements())
        {
            string kind = child.Name.LocalName;
            if (kind is not ("subform" or "subformSet" or "area" or "exclGroup" or "field"))
            {
                continue;
            }

            string? name = (string?)child.Attribute("name");
            string childPath = path;
            if (!string.IsNullOrEmpty(name) && kind is not ("area" or "subformSet"))
            {
                int index = seen.GetValueOrDefault(name);
                seen[name] = index + 1;
                childPath = path.Length > 0 ? $"{path}.{name}[{index}]" : $"{name}[{index}]";
                if (kind is "field" or "exclGroup" && Description(child) is { Length: > 0 } description)
                {
                    descriptions[childPath] = description;
                }
            }

            if (kind != "field")
            {
                Collect(child, childPath, descriptions);
            }
        }
    }

    /// <summary>The tooltip, else what a screen reader is told to say, else the caption.</summary>
    private static string? Description(XElement element)
    {
        XElement? assist = element.Elements().FirstOrDefault(e => e.Name.LocalName == "assist");
        string? text = assist?.Elements().FirstOrDefault(e => e.Name.LocalName == "toolTip")?.Value
            ?? assist?.Elements().FirstOrDefault(e => e.Name.LocalName == "speak")?.Value
            ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "caption")?.Descendants().FirstOrDefault(e => e.Name.LocalName is "text" or "exData")?.Value;
        return text is null ? null : string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    private byte[]? ReadXfaTemplate()
    {
        lock (PdfiumLibrary.Sync)
        {
            ObjectDisposedException.ThrowIf(_document == 0, this);
            int count = FPDF_GetXFAPacketCount(_document);
            for (int index = 0; index < count; index++)
            {
                unsafe
                {
                    int nameLength = (int)FPDF_GetXFAPacketName(_document, index, null, new CULong(0)).Value;
                    if (nameLength <= 0 || nameLength > 256)
                    {
                        continue;
                    }

                    byte[] nameBytes = new byte[nameLength];
                    fixed (byte* buffer = nameBytes)
                    {
                        FPDF_GetXFAPacketName(_document, index, buffer, new CULong((nuint)nameLength));
                    }

                    if (Encoding.UTF8.GetString(nameBytes).TrimEnd('\0') != "template")
                    {
                        continue;
                    }

                    if (!FPDF_GetXFAPacketContent(_document, index, null, new CULong(0), out CULong size) || size.Value == 0 || size.Value > MaxXfaTemplateBytes)
                    {
                        return null;
                    }

                    byte[] content = new byte[(int)size.Value];
                    fixed (byte* buffer = content)
                    {
                        return FPDF_GetXFAPacketContent(_document, index, buffer, size, out CULong written)
                            ? content[..(int)Math.Min((ulong)content.Length, written.Value)]
                            : null;
                    }
                }
            }

            return null;
        }
    }
}
