using System.Text.Json.Nodes;
using Fillsign.Pdf.Pdfium;
using Fillsign.Pdf.Tests;

namespace Fillsign.Cli.Tests;

/// <summary>FS-969, FS-970: filling forms from scripts and assistants.</summary>
public sealed class FormFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("fillsign-cli-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Dictionary<string, IReadOnlyList<string>> Values(params (string Name, string[] Values)[] values) =>
        values.ToDictionary(value => value.Name, value => (IReadOnlyList<string>)value.Values, StringComparer.Ordinal);

    [Fact]
    public async Task Fields_are_listed_once_each_with_labels_kinds_and_options()
    {
        using FormFile form = await FormFile.OpenAsync(TestFiles.Sample("form-fields.pdf"), TestContext.Current.CancellationToken);
        IReadOnlyList<FieldInfo> fields = form.Fields();
        Assert.Equal("Full name", fields.Single(field => field.Name == "Name").Label);
        Assert.Equal(["Individual", "Family", "Business"], fields.Single(field => field.Name == "Membership").Options);
        Assert.Equal("multichoice", fields.Single(field => field.Name == "Heard").Kind);
        Assert.False(fields.Single(field => field.Name == "Total").CanFill);
        Assert.False(fields.Single(field => field.Name == "Signature").CanFill);
        Assert.DoesNotContain(fields, field => field.Name == "Reset");
        Assert.Equal(13, form.Info().FillableFields);
        Assert.Equal(1, form.Info().SignatureFields);
    }

    [Fact]
    public async Task Wrong_values_are_all_explained_and_nothing_changes()
    {
        using FormFile form = await FormFile.OpenAsync(TestFiles.Sample("form-fields.pdf"), TestContext.Current.CancellationToken);
        var problem = Assert.Throws<FormException>(() => form.Fill(Values(
            ("Name", ["Fine"]), ("Postcode", ["400012"]), ("Membership", ["Gold"]), ("Total", ["1"]), ("Signature", ["x"]), ("Nope", ["1"]), ("ContactEmail", ["maybe"]))));
        Assert.Contains("up to 4 characters", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Individual, Family, Business", problem.Message, StringComparison.Ordinal);
        Assert.Contains("fills it in itself", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Sign it in Fillsign", problem.Message, StringComparison.Ordinal);
        Assert.Contains("no field called “Nope”", problem.Message, StringComparison.Ordinal);
        Assert.Contains("yes or no", problem.Message, StringComparison.Ordinal);
        Assert.Empty(form.State.Fields);
    }

    [Fact]
    public async Task A_final_copy_is_filled_and_locked_and_nothing_is_overwritten_unless_asked()
    {
        string output = Path.Combine(_folder, "final.pdf");
        using (FormFile form = await FormFile.OpenAsync(TestFiles.Sample("form-fields.pdf"), TestContext.Current.CancellationToken))
        {
            form.Fill(Values(("Name", ["Jordan Lee"]), ("ContactEmail", ["yes"]), ("Membership", ["family"]), ("Heard", ["Search", "Friend"]), ("Title", ["Prof"])));
            form.WriteFinalCopy(output, lockFields: true, overwrite: false);
            Assert.Throws<FormException>(() => form.WriteFinalCopy(output, lockFields: true, overwrite: false));
        }

        using PdfiumDocument final = PdfiumDocument.Open(File.ReadAllBytes(output));
        IReadOnlyList<FormField> fields = final.GetFormFields(0);
        Assert.Equal("Jordan Lee", fields.Single(field => field.Name == "Name").Value);
        Assert.True(fields.Single(field => field.Name == "Membership" && field.OnValue == "Family").IsChecked);
        Assert.Equal("Friend\nSearch", fields.Single(field => field.Name == "Heard").Value);
        Assert.Equal("Prof", fields.Single(field => field.Name == "Title").Value);
        Assert.All(fields, field => Assert.True(field.IsReadOnly, field.Name));
    }

    [Fact]
    public async Task A_working_copy_opens_again_with_its_values_and_exports_to_a_final_copy()
    {
        string working = Path.Combine(_folder, "working.pdf");
        using (FormFile form = await FormFile.OpenAsync(TestFiles.Sample("form-fields.pdf"), TestContext.Current.CancellationToken))
        {
            form.Fill(Values(("Name", ["Ada"])));
            form.WriteWorkingCopy(working, overwrite: false);
        }

        using FormFile reopened = await FormFile.OpenAsync(working, TestContext.Current.CancellationToken);
        Assert.True(reopened.IsWorkingCopy);
        Assert.Equal("Ada", reopened.Fields().Single(field => field.Name == "Name").Value);
        string final = Path.Combine(_folder, "final.pdf");
        reopened.WriteFinalCopy(final, lockFields: false, overwrite: false);
        using PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(final));
        FormField name = pdf.GetFormFields(0).Single(field => field.Name == "Name");
        Assert.Equal("Ada", name.Value);
        Assert.False(name.IsReadOnly);
    }

    [Fact]
    public async Task Files_whose_owner_forbids_changes_are_left_alone_unless_told_otherwise()
    {
        string path = TestFiles.Sample("corpus/empty_protected.pdf");
        using FormFile form = await FormFile.OpenAsync(path, TestContext.Current.CancellationToken);
        Assert.True(form.Info().OwnerForbidsChanges);
        Assert.Throws<FormException>(() => form.Fill(Values()));
        form.Fill(Values(), editAnyway: true);
    }

    [Fact]
    public async Task Missing_files_and_non_pdfs_are_explained()
    {
        await Assert.ThrowsAsync<FormException>(() => FormFile.OpenAsync(Path.Combine(_folder, "nowhere.pdf"), TestContext.Current.CancellationToken));
        string notes = Path.Combine(_folder, "notes.pdf");
        File.WriteAllText(notes, "not a pdf");
        await Assert.ThrowsAsync<FormException>(() => FormFile.OpenAsync(notes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_command_line_fills_from_set_and_a_values_file_and_says_what_went_wrong()
    {
        string values = Path.Combine(_folder, "values.json");
        File.WriteAllText(values, """{ "Email": "ada@example.com", "ContactPhone": true, "Heard": ["Friend", "Newspaper"] }""");
        string output = Path.Combine(_folder, "out.pdf");
        Assert.Equal(0, await Program.Main(["fill", TestFiles.Sample("form-fields.pdf"), "--out", output, "--values", values, "--set", "Name=Ada"]));
        using (FormFile filled = await FormFile.OpenAsync(output, TestContext.Current.CancellationToken))
        {
            Assert.Equal("Ada", filled.Fields().Single(field => field.Name == "Name").Value);
            Assert.True(filled.Fields().Single(field => field.Name == "ContactPhone").IsChecked);
        }

        Assert.Equal(1, await Program.Main(["fill", TestFiles.Sample("form-fields.pdf"), "--out", output, "--set", "Name=Again"]));
        Assert.Equal(2, await Program.Main(["fill", TestFiles.Sample("form-fields.pdf"), "--set", "Name=x"]));
        Assert.Equal(2, await Program.Main(["frobnicate"]));
    }

    [Fact]
    public async Task A_malformed_mcp_message_gets_an_error_and_the_server_keeps_going()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var server = new McpServer(TextReader.Null, TextWriter.Null, TextWriter.Null);

        JsonObject? badMethod = await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":5}""", token);
        Assert.Equal(-32600, badMethod!["error"]!["code"]!.GetValue<int>());

        JsonObject? badName = await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":["fill_form"]}}""", token);
        Assert.Equal(-32602, badName!["error"]!["code"]!.GetValue<int>());

        JsonObject? ping = await server.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", token);
        Assert.NotNull(ping!["result"]);
    }

    [Fact]
    public async Task The_mcp_server_answers_the_handshake_lists_its_tools_and_fills_forms()
    {
        var server = new McpServer(TextReader.Null, TextWriter.Null, TextWriter.Null);
        JsonObject? hello = await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""", TestContext.Current.CancellationToken);
        Assert.Equal("fillsign", hello!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Null(await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", TestContext.Current.CancellationToken));

        JsonObject? tools = await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", TestContext.Current.CancellationToken);
        Assert.Equal(["document_info", "list_form_fields", "fill_form", "export_final_copy"],
            tools!["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()));

        string output = Path.Combine(_folder, "mcp.pdf");
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "fill_form",
                ["arguments"] = new JsonObject
                {
                    ["path"] = TestFiles.Sample("form-fields.pdf"),
                    ["values"] = new JsonObject { ["Name"] = "Ada", ["ContactPost"] = true },
                    ["output_path"] = output,
                },
            },
        };
        JsonObject? filled = await server.HandleAsync(call.ToJsonString(), TestContext.Current.CancellationToken);
        Assert.False(filled!["result"]!["isError"]!.GetValue<bool>());
        Assert.True(File.Exists(output));

        // Problems go back to the assistant as tool errors it can act on.
        call["params"]!["arguments"]!["output_path"] = "relative.pdf";
        JsonObject? refused = await server.HandleAsync(call.ToJsonString(), TestContext.Current.CancellationToken);
        Assert.True(refused!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("absolute path", refused["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        JsonObject? unknown = await server.HandleAsync("""{"jsonrpc":"2.0","id":9,"method":"nope"}""", TestContext.Current.CancellationToken);
        Assert.Equal(-32601, unknown!["error"]!["code"]!.GetValue<int>());
        JsonObject? garbled = await server.HandleAsync("{not json", TestContext.Current.CancellationToken);
        Assert.Equal(-32700, garbled!["error"]!["code"]!.GetValue<int>());
    }
}
