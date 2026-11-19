using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Tests;

/// <summary>FS-950: reading and filling a PDF's own form fields.</summary>
public sealed class FormFieldTests
{
    private static PdfiumDocument Open(string name) => PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample(name)));

    [Fact]
    public void Fields_are_read_with_their_kind_value_and_place()
    {
        using PdfiumDocument pdf = Open("corpus/160F-2019.pdf");
        IReadOnlyList<FormField> fields = pdf.GetFormFields(0);

        FormField surname = Assert.Single(fields, field => field.Name == "A.NOM");
        Assert.Equal(FormFieldKind.Text, surname.Kind);
        Assert.Equal(string.Empty, surname.Value);
        Assert.True(surname.Rect.Right > surname.Rect.Left && surname.Rect.Top > surname.Rect.Bottom);

        Assert.True(Assert.Single(fields, field => field.Name == "T.TOTREM").IsReadOnly);
        FormField[] radio = [.. fields.Where(field => field.Name == "R.CM")];
        Assert.Equal(["Oui", "Non"], radio.Select(button => button.OnValue));
        Assert.All(radio, button => Assert.Equal(FormFieldKind.Radio, button.Kind));
    }

    [Fact]
    public void Filled_values_are_saved_with_the_fields_still_working()
    {
        byte[] saved;
        using (PdfiumDocument pdf = Open("corpus/160F-2019.pdf"))
        {
            pdf.FillFormFields(new Dictionary<string, string>
            {
                ["A.NOM"] = "Lee",
                ["A.PRENOM"] = "Jordan Åsa",
                ["R.CM"] = "Non",
                ["T.TOTREM"] = "999", // read-only: left alone
                ["Missing"] = "ignored",
            });
            saved = pdf.SaveCopy(removeSecurity: true);
        }

        using PdfiumDocument reopened = PdfiumDocument.Open(saved);
        IReadOnlyList<FormField> fields = reopened.GetFormFields(0);
        Assert.Equal("Lee", fields.Single(field => field.Name == "A.NOM").Value);
        Assert.Equal("Jordan Åsa", fields.Single(field => field.Name == "A.PRENOM").Value);
        Assert.Equal("0", fields.Single(field => field.Name == "T.TOTREM").Value);
        FormField[] radio = [.. fields.Where(field => field.Name == "R.CM")];
        Assert.Equal([false, true], radio.Select(button => button.IsChecked));

        // The appearance was written too, so readers that don't run forms show the value.
        using PdfiumDocument original = Open("corpus/160F-2019.pdf");
        FormField surname = fields.Single(field => field.Name == "A.NOM");
        int filledInk = InkIn(reopened, surname.Rect), emptyInk = InkIn(original, surname.Rect);
        Assert.True(filledInk > emptyInk + 20, $"the surname is drawn in its box: {filledInk} vs {emptyInk}");
    }

    /// <summary>Dark pixels inside a box (PDF user space) on page 1, rendered at one pixel per point.</summary>
    private static int InkIn(PdfiumDocument pdf, (double Left, double Bottom, double Right, double Top) box)
    {
        PdfPageInfo info = pdf.GetPageInfo(0);
        int width = (int)info.Width, height = (int)info.Height;
        var pixels = new byte[width * height * 4];
        pdf.RenderPage(0, pixels, width, height, width * 4);
        int dark = 0;
        // Pixels count from the crop box's top-left corner.
        (float left, _, _, float top) = info.CropBox;
        for (int y = (int)(top - box.Top); y < (int)(top - box.Bottom); y++)
        {
            for (int x = (int)(box.Left - left); x < (int)(box.Right - left); x++)
            {
                int i = ((y * width) + x) * 4;
                dark += pixels[i] + pixels[i + 1] + pixels[i + 2] < 300 ? 1 : 0;
            }
        }

        return dark;
    }

    [Fact]
    public void Final_and_working_copies_carry_the_values_and_reopening_restores_them()
    {
        byte[] source = File.ReadAllBytes(TestFiles.Sample("corpus/160F-2019.pdf"));
        using PdfiumDocument original = PdfiumDocument.Open(source);
        Core.Model.SourcePage[] pages = [.. Enumerable.Range(0, original.PageCount).Select(index => original.GetPageInfo(index).ToSourcePage())];
        Core.Model.DocumentState state = Core.Model.DocumentState.ForSourcePages(pages.Length).WithField("A.NOM", "Lee").WithField("R.CM", "Oui");

        Export.WrittenDocument final = Export.DocumentWriter.FinalCopy(source, pages, state);
        using (PdfiumDocument result = PdfiumDocument.Open(final.Pdf))
        {
            IReadOnlyList<FormField> fields = result.GetFormFields(0);
            Assert.Equal("Lee", fields.Single(field => field.Name == "A.NOM").Value);
            Assert.True(fields.First(field => field.Name == "R.CM").IsChecked);
        }

        Export.WrittenDocument working = Export.DocumentWriter.WorkingCopy(source, pages, state);
        Core.Session.SessionLookup lookup = Core.Session.SessionStore.Find(working.Pdf);
        Assert.Equal(state.Fields, lookup.Record!.Document.Fields);
        using (PdfiumDocument result = PdfiumDocument.Open(working.Pdf))
        {
            Assert.Equal("Lee", result.GetFormFields(0).Single(field => field.Name == "A.NOM").Value);
        }
    }

    [Fact]
    public void A_multi_select_list_keeps_every_chosen_option()
    {
        byte[] saved;
        using (PdfiumDocument pdf = Open("form-fields.pdf"))
        {
            FormField heard = pdf.GetFormFields(0).Single(field => field.Name == "Heard");
            Assert.True(heard.IsMultiSelect && heard.IsList);
            Assert.Equal(string.Empty, heard.Value);
            pdf.FillFormFields(new Dictionary<string, string> { ["Heard"] = PdfiumDocument.JoinChoices(["Friend", "Newspaper"]) });
            saved = pdf.SaveCopy(removeSecurity: true);
        }

        using PdfiumDocument reopened = PdfiumDocument.Open(saved);
        Assert.Equal("Friend\nNewspaper", reopened.GetFormFields(0).Single(field => field.Name == "Heard").Value);
    }

    [Fact]
    public void Signature_fields_buttons_limits_typed_answers_and_descriptions_are_read()
    {
        byte[] saved;
        using (PdfiumDocument pdf = Open("form-fields.pdf"))
        {
            IReadOnlyList<FormField> fields = pdf.GetFormFields(0);
            FormField signature = fields.Single(field => field.Name == "Signature");
            Assert.Equal(FormFieldKind.Signature, signature.Kind);
            Assert.Equal("Your signature", signature.DisplayName);
            Assert.Equal(FormFieldKind.PushButton, fields.Single(field => field.Name == "Reset").Kind);
            Assert.Equal(4, fields.Single(field => field.Name == "Postcode").MaxLength);
            Assert.Equal(0, fields.Single(field => field.Name == "Name").MaxLength);
            Assert.True(fields.Single(field => field.Name == "Title").IsEditable);
            Assert.False(fields.Single(field => field.Name == "State").IsEditable);
            Assert.Equal("Your email address", fields.Single(field => field.Name == "Email").DisplayName);

            pdf.FillFormFields(new Dictionary<string, string> { ["Title"] = "Prof", ["Postcode"] = "400012", ["Signature"] = "x", ["Reset"] = "x" });
            saved = pdf.SaveCopy(removeSecurity: true);
        }

        using PdfiumDocument reopened = PdfiumDocument.Open(saved);
        IReadOnlyList<FormField> back = reopened.GetFormFields(0);
        Assert.Equal("Prof", back.Single(field => field.Name == "Title").Value);
        Assert.Equal("4000", back.Single(field => field.Name == "Postcode").Value);
    }

    [Fact]
    public void Text_fields_can_be_emptied_and_a_page_with_no_form_has_no_fields()
    {
        byte[] saved;
        using (PdfiumDocument pdf = Open("corpus/form_two_pages.pdf"))
        {
            pdf.FillFormFields(new Dictionary<string, string> { ["Text3"] = "page two" });
            pdf.FillFormFields(new Dictionary<string, string> { ["Text3"] = string.Empty, ["Text1"] = "first" });
            saved = pdf.SaveCopy(removeSecurity: true);
        }

        using PdfiumDocument reopened = PdfiumDocument.Open(saved);
        Assert.Equal(string.Empty, reopened.GetFormFields(1).Single(field => field.Name == "Text3").Value);
        Assert.Equal("first", reopened.GetFormFields(0).Single(field => field.Name == "Text1").Value);

        using PdfiumDocument plain = Open("four-pages.pdf");
        Assert.Empty(plain.GetFormFields(0));
    }
}
