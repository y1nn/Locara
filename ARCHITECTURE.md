# Locara Architecture

Locara is a local-first PC-game Arabic localization workbench built on **.NET 10** (`net10.0` core/CLI/tests, `net10.0-windows` WPF desktop). Projects are stored locally in self-contained folders backed by SQLite WAL (`project.db`, Schema v6).

## 1. Solution Layout

| Project | Target | Role |
|---|---|---|
| `src/Locara.Core` | `net10.0` | Format provider registry, engine detection, container & binary codecs, Arabic shaping/wrapping/font patching, file quality filtering & AI Scout, SQLite store, stateful AI translation bots, installer & Steam trial manager |
| `src/Locara.Desktop` | `net10.0-windows` | Arabic RTL WPF Localization Studio (Dark/Light `ThemeManager`, 4-stage rail, live 3-Bot Studio, Split Contextual Editor, and Windows Credential Manager) |
| `src/Locara.Cli` | `net10.0` | Headless CLI (`scan`, `aes-key`, `files`, `clusters`, `scout`, `advice`, `translate`, `build`, `export-xunity`, `install`, `restore`, etc.) |
| `checks/Locara.Checks` | `net10.0` | xUnit test suite (`109` tests) |

Build settings (`Directory.Build.props`, `global.json`, `.github/workflows/ci.yml`):
- .NET SDK `10.0.404` (`rollForward: latestPatch`), `Nullable=enable`, `TreatWarningsAsErrors=true`, `RestorePackagesWithLockFile=true`.
- CI runs privacy checks, locked restore, Release build, xUnit tests, and tagged Windows x64 zip packaging.

## 2. Project Storage (`project.db`)

Each `.locara` project folder contains:
- `project.db` (SQLite WAL, `user_version = 6`): `settings`, `sources`, `files`, `units`, `glossary`, `bot_glossary`, `attempts`, `team_runs`, `team_batches`, `team_notes`, `usage_ledger`.
- `outputs/` — preview/final builds and XUnity exports.
- `backups/` — SHA-256 verified originals for in-place installation and one-click restore.
- `team-live.log` — per-batch AI team execution log.

Typed access to `settings` goes through `ProjectSettings.cs`. User settings live in `%LOCALAPPDATA%\Locara` (with read fallback to `%LOCALAPPDATA%\Locara2`).

## 3. Core Architecture (`src/Locara.Core/`)

- **Discovery & Providers (`LocalizationProviders.cs`, `LocaleClassifier.cs`, `Engines.cs`, `Containers.cs`):**
  - `LocalizationProviderRegistry` registers every format with its matching rules, binary content probe, streamed disk extractor, and metadata (`IsDedicatedLocalization`, `IsOpenDataFormat`, `IsConfigurable`, `UsesStreamedFileAccess`).
  - `Containers.cs` handles folders (rejecting symlinks), AES-256 ZIPs, and Godot `PCK v2/v3` archives up to 16 GiB.
  - `ProjectService.cs` / `ProjectService.Scan.cs` / `ProjectService.Build.cs` implement two-pass scanning (prioritizing dedicated localization files and lazy-skipping secondary locales and auxiliary data files), custom-archive diagnostics when no supported text files exist, and overlay-only builds for large loose-folder games and in-place deployments.

- **Engine Codecs:**
  - **Unity (`UnityI2Format.cs`, `UnityBundleFormat.cs`, `UnityAssetsFormat.cs`, `XUnityAutoTranslator.cs`):** Pure-C# UnityFS (preserving the 128 KiB uncompressed block size) and SerializedFile v17–v26 support for I2 Localization, Addressables `.bundle` + `catalog.bin`/`catalog.json` CRC sync, `TextAsset` extraction, and BepInEx `XUnity.AutoTranslator`. `UnityTmpArabicPatcher` injects fallback Arabic fonts into TextMeshPro and neutralizes runtime OpenType `'kern'` features; loose `*_Data/**/*.ttf` fonts receive a bilingual Arabic+Latin fallback when TMP isn't present.
  - **Unreal Engine 4 & 5 (`UnrealLocresFormat.cs`, `UnrealStringTableFormat.cs`, `UnrealPakCodec.cs`):** Reads and writes `.locres` v0–v3, standalone and split `.uasset`/`.uexp` `UStringTable` assets, and `.pak` v4–v12 archives (None/Zlib/Gzip/LZ4 plus AES-256 ECB index & payload decryption when a key is supplied). Emits `*_Locara_P.pak` patch archives or stream-patches UE5 IoStore `.pak` files (preserving AES index encryption) and injects bilingual `.ufont`/`.ttf` fonts.
  - **Godot (`GodotTranslationFormat.cs`):** Reads and writes Godot 3/4 `RSRC` and compressed `RSCC` (`FastLZ`, `Zlib`, `Zstd`, `Gzip`) `.translation` files (`OptimizedTranslation`/`PHashTranslation` with SMAZ decoding and plain `Translation`) while keeping hash tables intact.
  - **Other Engines & Formats (`Formats.cs`, `CatalogFormats.cs`, `StructuredTextFormats.cs`):** Ren’Py, RPG Maker MV/MZ, GameMaker, JSON/JSONC, CSV/TSV, XML/XLIFF, repeated-key INI, YAML, TOML, `.strings`, Lua, `LocalizationTextFormat` (`loc-txt`), PO/POT (including `.pot -> .po` target delivery), and XLSX.

- **Arabic Display (`ArabicTextEngine.cs`, `StarTruckerDisplay.cs`, `ArabicFontCatalog.cs`):**
  - Uses Windows native `icu.dll` for Arabic contextual shaping and UAX #9 BiDi ordering, paired with tag-safe dynamic-programming wrapping (keeping `<nobr>`, `<link>`, `<a>`, and BBCode spans intact) and bundled SIL OFL 1.1 fonts.

- **Filtering, Stateful AI Bots & Deployment (`FieldSemantics.cs`, `TextQualityClassifier.cs`, `SchemaClusterer.cs`, `LocalizationScout.cs`, `DeploymentAdvisor.cs`, `Translation.cs`, `TranslationBots.cs`, `TranslationTeam.cs`, `PackageInstaller.cs`, `SteamGameTrial.cs`):**
  - Classifies files/fields locally, optionally runs token-budgeted schema analysis via AI Scout, and advises on engine-specific delivery.
  - Masks placeholders/tags before AI requests and runs an autonomous 3-bot pipeline: `GameTermMiner` + `Lore & Glossary Bot` (builds `world_brief` and `bot_glossary`), `SceneLaneBuilder` + `SceneSessionState` (contiguous scene lanes with bounded multi-turn `ChatTurn[]` history, rolling `previousLines`/`upcomingLines`, live cross-bot term sharing, and static prefix prompt caching), and `SmartQaEvaluator` (skips external QA calls on clean short UI labels while routing narrative/ambiguous lines to the contextual QA Bot).
  - Verifies every in-place install and Steam trial with SHA-256 checksums, external-update detection, and crash-recovery journals.
