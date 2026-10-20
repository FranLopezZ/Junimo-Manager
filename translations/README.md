# Translations / Traducciones

Junimo Manager's texts live in one JSON file per language in this folder. `en.json` is the reference.
Los textos de Junimo Manager están en un archivo JSON por idioma en esta carpeta. `en.json` es la referencia.

## How to translate / Cómo traducir

**English**

1. Copy `en.json` and rename it with your language code: `fr.json`, `pt.json`, `de.json`, `ru.json`, `zh.json`…
   (Or, inside the app: *Settings → Language → Create translation template*.)
2. Set `"_name"` to the name of your language written in that language (`"Français"`).
3. Replace the **right-hand side** of every line with your translation. Do **not** touch the left-hand side (the key), and keep
   placeholders such as `{0}` and `{1}` exactly as they are (you may move them inside the sentence).
4. Lines you leave out or leave in English are not a problem: missing ones fall back to English.
5. Save the file as **UTF-8**.

**Test it without compiling:** copy the file to the languages folder (*Settings → Language → Open languages folder*),
restart the app and pick your language in *Settings → Language*.

**Send it to us:** open a pull request adding `translations/<code>.json` (or attach the file to an issue).
It is embedded automatically in the next release, no code changes needed.

**Español**

1. Copia `en.json` y renómbralo con el código de tu idioma (`fr.json`, `pt.json`, `de.json`…), o crea la plantilla desde
   *Ajustes → Idioma → Crear plantilla de traducción*.
2. Pon en `"_name"` el nombre de tu idioma escrito en ese idioma.
3. Cambia **solo la parte derecha** de cada línea. La izquierda (la clave) no se toca, y los marcadores `{0}`, `{1}`… se
   conservan tal cual (puedes moverlos dentro de la frase).
4. Lo que no traduzcas se muestra en inglés.
5. Guarda el archivo en **UTF-8**.

**Probar sin compilar:** copia el archivo a la carpeta de idiomas (*Ajustes → Idioma → Abrir carpeta de idiomas*), reinicia y
elige tu idioma. **Enviarla:** abre un pull request con `translations/<código>.json` (o adjunta el archivo en una incidencia).

## Notes for maintainers / Notas para mantenedores

- The key is the Spanish text used in the code (`Loc.T("…")`, `Loc.F("… {0}", x)`) or written in the XAML.
- `LocalizationTests` fails if a text in the code has no English entry, or if a translation file has unknown keys or different
  `{n}` placeholders. Run `python scripts/dev/translation_status.py` to see how complete each language is.
- Adding a language needs no code: files in this folder are embedded by the build (`SdvMods.Core.csproj`).
