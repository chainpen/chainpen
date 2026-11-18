# Fillsign

Fill in and sign documents, especially scanned paper forms with no fillable fields. A native desktop app for Windows, macOS and Linux, built with .NET 11 and Avalonia.

> Status: pre-alpha (Phase 0 spikes). The repository is private until the public beta. Outside contributions aren't accepted yet.

## Docs

- [Requirements](docs/REQUIREMENTS.md): what Fillsign must do
- [Implementation plan](docs/PLAN.md): stack, architecture, phases
- [Kanban board](docs/KANBAN.md): current work

## Build

Requires the .NET 11 SDK (`global.json` pins `11.0.100`, rolling forward to later feature bands).

```bash
dotnet build
dotnet test
dotnet run --project src/Fillsign.App
```

## Layout

| Path | Contents |
|---|---|
| `src/Fillsign.Core` | Model, geometry, undo/redo, editable-save format. No UI or PDF dependencies. |
| `src/Fillsign.Pdf` | PDFium binding: rendering and writing (export), HarfBuzz font subsetting. |
| `src/Fillsign.App` | Avalonia desktop app. |
| `tests/` | xUnit v3 tests for each project. |
| `spikes/` | Phase 0 go/no-go experiments (not shipped). |

## Licence

[Business Source License 1.1](LICENSE) (decision FS-002). In plain words:

- **Free for personal use**, always.
- **Free for everyone during the beta**, businesses included: every version below 1.0.0, until 90 days after 1.0.0 is released.
- **After the beta, businesses and other organisations need a paid licence** to use Fillsign for their work.

The source is public to read, build and change for non-production use. [LICENSE](LICENSE) is what counts; [NOTICE](NOTICE)
summarises it. Libraries and fonts Fillsign ships, and their licences, are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
