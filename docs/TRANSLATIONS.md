# Translations

Chainpen's interface is in English, German and Japanese. It follows the computer's language, or the one chosen in
**Settings → Language**, which rebuilds the open windows in it straight away (a toast or dialog already showing keeps
its wording until it next appears). The `chainpen` command and its MCP server stay in English.

| Language | Interface | Practice form | User guide | Status |
|---|---|---|---|---|
| English (`en`) | `Strings.resx` | `practice-form.pdf` | `USER-GUIDE.md` | Source |
| German (`de`) | `Strings.de.resx` | `practice-form.de.pdf` | not yet | Draft: needs a native speaker's review |
| Japanese (`ja`) | `Strings.ja.resx` | `practice-form.ja.pdf` | not yet | Draft: needs a native speaker's review |

The strings live in `src/Chainpen.App/Localization/` and `src/Chainpen.Pdf/Localization/`. The practice forms are made
by `build/make-practice-form.py`, and the practice guide quotes their wording (`Editor_Guide*` keys).

## Reviewing a translation

- Every key in `Strings.resx` needs a value in each translation. `TranslationTests` fails if one is missing, or if a
  translation changes a placeholder (`{0}`), a `Mod` shortcut token or a tool letter such as "(T)".
- `_One` and `_Other` keys are the singular and plural of one message. Japanese uses the same text for both.
- `Mod` becomes ⌘ on a Mac and Ctrl (German: Strg) elsewhere, so keep it as it is.
- The toolbar collapses to fit its labels, but shorter labels keep more of it visible. Check German at the smallest
  window (1024 × 720).

German glossary: Arbeitskopie (working copy), Endfassung (final copy), exportieren, Wiederherstellungskopie, Häkchen,
Kreuz, Unterschrift, Initialen, Formularfeld, Lücke (gap); "Sie" throughout.

Japanese glossary: 作業用コピー (working copy), 最終版 (final copy), 書き出す (export), 復元用コピー (recovery copy),
チェック / バツ, 署名, イニシャル, 入力欄 (form field), 未記入箇所 (gap); です・ます in sentences.

## What isn't translated

- Gap hints recognise labels by their words. English, German and Japanese label words are known; other languages still
  get boxes and underscore lines.
- Typed signatures use Latin-script handwriting styles. Names in other scripts (Japanese, Chinese) are drawn in an
  installed font that has every character, not in a handwriting style.
- Crash reports stay in English, as they go into a GitHub issue.
- Labels Chainpen makes for an open document's form fields ("Deceased, part 2") are made when it opens, so they keep
  the language they were made in until the document is opened again.
