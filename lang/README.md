# Highway Roads translations

Highway Roads resolves every player-facing runtime text through MultiLangLib.
Each file in this directory is a UTF-8 JSON object whose keys are stable text
IDs. At runtime, for example, `tool.highway.name` becomes the canonical key
`multilanglib.GroundRoads.tool.highway.name`.

## Included locales

The directory contains catalogs for all 21 official Captain of Industry locale
filenames: `ca`, `cs`, `de`, `en`, `es`, `et`, `fr`, `hu`, `it`, `ja`, `ko`,
`nb_NO`, `nl`, `pl`, `pt_BR`, `ru`, `sv`, `tr`, `uk`, `zh_Hans`, and
`zh_Hant`. The additional neutral `pt` catalog is the fallback for Portuguese
regional locales.

## Adding or updating a language

1. Copy `en.json` to the desired Captain of Industry locale filename.
2. Translate values only; never rename or remove keys.
3. Preserve technical labels such as `T1/T2`, `T3`, `T4`, `G4/G8`, `E/Q`, and
   `Shift` where the target language normally uses them.
4. Keep exactly the same key set as `en.json`, do not leave values empty, and
   save the file as valid UTF-8 JSON.
5. Start the game with MultiLangLib and Highway Roads enabled. MultiLangLib's
   debug-language option shows canonical keys and makes missing UI text easy to
   identify.

MultiLangLib tries the selected regional catalog first and then its neutral
language before falling back to English. Existing prototype and toolbar text is
created while the game loads, so changing a catalog requires a game restart to
refresh every visible label.

## Deutsch

Highway Roads bezieht alle sichtbaren Laufzeittexte über MultiLangLib. Für eine
neue Übersetzung nur die Werte aus `en.json` übersetzen, alle Schlüssel und
technischen Bezeichnungen erhalten und die Datei als gültiges UTF-8-JSON unter
dem passenden COI-Sprachdateinamen speichern. Fehlende Übersetzungen fallen
standardmäßig auf Englisch zurück.
