# Locara — Maintainer & Developer Reference

Concise engineering reference for maintaining and extending **Locara** (post-`v1.1.0` baseline, November 2026).

---

## 1. Build, Test, CI & Publish Commands

- **SDK:** `.NET 10.0.404` pinned in [global.json](../global.json) (`TreatWarningsAsErrors=true`, `RestorePackagesWithLockFile=true`).
- **Build & Test (`75` xUnit tests):**
  ```powershell
  dotnet build -c Release -p:RestoreLockedMode=true
  dotnet test -c Release --no-build
  ```
- **Continuous Integration:** Automated privacy guard, locked NuGet restore, Release build, xUnit suite, and tagged archive packaging run in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml).
- **Windows x64 Release Package (`Locara-windows-x64.zip` + `.sha256`):**
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1
  ```
  *(Pass `-Dotnet 'path\to\dotnet.exe'` if using a side-by-side .NET 10 SDK installation.)*

---

## 2. Core Module Map (`src/Locara.Core/`)

| File | Responsibility |
|---|---|
| `Models.cs` | Core records, `Identity.SafePath`, `Identity.RequireNoLinks`, `ExtractionRule.FieldPatterns`, `LocaraPaths.UserDataDirectory` (`Locara` with `Locara2` fallback) |
| `ProjectStore.cs` | SQLite WAL store (Schema v5: `settings`, `sources`, `files`, `units`, `glossary`, `attempts`, `team_runs`, `team_batches`, `team_notes`, `usage_ledger`) |
| `ProjectService.cs` | Source/package resolution (`FindMainGodotPck`), lazy-skip secondary locale scanning & on-demand extraction, Layer 1 file/language filtering (`ExtractPathLocale`), schema cluster & AI Scout application, Gettext `.pot -> .po` delivery, loose Unity TTF patching, streaming builds, `ExportXUnityAutoTranslator` |
| `Engines.cs` | Engine detection (`godot`, `rpgmaker`, `renpy`, `unity`, `unreal`, `gamemaker`, `generic`), `IsEngineInternalMetadata`, RPG Maker event command extraction |
| `Containers.cs` | `FolderContainer`, `ZipContainer` (AES-256), `PckContainer` (Godot `PCK v2` & `PCK v3`, up to 16 GiB) |
| `Formats.cs` / `CatalogFormats.cs` / `StructuredTextFormats.cs` | Format registry + `JSON`, `CSV`/`TSV`, `XML`/`XLIFF`, `INI`, `YAML`, `TOML`, `.strings` (UTF-8/UTF-16 + comment context), `Lua` (lexer-safe table extractor), `PO`/`POT`, `XLSX`, `Ren’Py`, `StarTruckerCatalog` |
| `FieldSemantics.cs` | Key semantic classifier (`LikelyHumanText` vs `LikelyTechnical`) & locator pattern normalizer (`/*/items/*/description`) |
| `SchemaClusterer.cs` | Groups structurally identical files into `FileSchemaCluster` templates for rule reuse & AI Scout |
| `TextQualityClassifier.cs` | Layer 1 value + key classifier (`Translatable`, `Mixed`, `AuxiliaryData`, `SecondaryLanguage`, `Technical`) and `HumanOnlyFilter` |
| `LocalizationScout.cs` | Layer 2 optional token-budgeted AI schema analyzer with strict anti-hallucination validation & usage logging |
| `DeploymentAdvisor.cs` | Layer 3 engine-aware deployment intelligence (Gettext `.pot -> .po`, Unity hardcoded -> `XUnity`, UE5 IoStore vs `_P.pak`, GameMaker `data.win` vs external files) |
| `UnityI2Format.cs` | `UnityFsStream`, `UnitySerializedFile` (v17–v26), `UnityI2Format`, `UnityArabicDisplayOptions` (wrap, scale, native RTL toggle, `ContrastBoost`), generalized `UnityTmpArabicPatcher` (TMP fallback prepend, runtime OpenType `'kern'` feature neutralization, SDF `_FaceDilate` boost) |
| `UnityBundleFormat.cs` | `Lz4Block`, `UnityFsBundle` (128 KiB block invariant), `UnityBundleFormat`, `UnityAddressablesCatalog` (`catalog.bin`/`catalog.json` CRC sync) |
| `UnityAssetsFormat.cs` | Universal Unity `TextAsset` (`classId=49`) extractor/rewriter (`resources.assets`, `sharedassets*.assets`, `data.unity3d`) |
| `XUnityAutoTranslator.cs` | `XUnityTranslationFormat` (`.txt` read/write) + `XUnityAutoTranslatorExporter` (`BepInEx/` overlay) |
| `StarTruckerDisplay.cs` | Native Windows `icu.dll` Arabic shaping & UAX #9 BiDi, DP tag-safe `WrapArabicParagraph` (atomic `<nobr>`/`<link>`/`<a>` spans) |
| `ArabicFontCatalog.cs` | Bundled OFL 1.1 Arabic fonts, Windows system fonts, custom `.ttf`/`.otf` import, `ResolveBilingual` / `ResolveForUnreal` / `ResolveContrastVariant`, OFL license writer |
| `UnrealLocresFormat.cs` | Pure-C# UE4/UE5 `.locres` v0–v3 reader/writer (UTF-16LE for Arabic) |
| `UnrealStringTableFormat.cs` | Pure-C# UE4/UE5 `UStringTable` `.uasset` (standalone and split `.uasset` + `.uexp` pairs) reader/writer |
| `UnrealPakCodec.cs` | Pure-C# `.pak` v4–v12 reader, v8/v11/v12 drop-in `*_Locara_P.pak` writer, UE5 IoStore streaming `PatchExistingPak`, `.locres` + `StringTable` `.uasset`/`.uexp` + bilingual `.ufont`/`.ttf` patcher |
| `GodotTranslationFormat.cs` | Pure-C# Godot 3 & 4 `RSRC` and compressed `RSCC` (`FastLZ`, `Zlib/Deflate`, `Zstd`, `Gzip`) `.translation` (`OptimizedTranslation` / `PHashTranslation` with SMAZ + `Translation`) |
| `Translation.cs` / `TranslationTeam.cs` | Token masking/validation, API providers, `ArabicPromptPresets` (6 genre presets), parallel Supervisor/Translator/Reviewer team, transient network retry (`CanRetry`) |
| `PackageInstaller.cs` / `SteamGameTrial.cs` / `StarTruckerTrial.cs` | SHA-256 verified in-place install/restore (supports newly added drop-in files & companion `.uexp` files) + Steam auto-restore trials |

---

## 3. Critical Binary Invariants (Do Not Regress)

1. **Unity 6 / IL2CPP `data.unity3d` block size:** Every uncompressed LZ4 block except the final block MUST be exactly `128 KiB` (`131,072` bytes).
2. **Unity TMP OpenType `'kern'` neutralization:** Disable `m_enableKerning`, `m_GetFontFeaturesAtRuntime`, and neutralize the `'kern'` (`nrek`) active font feature inside `TMP Settings` (`UnityTmpArabicPatcher.TryPatchTmpSettings` in `UnityI2Format.cs`) so RTL kerning pairs never shift visually-reordered LTR Arabic glyphs.
3. **UE5 IoStore (`.pak` + `.utoc`):** Never rely on standalone `_P.pak` when a companion `.utoc` exists; use `UnrealPakCodec.PatchExistingPak` to stream-patch the base `.pak`.
4. **Unreal `.ufont` & `StringTable` (`.uasset` + `.uexp`):** Auto-detect `int32(len) + TTF + 4B` wrapper on UE5 `.ufont` entries vs UE4 raw TTF (`ArabicFontCatalog.ResolveForUnreal`). When rewriting split `UStringTable` `.uasset` + `.uexp` pairs, update `FObjectExport.SerialSize` in `.uasset` to match the exact `.uexp` payload length (`uexp.Length - 4`) before the `0x9E2A83C1` footer tag.
5. **Godot `PCK v3` & `OptimizedTranslation`:** Preserve `dirBase` at end-of-file, `PACK_REL_FILEBASE`, bare vs `res://` path convention, and `hash_table` / `bucket.size` / `bucket.func` / `elem.key` untouched (`comp_size == uncomp_size`).
6. **Arabic TMP Wrapping:** `<nobr>`, `<link>`, and `<a>` must remain non-breakable atomic spans across wrapped lines.
7. **Zero game-specific hacks in new code:** Steam titles are used strictly for QA verification; all new engine pipelines must remain 100% general-purpose, and tracked files must never contain local paths, usernames, or credentials.
