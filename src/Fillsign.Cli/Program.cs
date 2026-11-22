using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fillsign.Cli;

/// <summary>
/// The `fillsign` command (FS-969): read and fill a PDF's form fields from scripts, and `fillsign mcp` for AI assistants
/// (FS-970). Exit codes: 0 done, 1 something couldn't be done (the message says why), 2 the command was mistyped.
/// </summary>
public static class Program
{

    private const string Usage = """
        fillsign: fill in PDF forms from scripts and AI assistants. Signing is done in the Fillsign app.

          fillsign info <file> [--json]
              Pages, whether it's a Fillsign working copy, how many fields can be filled.
          fillsign fields <file> [--json]
              Each field: its name, what it asks, its kind, value and options.
          fillsign fill <file> --out <file> [--set NAME=VALUE]... [--values <file.json>]
                        [--working-copy] [--unlocked] [--overwrite] [--edit-anyway]
              Fills fields and writes a final copy with its fields locked (--unlocked leaves them editable),
              or a working copy that opens editable in Fillsign. For a list that takes several answers, give
              --set NAME=VALUE once for each. --values reads {"Name": "Jordan", "Heard": ["Friend", "Search"]}.
          fillsign export <working-copy> --out <file> [--unlocked] [--overwrite]
              Makes the final copy to send from a Fillsign working copy.
          fillsign mcp
              Runs an MCP server on standard input and output, for AI assistants.

        Files whose owner forbids changes are left alone unless you add --edit-anyway.
        Nothing is overwritten unless you add --overwrite.
        """;

    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // The command speaks English whatever the computer's language: scripts and assistants read its output.
        System.Globalization.CultureInfo english = System.Globalization.CultureInfo.GetCultureInfo("en");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = english;
        System.Globalization.CultureInfo.CurrentUICulture = english;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Out.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        if (args[0] is "--version" or "version")
        {
            Console.Out.WriteLine($"fillsign {Version}");
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "info" => await InfoAsync(Arguments.Parse(args[1..])).ConfigureAwait(false),
                "fields" => await FieldsAsync(Arguments.Parse(args[1..])).ConfigureAwait(false),
                "fill" => await FillAsync(Arguments.Parse(args[1..])).ConfigureAwait(false),
                "export" => await ExportAsync(Arguments.Parse(args[1..])).ConfigureAwait(false),
                "mcp" => await new McpServer(Console.In, Console.Out, Console.Error).RunAsync().ConfigureAwait(false),
                _ => throw new UsageException($"There's no command “{args[0]}”."),
            };
        }
        catch (UsageException ex)
        {
            await Console.Error.WriteLineAsync($"{ex.Message} Run `fillsign --help` for the commands.").ConfigureAwait(false);
            return 2;
        }
        catch (FormException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A file that couldn't be read or written, or a document that couldn't be made: say so and exit 1, as the
            // MCP server does, rather than end with a stack trace.
            await Console.Error.WriteLineAsync($"Fillsign couldn’t do that: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    internal static string Version =>
        typeof(Program).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";

    private static async Task<int> InfoAsync(Arguments arguments)
    {
        using FormFile form = await FormFile.OpenAsync(arguments.File).ConfigureAwait(false);
        DocumentInfo info = form.Info();
        if (arguments.Has("--json"))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(info, CliJson.Default.DocumentInfo));
            return 0;
        }

        Console.Out.WriteLine(info.Path);
        Console.Out.WriteLine($"  {info.Pages} {(info.Pages == 1 ? "page" : "pages")}{(info.IsWorkingCopy ? ", a Fillsign working copy" : string.Empty)}");
        // The same count the app gives (UX review 9): signature fields are fields too, signed in the app.
        Console.Out.WriteLine(info.SignatureFields == 0
            ? $"  {info.FillableFields} fields to fill"
            : $"  {info.FillableFields + info.SignatureFields} fields: {info.FillableFields} to fill, {info.SignatureFields} to sign in the Fillsign app");
        if (info.OwnerForbidsChanges)
        {
            Console.Out.WriteLine("  Its owner has set it so it can't be changed.");
        }

        return 0;
    }

    private static async Task<int> FieldsAsync(Arguments arguments)
    {
        using FormFile form = await FormFile.OpenAsync(arguments.File).ConfigureAwait(false);
        IReadOnlyList<FieldInfo> fields = form.Fields();
        if (arguments.Has("--json"))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(fields, CliJson.Default.IReadOnlyListFieldInfo));
            return 0;
        }

        if (fields.Count == 0)
        {
            Console.Out.WriteLine("This document has no form fields.");
            return 0;
        }

        foreach (FieldInfo field in fields)
        {
            string value = field.Kind switch
            {
                "checkbox" => field.IsChecked == true ? "ticked" : "not ticked",
                "multichoice" => field.Chosen.IsEmpty ? "(none chosen)" : string.Join(", ", field.Chosen),
                _ => field.Value.Length > 0 ? field.Value : "(empty)",
            };
            string label = field.Label != field.Name ? $"  “{field.Label}”" : string.Empty;
            string notes = !field.CanFill ? (field.Kind == "signature" ? "  [sign in Fillsign]" : "  [read-only]") : string.Empty;
            Console.Out.WriteLine($"p{field.Page}  {field.Kind,-11} {field.Name}{label}: {value}{notes}");
            if (!field.Options.IsEmpty)
            {
                Console.Out.WriteLine($"      options: {string.Join(" | ", field.Options.Where(option => option.Length > 0))}");
            }
        }

        return 0;
    }

    private static async Task<int> FillAsync(Arguments arguments)
    {
        string output = arguments.Required("--out");
        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (arguments.Value("--values") is { } valuesFile)
        {
            foreach ((string name, IReadOnlyList<string> given) in ReadValues(valuesFile))
            {
                values[name] = given;
            }
        }

        foreach (string pair in arguments.All("--set"))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new UsageException($"--set needs NAME=VALUE, not “{pair}”.");
            }

            string name = pair[..equals];
            List<string> list = values.TryGetValue(name, out IReadOnlyList<string>? existing) ? [.. existing] : [];
            list.Add(pair[(equals + 1)..]);
            values[name] = list;
        }

        if (values.Count == 0)
        {
            throw new UsageException("Give the values to fill with --set NAME=VALUE or --values <file.json>.");
        }

        using FormFile form = await FormFile.OpenAsync(arguments.File).ConfigureAwait(false);
        form.Fill(values, editAnyway: arguments.Has("--edit-anyway"));
        bool overwrite = arguments.Has("--overwrite");
        if (arguments.Has("--working-copy"))
        {
            form.WriteWorkingCopy(output, overwrite);
            Console.Out.WriteLine($"Filled {values.Count} {(values.Count == 1 ? "field" : "fields")}. Working copy saved to {Path.GetFullPath(output)}");
        }
        else
        {
            form.WriteFinalCopy(output, lockFields: !arguments.Has("--unlocked"), overwrite);
            Console.Out.WriteLine($"Filled {values.Count} {(values.Count == 1 ? "field" : "fields")}. Final copy saved to {Path.GetFullPath(output)}");
        }

        return 0;
    }

    private static async Task<int> ExportAsync(Arguments arguments)
    {
        string output = arguments.Required("--out");
        using FormFile form = await FormFile.OpenAsync(arguments.File).ConfigureAwait(false);
        form.WriteFinalCopy(output, lockFields: !arguments.Has("--unlocked"), arguments.Has("--overwrite"));
        Console.Out.WriteLine($"Final copy saved to {Path.GetFullPath(output)}");
        return 0;
    }

    /// <summary>A JSON object of field names to a value (string, number, true/false) or a list of values.</summary>
    internal static Dictionary<string, IReadOnlyList<string>> ReadValues(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FormException($"There's no file at {full}.");
        }

        try
        {
            return FromJson(JsonNode.Parse(File.ReadAllText(full)) as JsonObject ?? throw new FormException($"{full} should hold a JSON object of field names to values."));
        }
        catch (JsonException ex)
        {
            throw new FormException($"{full} isn't valid JSON: {ex.Message}");
        }
    }

    internal static Dictionary<string, IReadOnlyList<string>> FromJson(JsonObject values)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach ((string name, JsonNode? node) in values)
        {
            result[name] = node switch
            {
                JsonArray list => [.. list.Select(item => item?.ToString() ?? string.Empty)],
                JsonValue one when one.TryGetValue(out bool flag) => [flag ? "yes" : "no"],
                null => [string.Empty],
                _ => [node.ToString()],
            };
        }

        return result;
    }
}

/// <summary>The JSON the command and MCP server write, generated at build time so it works under Native AOT (FS-809).</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(DocumentInfo))]
[System.Text.Json.Serialization.JsonSerializable(typeof(IReadOnlyList<FieldInfo>))]
internal sealed partial class CliJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>The command was mistyped.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>A command's file and options: flags (--json), and options with values (--out x, --set a=b, repeatable).</summary>
internal sealed class Arguments
{
    private static readonly HashSet<string> s_withValues = ["--out", "--set", "--values"];
    private readonly List<(string Name, string? Value)> _options = [];

    private Arguments(string file) => File = file;

    public string File { get; }

    public static Arguments Parse(string[] args)
    {
        string? file = null;
        var options = new List<(string, string?)>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                file = file is null ? arg : throw new UsageException($"One file at a time: “{file}” and “{arg}” were both given.");
                continue;
            }

            if (s_withValues.Contains(arg))
            {
                options.Add((arg, i + 1 < args.Length ? args[++i] : throw new UsageException($"{arg} needs a value.")));
            }
            else
            {
                options.Add((arg, null));
            }
        }

        var parsed = new Arguments(file ?? throw new UsageException("Which file? Give its path."));
        parsed._options.AddRange(options);
        return parsed;
    }

    public bool Has(string flag) => _options.Exists(option => option.Name == flag);

    public string? Value(string name) => _options.FindLast(option => option.Name == name).Value;

    public string Required(string name) => Value(name) ?? throw new UsageException($"{name} is needed.");

    public IEnumerable<string> All(string name) => _options.Where(option => option.Name == name && option.Value is not null).Select(option => option.Value!);
}
