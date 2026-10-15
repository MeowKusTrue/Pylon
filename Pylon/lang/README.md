# Translating Pylon

Pylon's texts are written in English. Each language is a JSON file here, named by its code (`ru.json`, `de.json`,
`pt-BR.json`), mapping every English text to its translation. `en.json` is the source: every text Pylon shows.

## On Weblate (the easy way)

Translate at <https://hosted.weblate.org/engage/pylon/> in the browser. Weblate keeps these files up to date and sends
the changes as pull requests; they ship with the next release.

## By hand

1. Copy `en.json` to `<code>.json` (or run `Pylon.exe --export-strings <code>.json` for an empty template).
2. Set `_name` to the language's own name (`"Deutsch"`) and translate the values. An empty value keeps the English text.
   Keep `{0}`, `{1}` and keys like `Ctrl+O` as they are.
3. Plurals: `"{0} item"` / `"{0} items"`, plus `"{0} items (few)"` for languages with a separate form for 2–4.
4. Try it: put the file in `%AppData%\Pylon\lang`, pick the language in Settings → General → Interface language and let
   Pylon restart.
5. Send it as a pull request to this folder.

## For maintainers

- A new text in the code goes into `en.json` (the text itself as key and value) and `ru.json`; the checks in
  `tests/Checks` fail when a language file's keys differ from `en.json` or a translation loses a `{0}` placeholder.
- Weblate component: file mask `Pylon/lang/*.json`, monolingual base `Pylon/lang/en.json`, file format "JSON file",
  new translations from `en.json` with empty values. License: GPL-3.0 (the project's).
