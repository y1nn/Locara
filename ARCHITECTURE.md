# Locara Architecture

**Locara** is a local-first PC-game localization workbench targeting **.NET 10** (`net10.0` core/CLI/tests, `net10.0-windows` WPF desktop) with zero cloud backend dependencies. Every project is a self-contained directory centered on a SQLite WAL database (`project.db`, Schema v5).

---

## 1. Solution Structure

| Project | Target | Responsibility |
|---|---|---|
| `src/Locara.Core` | `net10.0` | Engine detection, container codecs, binary & text format handlers, Arabic shaping/wrap/font patching, 3-layer smart discovery & deployment advisor, SQLite project store, AI translation team & prompt presets, usage ledger, verified installer & Steam trial manager |
| `src/Locara.Desktop` | `net10.0-windows` | Arabic RTL WPF workbench (`MainWindow` split across feature partials: Overview, Games, Files, Translation, Team, Usage, Fonts, Install) + `ScoutPlanWindow` + Windows Credential Manager integration |
| `src/Locara.Cli` | `net10.0` | Scriptable headless CLI exposing project scan, file/cluster discovery, AI Scout, deployment advice, translation, build, XUnity export, install, and restore |
| `checks/Locara.Checks` | `net10.0` | Automated xUnit test suite (`75` tests covering codecs, formats, engines, lazy-skip archive scanning, Arabic shaping/wrap/TMP contrast, SQLite migrations, smart discovery & AI Scout, AI team concurrency/presets, and install/restore safety) |

Build & CI invariants enforced in `Directory.Build.props`, `global.json`, and `.github/workflows/ci.yml`:
- .NET SDK pinned to `10.0.404` (`rollForward: latestPatch`).
- `TreatWarningsAsErrors=true`, `Nullable=enable`, `RestorePackagesWithLockFile=true`.
- Automated GitHub Actions CI runs privacy/leak checks, locked NuGet restore, Release build, and all `75` xUnit tests on every push and pull request.

---

## 2. Project Workspace & SQLite Schema (v5)

Each project directory stores:
- `project.db` (SQLite WAL mode, `PRAGMA user_version = 5`, `PRAGMA foreign_keys = ON`):
  - `settings(key, value)` — source path, engine profile, selected package, extraction rules, file selection overrides, display font, Unity TMP wrap/scale/shaping/contrast options, Gettext `.pot -> .po` target, team settings, last output.
  - `sources(path, scope)` — stable per-source scope IDs preserving unit history when switching/relocating sources.
  - `files(path, hash, format, snapshot, active)` — SHA-256 fingerprint of every scanned resource (including lazy-skipped secondary locale resources).
  - `units(id, path, locator, source, target, context, status, warning, active, revision, imported)` — unit statuses: `pending`, `translated`, `imported`, `approved`, `conflict`; `active`: `0` (removed), `1` (included), `2` (excluded by File Guard or user).
  - `glossary(source, target)` — project terminology enforced by translator & reviewer prompts and local issue checks.
  - `attempts(...)` — idempotent in-flight tracking for single-runner batches.
  - `team_runs`, `team_batches`, `team_notes` — durable state machine for multi-agent translation runs (resume after crash, cancellation, or budget stop).
  - `usage_ledger(provider, model, requests, input_tokens, output_tokens, thinking_tokens, total_tokens, characters, updated)` — cumulative lifetime token/cost tracking across runs.
- `outputs/` — preview/final builds and XUnity exports.
- `backups/` — verified SHA-256 backups for direct in-place installs.
- `team-live.log` — append-only per-batch execution log.

User-level settings live in `%LOCALAPPDATA%\Locara` (with automatic read fallback to legacy `%LOCALAPPDATA%\Locara2` and `Locara2/*` Windows Credential Manager entries for seamless upgrades).

---

## 3. Containers, Engines & Format Codecs

### Containers (`Containers.cs`)
- **`FolderContainer`**: Safe recursive directory walker rejecting symlinks/reparse points (`Identity.RequireNoLinks`) and skipping `.git`/`.godot`.
- **`ZipContainer`**: SharpZipLib adapter supporting AES-256 encrypted ZIPs with a 1 GiB decompression bomb limit.
- **`PckContainer`**: Pure-C# Godot **PCK v2** and **Godot 4.5+ PCK v3** streaming reader/writer:
  - Supports `PCK v3` end-of-file directory table (`dirBase`), `PACK_REL_FILEBASE`, bare paths vs `res://` prefix preservation, MD5 verification, and large archives up to 16 GiB.
  - `ProjectService.FindMainGodotPck` automatically selects root-level Godot game `.pck` archives containing `project.godot` or `project.binary`, ignoring loose metadata files and `mods/*.pck`.

### Unity Engine Pipeline (`UnityBundleFormat.cs`, `UnityI2Format.cs`, `UnityAssetsFormat.cs`, `StarTruckerDisplay.cs`)
- **`UnityFsBundle` & `UnityFsStream`**: Pure-C# UnityFS v6–v8 (LZ4/LZ4HC/uncompressed) block codec preserving the strict **128 KiB uncompressed block invariant** required by Unity 6 / IL2CPP `BundleFileInstance`.
- **`UnitySerializedFile`**: Pure-C# reader/rewriter for Unity SerializedFile v17–v26 (Unity 2017 – Unity 6).
- **Format Handlers:**
  - `UnityI2Format` (`unity-i2`): Extracts and rewrites `I2.Loc.LanguageSourceAsset` (`I2Languages`) inside `data.unity3d` / `resources.assets`.
  - `UnityBundleFormat` (`unity-bundle`): Extracts and rewrites Unity Localization `StringTable` and human `TextAsset` objects inside Addressables `.bundle` files, automatically synchronizing `catalog.bin` and `catalog.json` uncompressed CRC32 + bundle sizes (`UnityAddressablesCatalog`).
  - `UnityAssetsFormat` (`unity-assets`): Universal `TextAsset` (`classId = 49`) extractor/rewriter across `resources.assets`, `sharedassets*.assets`, and `data.unity3d` nodes; parses embedded `JSON`, `XML`, `PO`, `TSV`, `CSV`, `INI`, `YAML`, and plain `TXT`, filtered per-asset via `TextQualityClassifier`.
  - `XUnityTranslationFormat` & `XUnityAutoTranslatorExporter` (`XUnityAutoTranslator.cs`): Reads/writes BepInEx `_AutoGeneratedTranslations.txt` (`key=value` with proper escape preservation) and exports ready-to-drop `BepInEx/` overlays for hardcoded C#/IL2CPP Unity games.
- **Arabic Display, TMP Patching & Loose Font Injection:**
  - `UnityTmpArabicPatcher`: Generalized across **all** Unity games (I2, Addressables, `TextAsset`, or loose files). Locates `LiberationSans` Font (`classId=128`), `LiberationSans SDF - Fallback`, `TMP Settings`, and `LiberationSans SDF - Fallback Material` (`classId=21`); embeds the chosen Arabic TTF, prepends the fallback font asset, neutralizes TMP's runtime OpenType `'kern'` (`nrek`) feature and `m_enableKerning` (preventing RTL kerning shifts on visually-reordered Arabic), rescales fallback font metrics (`0.50f–1.50f`), and optionally boosts visual contrast (`unity_arabic_contrast`: Bold weight upgrade + SDF `_FaceDilate` boost). When no TMP fallback host exists, `ProjectService` injects a bilingual Arabic+Latin TTF (`ArabicFontCatalog.ResolveBilingual`) into loose `*_Data/**/*.ttf` fonts.
  - `StarTruckerDisplay.Shape(text, maxLineChars)`: Uses Windows native ICU (`icu.dll`) for Arabic Presentation Forms shaping + UAX #9 BiDi visual reordering, combined with a **balanced dynamic-programming tag-safe Arabic word-wrapper** (`WrapArabicParagraph`) that keeps `<nobr>`/`<link>`/`<a>` atomic and closes/reopens paired rich-text tags across wrapped lines.
  - **Native RTL Toggle (`unity_arabic_shaping`):** Disables pre-shaping to write logical UTF-8 Arabic while keeping TMP fallback font injection active.

### Unreal Engine 4 & 5 Pipeline (`UnrealLocresFormat.cs`, `UnrealStringTableFormat.cs`, `UnrealPakCodec.cs`)
- **`UnrealLocresFormat` (`unreal-locres`)**: Pure-C# `.locres` v0–v3 reader/writer preserving `Namespace`, `Key`, `FTextKey` hashes, `SourceStringHash`, ref-counted string pool, and writing Arabic/non-ASCII as UTF-16LE `FString`.
- **`UnrealStringTableFormat` (`unreal-stringtable`)**: Pure-C# UE4/UE5 `UStringTable` `.uasset` reader/writer supporting both standalone single-file `.uasset` packages and cooked split `.uasset` + `.uexp` pairs (on disk and inside `.pak` archives), updating `FObjectExport.SerialSize` and preserving metadata comments.
- **`UnrealPakCodec` & `UnrealPakFormat` (`unreal-pak`)**:
  - Reads UE4/UE5 `.pak` v4–v12 (None/Zlib/Gzip/LZ4) with clear diagnostics for AES-encrypted or Oodle-compressed archives.
  - Extracts primary game `.locres` and `UStringTable` `.uasset`/`.uexp` assets while ignoring Chromium `.pak` and `Engine/*` boilerplate.
  - **Non-IoStore games:** Emits a drop-in patch archive (`<BasePakStem>_Locara_P.pak`) in `Content/Paks/` (v8/v11/v12, including `PrunedDirectoryIndex` inside `PathHashIndex` and UE5.5+ v12 non-null-terminated filenames).
  - **UE5 IoStore games (`.pak` + companion `.utoc`):** Streams an in-place patch of the base `.pak` via `UnrealPakCodec.PatchExistingPak` so UE5 never rejects standalone `_P.pak` files with `utoc not found`.
  - **Bilingual Font Injection:** Automatically injects a bilingual Arabic+Latin font (`ArabicFontCatalog.ResolveForUnreal`) into game `.ufont`/`.ttf` assets, auto-detecting UE4 raw TTF vs UE5 8-byte envelope wrapping.

### Godot Binary `.translation` Pipeline (`GodotTranslationFormat.cs`)
- Pure-C# Godot 3 & 4 `RSRC` and compressed `RSCC` (**FastLZ `mode=0`**, **Zlib/Deflate `mode=1`**, **Zstd `mode=2`**, and **Gzip `mode=3`**) binary resource parser/writer supporting:
  - `OptimizedTranslation` (Godot 4) and `PHashTranslation` (Godot 3): Decompresses SMAZ payloads using Godot's official 254-entry reverse codebook; writes translated Arabic strings as uncompressed UTF-8 (`comp_size == uncomp_size`) while preserving `hash_table`, `bucket.size`, `bucket.func`, and `elem.key` bit-for-bit.
  - Plain `Translation` resources (`PackedStringArray` and `Dictionary` message maps).

### Additional Engines & Structured Text Formats (`Engines.cs`, `Formats.cs`, `CatalogFormats.cs`, `StructuredTextFormats.cs`)
- **Ren’Py** (`.rpy` `game/tl/arabic` templates + SDK generation), **RPG Maker MV/MZ** (`data/*.json` + event codes `401`/`405`/`102`/`402`), and **GameMaker** (`data.win`/`game.unx` detection, external localization files, Steam trial integration, and `STRG` compiled-archive diagnostics).
- **Structured & Catalog Formats:** `JSON`, `CSV`/`TSV`, `XML`/`XLIFF`, `INI`, `YAML`, **`TOML`** (comment/type-preserving `[table]` & `[[array_of_tables]]`), **`.strings`** (UTF-8 / UTF-16LE / UTF-16BE BOM-preserving with `/* ... */` & `//` comment context), **`Lua`** (lexer-safe table string extractor/rewriter), `PO`/`POT` (with `.pot -> .po` sibling/Arabic target delivery), and `XLSX`.

---

## 4. Universal Smart Discovery, Lazy-Skip Scanning & Deployment Advisor (`FieldSemantics.cs`, `TextQualityClassifier.cs`, `SchemaClusterer.cs`, `LocalizationScout.cs`, `DeploymentAdvisor.cs`, `ProjectService.cs`)

Before translation, every discovered resource passes through a 3-layer adaptive pipeline:
1. **Layer 1 — Local Semantic, Language & Lazy-Skip Guard (Zero Cost):**
   - **Engine Metadata Filter (`Engines.IsEngineInternalMetadata`)**: Drops runtime configs, IL2CPP backup directories, crash handlers, and Locara's own `*_Locara_P.pak`.
   - **Lazy-Skip Secondary Locale Optimization**: When primary English/template files exist in the container index, secondary-language files are fingerprinted via streaming SHA-256 without reading payloads into memory, parsing formats, or inserting unused rows into SQLite — accelerating initial scans on multi-gigabyte multi-language `.pck`/`.zip` archives while extracting on demand immediately if the user manually enables a secondary locale in Tab «الملفات».
   - **Dedicated Localization Priority**: When primary dedicated localization files provide ≥ 20 human units, sidecar open data files outside explicit localization directories are classified as `AuxiliaryData` and auto-excluded by default.
   - **Key + Value Semantic Classifier & Schema Clustering (`FieldSemantics` + `TextQualityClassifier` + `SchemaClusterer`)**: Combines value heuristics with field-name semantics across open data formats (`JSON`, `YAML`, `TOML`, `Lua`, `XML`, `INI`, `CSV`/`TSV`, `XLSX`), supporting per-file `humanOnly` mode, normalized `ExtractionRule.FieldPatterns`, and structural `FileSchemaCluster` grouping.
2. **Layer 2 — Optional AI Localization Scout (`LocalizationScout`, `ScoutPlanWindow`)**:
   - Sends a compact, token-budgeted schema summary (field names + short samples, never full text) to the user's configured AI provider, enforces strict anti-hallucination validation on returned `clusterId` and `fieldPatterns`, records usage in `usage_ledger`, and applies user-approved `ExtractionRule.FieldPatterns` across entire clusters.
3. **Layer 3 — Deployment Intelligence (`DeploymentAdvisor`)**:
   - Analyzes engine, active formats, and sibling assets to surface actionable guidance before build/install (Gettext `.pot` vs sibling `.po` delivery, Unity zero-text → `XUnity.AutoTranslator`, UE5 IoStore vs `_P.pak`, GameMaker `data.win` vs external files).

---

## 5. Multi-Agent AI Translation Engine (`Translation.cs`, `TranslationTeam.cs`, `ApiProvider.*.cs`)

- **Token Masking (`Tokens`)**: Protects `{0}`, `%s`, `{[VAR]}`, `<tags>`, `[bbcode]`, and escape sequences with `__LOCARA_n__` placeholders before API transmission and validates restoration before saving.
- **Built-in Arabic Prompt Presets (`ArabicPromptPresets`)**: Six curated genre presets (`balanced`, `story-rpg`, `action-ui`, `scifi-sim`, `fantasy-lore`, `indie-light`) configuring cohesive project style and Supervisor/Translator/Reviewer instructions in one click while preserving model routing.
- **Orchestration & Fault Tolerance (`TranslationTeam`, `TranslationRunner.CanRetry`)**:
  - Optional **Supervisor** + up to **16 parallel workers** (`translate → review → revise → apply`) with per-role and per-worker (`#1..#16`) API profile & model routing.
  - Exponential backoff retries on transient network/rate-limit errors (`408`, `429`, `5xx`, transport drops) and fast-fail on auth/client/TLS pin errors.

---

## 6. Safe Installation, Steam Trials & Security

- **Verified In-Place Install (`PackageInstaller`)**: Records every changed, companion (`.uexp`), and drop-in file (`OriginalHash == ""`) in `install.json` + SHA-256 verified backup; detects external game updates and restores originals in one click.
- **Steam Trials (`SteamGameTrial`, `StarTruckerTrial`)**: Supports Steam-installed **Unity**, **Unreal Engine 4/5**, **Godot**, **GameMaker**, and **Star Trucker** games via `steam://rungameid/<AppId>` with automatic exit restore and crash-recovery journals.
- **Security Invariants**: Path-traversal and symlink blocking (`Identity.SafePath` / `Identity.RequireNoLinks`), Windows Credential Manager API key encryption, and explicit SHA-256 TLS certificate pinning for self-signed local HTTPS endpoints.
