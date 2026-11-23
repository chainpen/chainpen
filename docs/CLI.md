# Command line and MCP

Chainpen includes a `chainpen` command for scripts, and an MCP server so AI assistants can read and fill forms on
this computer. Both use the same code as the app: values are checked against each field, the PDF's own fields are
filled so they keep working, and the copies made are the same as the app's.

What they never do: sign (signing happens in the app, with your saved signatures), overwrite a file unless asked,
fill or copy a file whose owner forbids changes unless asked (`--edit-anyway`, command line only), or reach the
network. Every copy Chainpen writes is free of the owner's restriction, so `fill` and `export` both ask.

## Where it is

| Platform | Path |
|---|---|
| macOS | `/Applications/Chainpen.app/Contents/MacOS/chainpen` |
| Windows | `%LocalAppData%\Chainpen\current\chainpen.exe` |
| Linux (AppImage) | inside the AppImage; extract it, or build from source |

From source: `dotnet run --project src/Chainpen.Cli -- <command>`.

## Commands

```text
chainpen info <file> [--json]
chainpen fields <file> [--json]
chainpen fill <file> --out <file> [--set NAME=VALUE]... [--values <file.json>]
              [--working-copy] [--unlocked] [--overwrite] [--edit-anyway]
chainpen export <working-copy> --out <file> [--unlocked] [--overwrite] [--edit-anyway]
chainpen mcp
```

- `fields` lists each field once: its name (what to fill it by), what it asks, its kind (`text`, `checkbox`, `radio`,
  `choice`, `multichoice`, `signature`), its value and its options. Read-only fields and signature fields are marked.
- `fill` writes a **final copy** with its fields locked (read-only in other PDF apps); `--unlocked` leaves them editable.
  `--working-copy` writes a Chainpen working copy instead, which opens with everything still editable.
- Values: text as it is; check boxes `yes`/`no`; radio groups and lists one of their options (any case); a list that
  takes several answers, `--set Heard=Friend --set Heard=Search`. A drop-down that takes typing accepts any answer.
- `--values` reads a JSON object: `{"Name": "Jordan Lee", "ContactEmail": true, "Heard": ["Friend", "Search"]}`.
- Nothing is written if any value is wrong; every problem is listed.
- Exit codes: `0` done, `1` couldn't be done (the message says why), `2` the command was mistyped.

## MCP server

`chainpen mcp` speaks the Model Context Protocol (2025-06-18) over standard input and output. Add it to an MCP client,
for example Claude Desktop's `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "chainpen": {
      "command": "/Applications/Chainpen.app/Contents/MacOS/chainpen",
      "args": ["mcp"]
    }
  }
}
```

Tools (paths must be absolute and on this computer, not on a network share; `output_path` must end in `.pdf`):

| Tool | Does |
|---|---|
| `document_info` | Pages, whether it's a working copy, how many fields can be filled, whether its owner forbids changes |
| `list_form_fields` | The fields, as `chainpen fields --json` lists them |
| `fill_form` | `path`, `values`, `output_path`, optional `lock_fields` (default true), `working_copy`, `overwrite` |
| `export_final_copy` | `path`, `output_path`, optional `lock_fields`, `overwrite` |

A wrong value, a missing file or a relative path comes back as a tool error the assistant can correct. Files whose
owner forbids changes are never filled or exported through MCP.
