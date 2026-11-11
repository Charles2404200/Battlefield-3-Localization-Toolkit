# Battlefield 3 Vietnamese Localization Toolkit

Tools for exporting, translating, rebuilding, and installing Vietnamese text and fonts for the **32-bit Steam release of Battlefield 3**. The main application is a C# Windows Forms GUI. Text and font preparation tools are written in C#; the in-game font hook is a C++ x86 `dinput8.dll` proxy.

The current workflow has been tested on one Steam installation. The game has displayed **“CHIẾN DỊCH”** correctly with translated text and the replacement font. File offsets and payload sizes are specific to that game build; other releases have not been verified.

## What the toolkit does

| Component | Purpose |
| --- | --- |
| `Bf3VietHoaGui` | Exports text and fonts, validates the translation CSV, stages and installs the translation, and restores the previous installation. |
| `Bf3TextDump` | Reads the English localization bundle, exports a translator-friendly CSV, validates it, and rebuilds the text and Vietnamese character map. |
| `Bf3FontDump` | Exports the three English GFX font assets and a manifest. Its archive-import path is for offline analysis only. |
| `Bf3FontSubset` | Builds three fixed-size CAS font payloads from GFX files that already contain the required Vietnamese glyphs. |
| `Bf3FontHookNative` | Loads those payloads from the game's `fonts` directory and substitutes them in memory when BF3 reads the matching CAS assets. |

## Requirements

- Windows and a local **32-bit BF3 Steam installation** matching the supported archive layout.
- A writable project directory and permission to update files in the game directory.
- To run a published GUI: `Bf3VietHoaGui.exe`, plus its adjacent `hook` and `compressor` directories. A self-contained publish does not require a separate .NET runtime. This source repository does not include compiled GUI releases.
- To build from source: .NET 8 SDK, CMake 3.20 or newer, and Visual Studio C++ tools capable of building Win32/x86 DLLs.
- Three edited GFX font files with the Vietnamese glyphs required by the translation. Exporting the original game fonts alone does **not** add those glyphs. Extracted game assets and edited GFX files are not included in this source repository.

## Quick start

1. Close BF3. Build the GUI as described below, or use a separately packaged release. Open `Bf3VietHoaGui.exe` and check the **game directory**, **project directory**, and **translation CSV** paths.
2. Click **Xuất text** (Export text). This writes `dump/us_loc_en.tsv` for inspection and creates `translations/us_loc_en_vi.csv` if it does not already exist. An existing translation CSV is preserved.
3. Click **Xuất font** (Export fonts) if `dump/fonts/en/manifest.json` is missing. Make sure the GFX files under `dump/fonts/en/ui/static/` contain the full Vietnamese glyph set before installing. The exporter preserves existing edited GFX files.
4. Edit the `vietnamese` column in the CSV using UTF-8. Leave a cell empty to keep its English text. Keep `id_hash`, the English source, placeholders such as `{0}`, and control markers such as `#SEC#` intact.
5. Click **Kiểm tra CSV** (Validate CSV), then **Cài bản dịch** (Install translation). The GUI builds text, the character map, and the font payloads, backs up the current installation, and checks sizes and hashes before copying files.
6. Launch BF3 yourself to check the result. The GUI never launches the game. If you need to undo the latest installation, close BF3 and click **Khôi phục lần cài trước** (Restore previous installation).

The CSV has three logical columns:

| Column | Meaning |
| --- | --- |
| `id_hash` | Stable game text ID. Do not edit. |
| `english` | Original source text used for validation. Do not edit. |
| `vietnamese` | Your UTF-8 translation. Empty means use English. |

The importer normalizes Vietnamese text to Unicode NFC. It accepts commas accidentally left unquoted in the third column by joining the extra fields back into that translation; it does not rewrite the CSV. Standard CSV quoting is still recommended for spreadsheet compatibility, especially for commas, quotes, and newlines.

## How installation works

BF3 stores English localization in `Update/Patch/Data/Win32/Loc/en.sb`. The importer rewrites the text and character histogram while keeping the complete `.sb` file the same length. It maps 134 Vietnamese characters outside ASCII to game bytes and writes the matching `vietnamese_map.json` used by the font builder.

The localization text must fit the original **263,138-byte compressed TOC slot**. The importer first uses zlib, then bundled Zopfli compression if needed. If the original string order does not fit, it can group translated strings at the end and update the ID-to-offset table. It verifies each rebuilt ID and string before installation. If no supported layout fits, installation stops without changing game files.

The C++ x86 hook forwards DirectInput 8 exports to the system DLL. It intercepts matching `ReadFile` calls for three known CAS locations and replaces the returned bytes in memory with payloads from `Battlefield 3/fonts/`. The payloads must remain exactly **62,403**, **28,727**, and **28,845** bytes for the main UI, EULA, and store fonts respectively. The hook caches archive identity by handle, validates the target buffer, and handles completed overlapped reads. It loads font files when the game starts; restart BF3 after changing a payload. The current hook does **not** write a runtime log; an old `fonts/hook_native.log` may remain from previous builds.

The GUI installs only:

```text
Battlefield 3/
  dinput8.dll
  fonts/
    fontcollection_en_fontlib.cas-payload
    eulafontcollection_en_fontlib.cas-payload
    storefontcollection_en_fontlib.cas-payload
  Update/Patch/Data/Win32/Loc/en.sb
```

It checks that `en.toc` still matches the staged source, but does not replace it. It does not edit `UI.toc`, `cas.cat`, or the game's CAS archives. The GUI refuses to overwrite an unrelated `dinput8.dll`. Directly importing rebuilt fonts into retail archives is disabled because this Steam build rejected those modified archives as corrupt.

Each GUI installation creates a snapshot in `build/Loc/gui_install_backups/`. Staged text and map files are in `build/Loc/gui_stage/`; staged font payloads are in `build/fonts/gui_stage/vietnamese-metadata/`. The baseline copy of `en.sb` and `en.toc` is in `build/Loc/backup/`. Keep these backups if you want the GUI to restore the previous state.

## Build from source

From the project root in PowerShell, build the x86 hook first. Change the CMake generator if your installed Visual Studio version differs:

```powershell
cmake -S Bf3FontHookNative -B Bf3FontHookNative/build-x86 -G "Visual Studio 18 2026" -A Win32
cmake --build Bf3FontHookNative/build-x86 --config Release --target bf3_dinput8_proxy bf3_font_smoke
```

Then publish the self-contained x86 GUI:

```powershell
dotnet publish Bf3VietHoaGui -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o build/gui-release
```

Keep `build/gui-release/hook/dinput8.dll` and `build/gui-release/compressor/zopfli.exe` beside the published EXE. The GUI project copies these from the hook build and `Bf3TextDump/compressor` during publish.

The command-line tools can also be run directly:

```powershell
dotnet run --project Bf3TextDump -c Release -- --validate
dotnet run --project Bf3FontDump -c Release -- -o dump/fonts/en
dotnet run --project Bf3FontSubset -c Release -- --manifest dump/fonts/en/manifest.json --corpus translations/us_loc_en_vi.csv --vietnamese-map build/Loc/gui_stage/vietnamese_map.json --output-dir build/fonts/manual-stage
```

For CLI text import options, run `dotnet run --project Bf3TextDump -- --help`. The GUI is the supported way to coordinate text, font, backups, and installation.

## Common failures

| Symptom | Check |
| --- | --- |
| CSV validation reports changed English sources | Restore the `english` column to the exported text; edit only `vietnamese`. |
| Font builder reports missing glyphs | Add the reported glyphs to the edited GFX files, then rebuild. Original BF3 GFX files do not contain the complete Vietnamese set. |
| Text exceeds the signed TOC slot | Shorten some translations. The importer has already tried its supported compression and string layouts. No game file is installed on this failure. |
| GUI reports an existing `dinput8.dll` | Check the existing mod. The installer will not overwrite an unrelated proxy DLL. |
| Game reports corrupt data | Close BF3 and use **Khôi phục lần cài trước** in the GUI, then check that the game opens before trying another build. |

## Third-party code

The native hook includes [MinHook](Bf3FontHookNative/third_party/minhook) under its BSD-style license. The text compressor includes [Zopfli](Bf3TextDump/compressor/source) under Apache License 2.0. Their license texts are kept next to the bundled sources and binaries. The project does not include a top-level license file; these third-party licenses do not define a license for the toolkit's own code.
