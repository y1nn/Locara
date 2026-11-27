# Locara Architecture

Locara is a local-first PC-game Arabic localization workbench built on **.NET 10** (`net10.0` core/CLI/tests, `net10.0-windows` WPF desktop). Projects are stored locally in self-contained folders backed by SQLite WAL (`project.db`, Schema v6).

## 1. Solution Layout

| Project | Target | Role |
|---|---|---|
| `src/Locara.Core` | `net10.0` | Format provider registry, engine detection, container & binary codecs (including GameMaker `data.win` `STRG`), Arabic shaping/wrapping/font patching, live Arabic preview formatter, file quality filtering & AI Scout, SQLite store, cross-project Shared TM, glossary consistency audit, UI length guard, pre-build QA gate, glossary & light-patch exchange, quiet update checker & verified self-updater, stateful AI translation bots, installer & Steam trial manager |
| `src/Locara.UnityRtlFix` | `netstandard2.0` | Lightweight MIT BepInEx 5 runtime plugin (`com.locara.unity.rtlfix`) compiled against compile-only stubs and embedded in `Locara.Core` for classic Unity (`UI.Text` / `NGUI`) multi-line Arabic RTL reflow |
| `src/Locara.Desktop` | `net10.0-windows` | Arabic RTL WPF Localization Studio (Dark/Light `ThemeManager`, 4-stage rail, live 3-Bot Studio, Split Contextual Editor with live Arabic preview card, Packages-page pre-build QA card, `PatchImportWindow`, Stage 4 installer, quiet GitHub Releases update badge with verified 1-click self-update, and Windows Credential Manager) |
| `src/Locara.Cli` | `net10.0` | Headless CLI (`scan`, `aes-key`, `files`, `clusters`, `scout`, `advice`, `translate`, `build`, `export-xunity`, `install`, `restore`, etc.) |
| `checks/Locara.Checks` | `net10.0` | xUnit test suite (`229` tests) |

Build settings (`Directory.Build.props`, `global.json`, `.github/workflows/ci.yml`):
- .NET SDK `10.0.404` (`rollForward: latestPatch`), `<Version>1.5.0</Version>`, `Nullable=enable`, `TreatWarningsAsErrors=true`, `RestorePackagesWithLockFile=true`.
- CI runs privacy checks, locked restore, Release build, xUnit tests, tagged Windows x64 zip packaging, and automated GitHub Release publishing (`publish-release` job scoped to `v*` tags).

## 2. Project Storage (`project.db`)

Each `.locara` project folder contains:
- `project.db` (SQLite WAL, `user_version = 6`): `settings`, `sources`, `files`, `units`, `glossary`, `bot_glossary`, `attempts`, `team_runs`, `team_batches`, `team_notes`, `usage_ledger`.
- `outputs/` — preview/final builds and XUnity exports.
- `backups/` — SHA-256 verified originals for in-place installation and one-click restore.
- `team-live.log` — per-batch AI team execution log.

Typed access to `settings` goes through `ProjectSettings.cs`. User settings live in `%LOCALAPPDATA%\Locara` (with read fallback to `%LOCALAPPDATA%\Locara2`), including `translation-memory.db` (cross-project Shared Translation Memory SQLite WAL), `ui-theme.txt`, `update-check.txt`, and `provider-profiles.json`.

## 3. Core Architecture (`src/Locara.Core/`)

- **Discovery & Providers (`LocalizationProviders.cs`, `LocaleClassifier.cs`, `Engines.cs`, `Containers.cs`):**
  - `LocalizationProviderRegistry` registers every format with its matching rules, binary content probe, streamed disk extractor, and metadata (`IsDedicatedLocalization`, `IsOpenDataFormat`, `IsConfigurable`, `UsesStreamedFileAccess`).
  - `Containers.cs` handles folders (rejecting symlinks), AES-256 ZIPs, and Godot `PCK v2/v3` archives up to 16 GiB.
  - `ProjectService.cs` / `ProjectService.Scan.cs` / `ProjectService.Build.cs` implement two-pass scanning (prioritizing dedicated localization files and lazy-skipping secondary locales and auxiliary data files), custom-archive diagnostics when no supported text files exist, and overlay-only builds for large loose-folder games and in-place deployments.

- **Engine Codecs:**
  - **Unity (`UnityI2Format.cs`, `UnityBundleFormat.cs`, `UnityAssetsFormat.cs`, `XUnityAutoTranslator.cs`, `UnityClassicRtlReflow.cs`, `UnityClassicRtlPlugin.cs`):** Pure-C# UnityFS (preserving the 128 KiB uncompressed block size) and SerializedFile v17–v26 support for I2 Localization, Addressables `.bundle` + `catalog.bin`/`catalog.json` CRC sync, `TextAsset` extraction, BepInEx `XUnity.AutoTranslator`, and embedded `Locara.UnityRtlFix.dll` runtime multi-line RTL reflow plugin for classic Unity (`UI.Text`/`NGUI`). `UnityTmpArabicPatcher` injects fallback Arabic fonts into TextMeshPro and neutralizes runtime OpenType `'kern'` features; loose `*_Data/**/*.ttf` fonts receive a bilingual Arabic+Latin fallback when TMP isn't present.
  - **Unreal Engine 4 & 5 (`UnrealLocresFormat.cs`, `UnrealStringTableFormat.cs`, `UnrealPakCodec.cs`):** Reads and writes `.locres` v0–v3, standalone and split `.uasset`/`.uexp` `UStringTable` assets, and `.pak` v4–v12 archives (None/Zlib/Gzip/LZ4/Oodle plus AES-256 ECB index & payload decryption when a key is supplied). Emits `*_Locara_P.pak` patch archives or stream-patches UE5 IoStore `.pak` files (preserving AES index encryption) and injects bilingual `.ufont`/`.ttf` fonts.
  - **Godot (`GodotTranslationFormat.cs`):** Reads and writes Godot 3/4 `RSRC` and compressed `RSCC` (`FastLZ`, `Zlib`, `Zstd`, `Gzip`) `.translation` files (`OptimizedTranslation`/`PHashTranslation` with SMAZ decoding and plain `Translation`) while keeping hash tables intact.
  - **GameMaker (`GameMakerDataWinFormat.cs`):** Streamed pure-C# IFF `FORM` reader/rebuilder for `data.win`, `game.unx`, and `game.ios`. Extracts only structurally human `STRG` strings (identifier, resource-name, shader, and path strings referenced by `GEN8`/`SPRT`/`OBJT`/`ROOM`/`CODE`/etc. are filtered out). Unmodified entries keep their original offsets; expanded entries are appended with 128-byte alignment and downstream `TXTR`/`AUDO` pointers are shifted, failing fast on unknown post-`STRG` chunks. In-place installs use the standard SHA-256 verified backup. Text is written as logical UTF-8 by default; the optional per-project setting `gamemaker_shape_arabic` (default `false`) pre-shapes Arabic. No SQLite schema change (still Schema v6).
  - **Other Engines & Formats (`Formats.cs`, `CatalogFormats.cs`, `StructuredTextFormats.cs`):** Ren’Py, RPG Maker MV/MZ, GameMaker external files, JSON/JSONC, CSV/TSV, XML/XLIFF, repeated-key INI, YAML, TOML, `.strings`, Lua, `LocalizationTextFormat` (`loc-txt`), PO/POT (including `.pot -> .po` target delivery), and XLSX.

- **Arabic Display (`ArabicTextEngine.cs`, `StarTruckerDisplay.cs`, `ArabicFontCatalog.cs`):**
  - Uses Windows native `icu.dll` for Arabic contextual shaping and UAX #9 BiDi ordering, paired with tag-safe dynamic-programming wrapping (keeping `<nobr>`, `<link>`, `<a>`, and BBCode spans intact) and bundled SIL OFL 1.1 fonts.
  - `ArabicPreviewFormatter` powers the editor's **Live In-Game Arabic Preview**: it wraps text at a 30/45/60-character box width and renders either `PreShaped` (visual-order Presentation Forms, with LRM `\u200E` grapheme-cluster isolation only in `DisplayText` for WPF `LeftToRight` text blocks so DirectWrite does not double-reverse them; `RawEngineOutput` never contains directional marks) or `Logical` (unshaped wrapped text) output, and flags lines that exceed the box width.

- **Filtering, Productivity, Stateful AI Bots & Deployment (`FieldSemantics.cs`, `TextQualityClassifier.cs`, `SchemaClusterer.cs`, `LocalizationScout.cs`, `DeploymentAdvisor.cs`, `SharedTranslationMemory.cs`, `GlossaryConsistencyChecker.cs`, `UiLengthGuard.cs`, `PreBuildQaGate.cs`, `GlossaryExchange.cs`, `LocaraPatchExchange.cs`, `AppUpdateChecker.cs`, `AppSelfUpdater.cs`, `Translation.cs`, `TranslationBots.cs`, `TranslationTeam.cs`, `PackageInstaller.cs`, `SteamGameTrial.cs`):**
  - Classifies files/fields locally, optionally runs token-budgeted schema analysis via AI Scout, and advises on engine-specific delivery.
  - Provides cross-project 100% exact-match translation prefill (`SharedTranslationMemory`), post-translation manual glossary consistency auditing (`GlossaryConsistencyChecker`), visible-length overflow warnings for short UI strings (`UiLengthGuard`), and an optional, non-blocking GitHub Releases update checker (`AppUpdateChecker`, zero telemetry).
  - `PreBuildQaGate.Evaluate` summarizes project readiness in four categories: `issues` (token errors and conflicts; the only blocking category), `glossary`, `pending`, and `length` (non-blocking warnings). The Packages page shows it as a card, direct in-place installs show its warnings in the confirm dialog, and the editor has a matching `length` filter.
  - `GlossaryExchange` (`locara-glossary-v1` JSON, or CSV) and `LocaraPatchExchange` (`locara-patch-v1`, `.locara-patch.json`, approved lines only) exchange glossaries and light patches with bounded file sizes and JSON depth. `PatchImportWindow` previews every import; lines are stored with status `imported` by default and become `approved` only if the user ticks the approve checkbox.
  - `AppSelfUpdater` downloads `Locara-windows-x64.zip` from allowlisted HTTPS GitHub release URLs, verifies it against the release `.sha256` asset (size caps, constant-time SHA-256 comparison, Zip-Slip-safe extraction, PE header check), then writes an `apply-update.cmd` swap-and-restart script. It only runs for writable standalone installs; the manual GitHub release link is always kept.
  - Masks placeholders/tags before AI requests and runs an autonomous 3-bot pipeline: `GameTermMiner` + `Lore & Glossary Bot` (builds `world_brief` and `bot_glossary`), `SceneLaneBuilder` + `SceneSessionState` (contiguous scene lanes with bounded multi-turn `ChatTurn[]` history, rolling `previousLines`/`upcomingLines`, live cross-bot term sharing, and static prefix prompt caching), and `SmartQaEvaluator` (skips external QA calls on clean short UI labels while routing narrative/ambiguous lines to the contextual QA Bot).
  - Verifies every in-place install and Steam trial with SHA-256 checksums, external-update detection, and crash-recovery journals.
