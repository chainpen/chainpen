# Fillsign

Fill in and sign documents, especially scanned paper forms with no fillable fields. A native desktop app for Windows, macOS and Linux, built with .NET 11 and Avalonia.

> Status: beta. Bug reports and ideas are welcome in Issues; outside code contributions aren't accepted yet ([CONTRIBUTING](CONTRIBUTING.md)).

## Docs

- [User guide](docs/USER-GUIDE.md) and [installing](docs/INSTALL.md) (including first-run warnings on unsigned builds)
- [Working-copy file format](docs/FILE-FORMAT.md)

## Build

Requires the .NET 11 SDK (`global.json` pins `11.0.100`, rolling forward to later feature bands).

```bash
dotnet build
dotnet test
dotnet run --project src/Fillsign.App
```

## Release builds

`build/package.sh <rid> <version>` publishes a self-contained build and packs it with Velopack into `artifacts/releases/<rid>/` (installer, portable copy and update feed). Run it on the matching OS. Pushing a `v*.*.*` tag (or running `.github/workflows/release.yml` by hand) tests and builds all six platforms and makes a **draft** GitHub release. Review it on the Releases page and choose Publish release; installed copies only update from published releases. Pushes to `main` and pull requests into it are tested on all five platforms. The app icon is drawn by `build/icon/make-icon.py`.

## Layout

| Path | Contents |
|---|---|
| `src/Fillsign.Core` | Model, geometry, undo/redo, editable-save format. No UI or PDF dependencies. |
| `src/Fillsign.Pdf` | PDFium binding: rendering and writing (export), HarfBuzz font subsetting. |
| `src/Fillsign.App` | Avalonia desktop app. |
| `tests/` | xUnit v3 tests for each project. |
| `docs/` | User guide, install guide, command line and working-copy file format. |
| `build/` | Packaging script, macOS Info.plist, app icon, corpus download. |

## Command line and MCP

The `fillsign` command fills a PDF's form fields from scripts, and `fillsign mcp` lets AI assistants do the same over
the Model Context Protocol. Neither signs: that's done in the app. See [docs/CLI.md](docs/CLI.md).

## Licence

[Business Source License 1.1](LICENSE). In plain words:

- **Free for personal use**, always.
- **Free for everyone during the beta**, businesses included: every version below 1.0.0, until 90 days after 1.0.0 is released.
- **After the beta, businesses and other organisations need a paid licence** to use Fillsign for their work.

The source is public to read, build and change for non-production use. [LICENSE](LICENSE) is what counts; [NOTICE](NOTICE)
summarises it. Libraries and fonts Fillsign ships, and their licences, are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
