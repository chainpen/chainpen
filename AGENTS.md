# Notes for coding agents

Fillsign is a desktop app for filling in and signing PDFs and scanned paper forms, in C# on .NET 11 with Avalonia,
using PDFium to read and write PDFs. The [README](README.md) covers what it is, the layout and the licence; this file
covers how to work on it.

## Build and test

Needs the .NET 11 SDK (`global.json`). From the repository root:

```bash
dotnet build
dotnet test
dotnet run --project src/Fillsign.App
```

- On macOS, run the app with `build/run-mac.sh [file ...]` rather than `dotnet run`: it builds and opens it as
  `artifacts/Fillsign Dev.app`. Run as a bare program, macOS won't let anything be chosen in the Open panel (Open…,
  Add pages…) and computer-use tools can't reach the window.
- Tests use xUnit v3 on Microsoft.Testing.Platform. Run one project, class or test with
  `dotnet test --project tests/Fillsign.Pdf.Tests --filter-class Fillsign.Pdf.Tests.PdfCompactorTests` or
  `--filter-method "*Some_test_name*"`.
- `build/fetch-corpus.sh` downloads real-world PDFs from the pdf.js test suite into `samples/corpus/` (not committed).
  Corpus tests skip without it; CI always fetches it, so run it before changing anything in `Fillsign.Pdf`.
- `Fillsign.App.Tests` drive the real app headlessly with Avalonia.Headless: real windows, pointer and key input, and
  rendered frames. Screenshots they save land in `artifacts/screenshots/` (ignored).
- Golden-file tests compare exported pages with the images in `tests/Fillsign.Pdf.Tests/Golden`. After a deliberate
  change, run them with `FILLSIGN_UPDATE_GOLDEN=1`, look at every changed image, and commit the ones that are right.
- Keychain tests only run on macOS with `FILLSIGN_KEYCHAIN_TESTS=1`.
- The README's screenshots in `docs/images/` are made by `ReadmeScreenshots` in `Fillsign.App.Tests` at 1440×900.
  After a change people see, run it with `FILLSIGN_README_SCREENSHOTS=1`, look at every image, and commit the right ones.

## Rules the build enforces

- Warnings are errors, with the `latest-recommended` analysers, and nullable reference types are on.
- Every project is AOT-compatible (`IsAotCompatible`), so the AOT analysers fail the build on reflection they can't
  follow. Serialise JSON through the source-generated `JsonSerializerContext` classes, not reflection.
- Package versions live in `Directory.Packages.props` (central package management); don't put versions in project files.

## How the code is arranged

- `Fillsign.Core`: the model, geometry, undo and redo, and the working-copy record. No UI and no PDF library.
- `Fillsign.Pdf`: everything that touches PDFium. PDFium is the only thing that writes PDFs.
- `Fillsign.App`: the Avalonia app. Behaviour belongs in view models where it can; code-behind handles focus, dialogs,
  input and platform details.
- `Fillsign.Cli`: the `fillsign` command and its MCP server, sharing `Core` and `Pdf` with the app.

Keep to those boundaries: `Core` must not reference Avalonia or PDFium.

## Conventions

- Follow `.editorconfig` and the code around you. Comments say why, not what.
- Text people read (UI, docs, messages) is plain, specific English, British spelling ("colour", "organise",
  "licence" as a noun), sentence case, and names the action ("Export final copy", not "OK").
- Accessibility is part of done: every control works by keyboard, shows a visible focus ring, and has an accessible
  name. Add or update a headless test for keyboard and focus behaviour you change.
- Nothing leaves the person's computer. The only network request is the update check against GitHub Releases. Don't add
  telemetry, analytics or other calls out.
- The working-copy format is a contract with files people already have. If you change the record, keep older files
  opening, and update [docs/FILE-FORMAT.md](docs/FILE-FORMAT.md) in the same change.
- When behaviour people see changes, update [docs/USER-GUIDE.md](docs/USER-GUIDE.md) too.
- Text people see goes in `Localization/Strings.resx` (app or `Fillsign.Pdf`), never as a literal: `Strings.Key` in C#,
  `{x:Static l:Strings.Key}` in AXAML. Whole sentences with `{0}` placeholders via `Text.Format`, and `Text.Plural` for
  counts; never glue a sentence from pieces. Add the German and Japanese values too (`Strings.de.resx`,
  `Strings.ja.resx`), marked for review in [docs/TRANSLATIONS.md](docs/TRANSLATIONS.md); `TranslationTests` checks they
  match the English.

## Commits

- Outside contributions aren't accepted yet ([CONTRIBUTING](CONTRIBUTING.md)).
- Never commit `artifacts/`, `samples/corpus/`, or local tool folders such as `.claude/`.
