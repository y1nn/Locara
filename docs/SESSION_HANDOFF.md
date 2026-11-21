# Locara — Maintainer & Developer Reference

Concise engineering reference for maintaining and extending **Locara** (public release baseline, November 2026).

---

## 1. Build, Test & Publish Commands

- **SDK:** `.NET 10.0.404` pinned in [global.json](../global.json) (`TreatWarningsAsErrors=true`, `RestorePackagesWithLockFile=true`).
- **Build & Test:**
  ```powershell
  dotnet build -c Release -p:RestoreLockedMode=true
  dotnet test -c Release --no-build
  ```
- **Windows x64 Release Package (`Locara-windows-x64.zip` + `.sha256`):**
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1
  ```
  *(Pass `-Dotnet 'path\to\dotnet.exe'` if using a side-by-side .NET 10 SDK installation.)*

---

## 2. Core Module Map (`src/Locara.Core/`)

| File | Responsibility |
|---|---|
| `Models.cs` | Core records, `Identity.SafePath`, `Identity.RequireNoLinks`, `LocaraPaths.UserDataDirectory` (`Locara` with `Locara2` fallback) |
| `ProjectStore.cs` | SQLite WAL store (Schema v5: `settings`, `sources`, `files`, `units`, `glossary`, `attempts`, `team_runs`, `team_batches`, `team_notes`, `usage_ledger`) |
| `ProjectService.cs` | Source/package resolution (`FindMainGodotPck`), scanning, Layer 1 file/language filtering (`ExtractPathLocale`), preview fingerprinting, streaming builds, `ExportXUnityAutoTranslator` |
| `Engines.cs` | Engine detection (`godot`, `rpgmaker`, `renpy`, `unity`, `unreal`, `generic`), `IsEngineInternalMetadata`, RPG Maker event command extraction |
| `Containers.cs` | `FolderContainer`, `ZipContainer` (AES-256), `PckContainer` (Godot `PCK v2` & `PCK v3`, up to 16 GiB) |
| `Formats.cs` / `CatalogFormats.cs` | Format registry + `JSON`, `CSV`/`TSV`, `XML`/`XLIFF`, `INI`, `YAML`, `PO`, `XLSX`, `Ren’Py`, `StarTruckerCatalog` |
| `UnityI2Format.cs` | `UnityFsStream`, `UnitySerializedFile` (v17–v26), `UnityI2Format`, `UnityArabicDisplayOptions`, generalized `UnityTmpArabicPatcher` |
| `UnityBundleFormat.cs` | `Lz4Block`, `UnityFsBundle` (128 KiB block invariant), `UnityBundleFormat`, `UnityAddressablesCatalog` (`catalog.bin`/`catalog.json` CRC sync) |
| `UnityAssetsFormat.cs` | Universal Unity `TextAsset` (`classId=49`) extractor/rewriter (`resources.assets`, `sharedassets*.assets`, `data.unity3d`) |
| `XUnityAutoTranslator.cs` | `XUnityTranslationFormat` (`.txt` read/write) + `XUnityAutoTranslatorExporter` (`BepInEx/` overlay) |
| `StarTruckerDisplay.cs` | Native Windows `icu.dll` Arabic shaping & UAX #9 BiDi, DP tag-safe `WrapArabicParagraph`, TrueType GPOS `'kern'` stripping |
| `ArabicFontCatalog.cs` | Bundled OFL 1.1 Arabic fonts, Windows system fonts, custom `.ttf`/`.otf` import, `SupportsLatinAndArabic` / `ResolveForUnreal`, OFL license writer |
| `UnrealLocresFormat.cs` | Pure-C# UE4/UE5 `.locres` v0–v3 reader/writer (UTF-16LE for Arabic) |
| `UnrealPakCodec.cs` | Pure-C# `.pak` v4–v12 reader, v8/v11/v12 drop-in `*_Locara_P.pak` writer, UE5 IoStore streaming `PatchExistingPak`, bilingual `.ufont`/`.ttf` injector |
| `GodotTranslationFormat.cs` | Pure-C# Godot 3 & 4 `RSRC` `.translation` (`OptimizedTranslation` / `PHashTranslation` with SMAZ + `Translation`) |
| `TextQualityClassifier.cs` | Layer 1 human vs technical string/file classifier (includes Arabic Presentation Forms `U+FB50–U+FEFF`) |
| `Translation.cs` / `TranslationTeam.cs` | Token masking/validation, API providers, parallel Supervisor/Translator/Reviewer team, transient network retry (`CanRetry`) |
| `PackageInstaller.cs` / `SteamGameTrial.cs` / `StarTruckerTrial.cs` | SHA-256 verified in-place install/restore (supports newly added drop-in files) + Steam auto-restore trials |

---

## 3. Critical Binary Invariants (Do Not Regress)

1. **Unity 6 / IL2CPP `data.unity3d` block size:** Every uncompressed LZ4 block except the final block MUST be exactly `128 KiB` (`131,072` bytes).
2. **Unity FreeType GPOS `'kern'`:** Strip/zero GPOS `'kern'` table on replaced TMP fallback fonts to avoid kerning crashes/artifacts in Unity's FreeType loader.
3. **UE5 IoStore (`.pak` + `.utoc`):** Never rely on standalone `_P.pak` when a companion `.utoc` exists; use `UnrealPakCodec.PatchExistingPak` to stream-patch the base `.pak`.
4. **UE5 `.ufont` envelope vs UE4 raw `.ufont`:** Auto-detect `int32(len) + TTF + 4B` wrapper on UE5 `.ufont` entries and inject a bilingual Latin+Arabic font (`ArabicFontCatalog.ResolveForUnreal`).
5. **Godot `PCK v3`:** Preserve `dirBase` at end-of-file, `PACK_REL_FILEBASE`, and bare vs `res://` path convention.
6. **Godot `OptimizedTranslation`:** Preserve `hash_table`, `bucket.size`, `bucket.func`, and `elem.key` untouched; write translated UTF-8 with `comp_size == uncomp_size`.
7. **Zero game-specific hacks in production:** Steam titles are used strictly for QA verification; all engine pipelines must remain general-purpose.
