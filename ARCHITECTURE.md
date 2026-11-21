# Locara Architecture

**Locara** is a local-first PC-game localization workbench targeting **.NET 10** (`net10.0` core/CLI/tests, `net10.0-windows` WPF desktop) with zero cloud backend dependencies. Every project is a self-contained directory centered on a SQLite WAL database (`project.db`, Schema v5).

---

## 1. Solution Structure

| Project | Target | Responsibility |
|---|---|---|
| `src/Locara.Core` | `net10.0` | Engine detection, container codecs, binary & text format handlers, Arabic shaping/wrap/font patching, Layer 1 quality classifier, SQLite project store, AI translation team & usage ledger, verified installer & Steam trial manager |
| `src/Locara.Desktop` | `net10.0-windows` | Arabic RTL WPF workbench (`MainWindow` split across feature partials: Overview, Games, Files, Translation, Team, Usage, Fonts, Install) + Windows Credential Manager integration |
| `src/Locara.Cli` | `net10.0` | Scriptable headless CLI exposing project scan, file selection, translation, build, XUnity export, install, and restore |
| `checks/Locara.Checks` | `net10.0` | Automated xUnit test suite (`52` tests covering codecs, formats, Arabic shaping/wrap, SQLite migrations, AI team concurrency/retry, and install/restore safety) |

Build invariants enforced in `Directory.Build.props` and `global.json`:
- .NET SDK pinned to `10.0.404` (`rollForward: latestFeature`).
- `TreatWarningsAsErrors=true`, `Nullable=enable`, `RestorePackagesWithLockFile=true`.

---

## 2. Project Workspace & SQLite Schema (v5)

Each project directory stores:
- `project.db` (SQLite WAL mode, `PRAGMA user_version = 5`, `PRAGMA foreign_keys = ON`):
  - `settings(key, value)` — source path, engine profile, selected package, extraction rules, file selection overrides, display font, Unity TMP wrap/scale/shaping options, team settings, last output.
  - `sources(path, scope)` — stable per-source scope IDs preserving unit history when switching/relocating sources.
  - `files(path, hash, format, snapshot, active)` — SHA-256 fingerprint of every scanned resource.
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

## 3. Containers & Binary Codecs

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
- **Arabic Display & TMP Patching:**
  - `UnityTmpArabicPatcher`: Generalized across **all** Unity games (I2, Addressables, `TextAsset`, or loose files). Locates `LiberationSans` Font (`classId=128`), `LiberationSans SDF - Fallback`, and `TMP Settings`, embeds the chosen Arabic TTF (with GPOS `'kern'` table stripping for Unity FreeType compatibility), patches fallback pointers, and rescales fallback font metrics (`0.50f–1.50f`).
  - `StarTruckerDisplay.Shape(text, maxLineChars)`: Uses Windows native ICU (`icu.dll`) for Arabic Presentation Forms shaping + UAX #9 BiDi visual reordering, combined with a **balanced dynamic-programming tag-safe Arabic word-wrapper** (`WrapArabicParagraph`) that closes/reopens rich-text tags across wrapped lines so multi-line Arabic paragraphs read top-to-bottom in LTR TextMeshPro boxes.
  - **Native RTL Toggle (`unity_arabic_shaping`):** Disables pre-shaping to write logical UTF-8 Arabic while keeping TMP fallback font injection active.

### Unreal Engine 4 & 5 Pipeline (`UnrealLocresFormat.cs`, `UnrealPakCodec.cs`)
- **`UnrealLocresFormat` (`unreal-locres`)**: Pure-C# `.locres` v0–v3 reader/writer preserving `Namespace`, `Key`, `FTextKey` hashes, `SourceStringHash`, ref-counted string pool, and writing Arabic/non-ASCII as UTF-16LE `FString`.
- **`UnrealPakCodec` & `UnrealPakFormat` (`unreal-pak`)**:
  - Reads UE4/UE5 `.pak` v4–v12 (None/Zlib/Gzip/LZ4) with clear diagnostics for AES-encrypted or Oodle-compressed archives.
  - Automatically ignores Chromium `.pak`, excludes `Engine/*` boilerplate when game `.locres` exists, and prefers English `.locres`.
  - **Non-IoStore games:** Emits a drop-in patch archive (`<BasePakStem>_Locara_P.pak`) in `Content/Paks/` (v8/v11/v12, including `PrunedDirectoryIndex` inside `PathHashIndex` and UE5.5+ v12 non-null-terminated filenames).
  - **UE5 IoStore games (`.pak` + companion `.utoc`):** Streams an in-place patch of the base `.pak` via `UnrealPakCodec.PatchExistingPak` (preserving all untouched payloads and exact index layout while updating patched entry offsets + SHA-1 hashes) so UE5 never rejects standalone `_P.pak` files with `utoc not found`.
  - **Bilingual Font Injection:** Automatically injects a bilingual Arabic+Latin font (`ArabicFontCatalog.ResolveForUnreal`, falling back to `IBM Plex Sans Arabic Bold` if the chosen font lacks Latin `A–Z`) into game `.ufont`/`.ttf` assets, auto-detecting UE4 raw TTF vs UE5 8-byte envelope wrapping (`int32 len + TTF + 4 zero bytes`) and skipping CJK fonts.

### Godot Binary `.translation` Pipeline (`GodotTranslationFormat.cs`)
- Pure-C# Godot 3 & 4 `RSRC` binary resource parser/writer supporting:
  - `OptimizedTranslation` (Godot 4) and `PHashTranslation` (Godot 3): Decompresses SMAZ payloads using Godot's official 254-entry reverse codebook; writes translated Arabic strings as uncompressed UTF-8 (`comp_size == uncomp_size`) while preserving `hash_table`, `bucket.size`, `bucket.func`, and `elem.key` bit-for-bit.
  - Uncompressed `Translation` resources (`PackedStringArray` and `Dictionary` message maps).

---

## 4. Layer 1 Quality & Language Guard (`TextQualityClassifier.cs`, `ProjectService.cs`)

Before translation, every discovered resource is evaluated:
1. **Engine Metadata Filter (`Engines.IsEngineInternalMetadata`)**: Drops runtime configs, IL2CPP backup directories (`*_BackUpThisFolder_ButDontShipItWithYourGame`), crash handlers, and Locara's own generated `*_Locara_P.pak`.
2. **Multi-Language Auto-Exclusion**: Detects English vs non-English locales across both Unity Addressables bundles and localization folder/file paths (`localization/eng/` vs `deu/`, `fra/`, `jpn/`, `.fr.translation`, etc.), excluding secondary languages (`active = 2`) whenever English exists.
3. **Human vs Technical Classifier (`TextQualityClassifier`)**: Distinguishes natural prose (including standard Arabic, shaped Arabic Presentation Forms `U+FB50–U+FEFF`, Latin, CJK, Cyrillic, etc.) from GUIDs, paths, namespaces, camelCase/snake_case identifiers, and hashes.

---

## 5. Multi-Agent AI Translation Engine (`Translation.cs`, `TranslationTeam.cs`, `ApiProvider.*.cs`)

- **Token Masking (`Tokens`)**: Protects `{0}`, `%s`, `{[VAR]}`, `<tags>`, `[bbcode]`, and escape sequences with `__LOCARA_n__` placeholders before API transmission and validates restoration before saving.
- **Orchestration (`TranslationTeam`)**:
  - Optional **Supervisor** plans section guidance & terminology across workers.
  - Up to **16 parallel workers** execute `translate → review → (optional revision up to 2 rounds) → apply`.
  - Supports **per-role** (Supervisor / Translator / Reviewer) and **per-worker** (`#1..#16`) API profile & model routing, applied dynamically even when resuming a saved run.
- **Fault Tolerance (`TranslationRunner.CanRetry`)**:
  - Automatically retries transient transport drops (`HttpRequestException` without HTTP status, `IOException`, `TaskCanceledException`, `408`, `429`, `5xx`) with exponential backoff (`2s → 4s → 8s → 16s`), while failing fast on auth/client errors (`400`/`401`/`403`) and TLS certificate pin mismatches (`SecureConnectionError`).
  - Validates JSON structure & exact ID coverage per batch before writing to SQLite.

---

## 6. Safe Installation, Steam Trials & Security

- **Verified In-Place Install (`PackageInstaller`)**:
  - Records every changed and newly added file (`OriginalHash == ""` for drop-in mods) in `install.json` + project-local verified backup.
  - Detects external Steam/game updates cleanly without clobbering user files, and restores originals (and removes added drop-in files) in one click.
- **Steam Trials (`SteamGameTrial`, `StarTruckerTrial`)**:
  - Supports Steam-installed **Unity**, **Unreal Engine 4/5**, **Godot**, and **Star Trucker** games via `steam://rungameid/<AppId>`, monitoring process exit and automatically restoring originals (with crash-recovery journals in `%LOCALAPPDATA%\Locara\SteamTrials`).
- **Security Invariants**:
  - `Identity.SafePath` & `Identity.RequireNoLinks` block path traversal, Windows reserved names, and symlink/reparse-point escapes.
  - API keys are stored encrypted in Windows Credential Manager (never in `project.db` or logs).
  - Self-signed local HTTPS endpoints require explicit SHA-256 certificate pinning.
