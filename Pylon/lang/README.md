# Translating Pylon

Pylon's texts are written in English; a language is a JSON file that maps each English text to its translation.
Russian is built in. Any other language is a file here named by its language code (`de.json`, `uk.json`, `pt-BR.json`).

1. Get the template with every text Pylon shows:

   ```
   Pylon.exe --export-strings de.json
   ```

2. Fill in `_name` with the language's own name (`"Deutsch"`), then translate the values. Leave a value empty to keep
   the English text. Keep `{0}`, `{1}` and keys like `Ctrl+O` as they are.
3. Plurals: `"{0} item"` / `"{0} items"`, plus `"{0} items (few)"` for languages that use a separate form for 2–4.
4. Try it: put the file in `%AppData%\Pylon\lang` (or `lang` next to `Pylon.exe`), pick the language in
   Settings → General → Interface language and let Pylon restart.
5. Send it as a pull request to this folder; it ships with the next release.
