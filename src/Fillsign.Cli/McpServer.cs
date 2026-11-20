using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fillsign.Cli;

/// <summary>
/// `fillsign mcp` (FS-970): a Model Context Protocol server over standard input and output (JSON-RPC 2.0, one message
/// per line), so AI assistants can read and fill forms on this computer with the same operations as the command line.
/// Local only; paths must be absolute; no signing; nothing overwritten unless asked; owners' restrictions respected.
/// Only protocol messages go to standard output; anything else goes to standard error.
/// </summary>
public sealed class McpServer(TextReader input, TextWriter output, TextWriter log)
{
    private const string ProtocolVersion = "2025-06-18";

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? reply = await HandleAsync(line, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                await output.WriteLineAsync(reply.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>One message in, its reply out; notifications get no reply.</summary>
    internal async Task<JsonObject?> HandleAsync(string line, CancellationToken cancellationToken = default)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("Not an object.");
        }
        catch (JsonException)
        {
            return Error(null, -32700, "The message isn't valid JSON.");
        }

        JsonNode? id = request["id"]?.DeepClone();
        bool isNotification = !request.ContainsKey("id");
        try
        {
            string method = Text(request["method"]) ?? throw new McpError(-32600, "The message has no method name.");
            JsonNode? result = method switch
            {
                "initialize" => Initialize(),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools() },
                "tools/call" => await CallAsync(request["params"] as JsonObject, cancellationToken).ConfigureAwait(false),
                _ when isNotification => null,
                _ => throw new McpError(-32601, $"Unknown method {method}."),
            };
            return isNotification ? null : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpError ex)
        {
            return isNotification ? null : Error(id, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The last stop for anything unexpected in one message, so a single bad request can't end the session.
            await log.WriteLineAsync($"fillsign mcp: a message failed: {ex}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return isNotification ? null : Error(id, -32603, "Fillsign couldn’t handle that message.");
        }
    }

    /// <summary>A JSON string's text, or null for anything else: the client's messages are checked, not trusted.</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "fillsign", ["title"] = "Fillsign", ["version"] = Program.Version },
        ["instructions"] = "Reads and fills PDF form fields on this computer. Use list_form_fields first to learn the field names and their options. Paths must be absolute. Signing is never done here: the person signs in the Fillsign app.",
    };

    private static JsonArray Tools() => new(
        Tool(
            "document_info",
            "What a PDF is: pages, whether it's a Fillsign working copy, how many form fields can be filled, and whether its owner forbids changes.",
            Properties(("path", "string", "Absolute path of the PDF.")),
            ["path"],
            readOnly: true),
        Tool(
            "list_form_fields",
            "The PDF's form fields: name (use it to fill), label (what it asks), kind (text, checkbox, radio, choice, multichoice, signature), current value, options, and whether it can be filled.",
            Properties(("path", "string", "Absolute path of the PDF.")),
            ["path"],
            readOnly: true),
        Tool(
            "fill_form",
            "Fills form fields and saves the result to a new file: a final copy to send (fields locked unless lock_fields is false) or a Fillsign working copy that stays editable. Values are checked against each field first; nothing is written if any is wrong. Signature fields can't be filled here.",
            Properties(
                ("path", "string", "Absolute path of the PDF to fill."),
                ("values", "object", "Field name to value: text, true/false for check boxes, one of the options for radio groups and lists, or a list of options for multichoice fields."),
                ("output_path", "string", "Absolute path to save to."),
                ("lock_fields", "boolean", "Final copy only: make the fields read-only (default true)."),
                ("working_copy", "boolean", "Save a Fillsign working copy instead of a final copy (default false)."),
                ("overwrite", "boolean", "Replace output_path if it exists (default false).")),
            ["path", "values", "output_path"],
            readOnly: false),
        Tool(
            "export_final_copy",
            "Makes the final copy to send from a PDF or a Fillsign working copy, with its fields locked unless lock_fields is false.",
            Properties(
                ("path", "string", "Absolute path of the PDF or working copy."),
                ("output_path", "string", "Absolute path to save the final copy to."),
                ("lock_fields", "boolean", "Make the fields read-only (default true)."),
                ("overwrite", "boolean", "Replace output_path if it exists (default false).")),
            ["path", "output_path"],
            readOnly: false)
    );

    private async Task<JsonObject> CallAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        string name = Text(parameters?["name"]) ?? throw new McpError(-32602, "Which tool? Give its name.");
        JsonObject arguments = parameters?["arguments"] as JsonObject ?? [];
        try
        {
            string text = name switch
            {
                "document_info" => await InfoAsync(arguments, cancellationToken).ConfigureAwait(false),
                "list_form_fields" => await FieldsAsync(arguments, cancellationToken).ConfigureAwait(false),
                "fill_form" => await FillAsync(arguments, cancellationToken).ConfigureAwait(false),
                "export_final_copy" => await ExportAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new McpError(-32602, $"There's no tool called {name}."),
            };
            return ToolResult(text, isError: false);
        }
        catch (FormException ex)
        {
            // A problem with the request itself (a wrong value, a missing file) goes back to the assistant to fix.
            return ToolResult(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await log.WriteLineAsync($"fillsign mcp: {name} failed: {ex}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return ToolResult($"Fillsign couldn’t do that: {ex.Message}", isError: true);
        }
    }

    private static async Task<string> InfoAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        using FormFile form = await FormFile.OpenAsync(AbsolutePath(arguments, "path"), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(form.Info(), CliJson.Default.DocumentInfo);
    }

    private static async Task<string> FieldsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        using FormFile form = await FormFile.OpenAsync(AbsolutePath(arguments, "path"), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(form.Fields(), CliJson.Default.IReadOnlyListFieldInfo);
    }

    private static async Task<string> FillAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        string output = OutputPath(arguments);
        JsonObject values = arguments["values"] as JsonObject ?? throw new FormException("values should be an object of field names to values.");
        using FormFile form = await FormFile.OpenAsync(AbsolutePath(arguments, "path"), cancellationToken).ConfigureAwait(false);
        form.Fill(Program.FromJson(values));
        bool overwrite = Flag(arguments, "overwrite", false);
        if (Flag(arguments, "working_copy", false))
        {
            form.WriteWorkingCopy(output, overwrite);
            return $"Filled {values.Count} fields. Working copy saved to {output}. It opens in Fillsign with everything still editable.";
        }

        bool locked = Flag(arguments, "lock_fields", true);
        form.WriteFinalCopy(output, locked, overwrite);
        return $"Filled {values.Count} fields. Final copy saved to {output}{(locked ? ", with its fields locked" : string.Empty)}. Any signature still needs to be added in Fillsign.";
    }

    private static async Task<string> ExportAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        string output = OutputPath(arguments);
        using FormFile form = await FormFile.OpenAsync(AbsolutePath(arguments, "path"), cancellationToken).ConfigureAwait(false);
        bool locked = Flag(arguments, "lock_fields", true);
        form.WriteFinalCopy(output, locked, Flag(arguments, "overwrite", false));
        return $"Final copy saved to {output}{(locked ? ", with its fields locked" : string.Empty)}.";
    }

    /// <summary>
    /// Assistants run elsewhere than Fillsign, so a relative path could mean anything: only absolute ones are taken. And
    /// only on this computer: opening a file on a network share hands the person's sign-in to whoever runs it, and
    /// writing one sends the form off the computer, which an assistant steered by a document's text might ask for.
    /// </summary>
    private static string AbsolutePath(JsonObject arguments, string name)
    {
        string path = Text(arguments[name]) ?? throw new FormException($"{name} is needed, as text.");
        if (!Path.IsPathFullyQualified(path))
        {
            throw new FormException($"{name} must be an absolute path, not “{path}”.");
        }

        return IsOnThisComputer(path) ? path : throw new FormException($"{name} must be a file on this computer, not on a network share.");
    }

    /// <summary>Where to save: a .pdf, so a filled form can never replace some other kind of file.</summary>
    private static string OutputPath(JsonObject arguments)
    {
        string path = AbsolutePath(arguments, "output_path");
        return Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new FormException($"output_path must end in .pdf, not “{Path.GetFileName(path)}”.");
    }

    private static bool IsOnThisComputer(string path)
    {
        // \\server\share, \\?\ and \\.\ on Windows; //server/share elsewhere.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        // A mapped drive or a mounted share looks local; the drive it's on says otherwise.
        string full = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            DriveInfo? drive = DriveInfo.GetDrives().Where(drive => full.StartsWith(drive.Name, comparison)).MaxBy(drive => drive.Name.Length);
            return drive?.DriveType != DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Flag(JsonObject arguments, string name, bool fallback) =>
        arguments[name] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required, bool readOnly) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. required.Select(item => (JsonNode)item)]),
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = !readOnly, ["openWorldHint"] = false },
    };

    private static JsonObject Properties(params (string Name, string Type, string Description)[] properties)
    {
        var result = new JsonObject();
        foreach ((string name, string type, string description) in properties)
        {
            result[name] = new JsonObject { ["type"] = type, ["description"] = description };
        }

        return result;
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed class McpError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
