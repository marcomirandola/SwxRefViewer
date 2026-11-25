# SwxRefViewer

A small Windows tool that shows the file references stored in SOLIDWORKS assemblies, parts and drawings. You do not need to open SOLIDWORKS to use it.

It helps when an assembly opens with unresolved references, for example when you import it into Vault, or when a sub-assembly opens fine on its own but shows missing references inside the top-level assembly.

## What it shows

- **Tree (left):** the top-level file and every referenced file, recursively. Colours:
  - black: found at the stored path
  - dark yellow, *Found elsewhere*: not at the stored path, but SOLIDWORKS can find it somewhere else
  - orange, *Several files with this name*: different files with the same name were found in the searched folders
  - red-orange, *Other file with same name already loaded*: SOLIDWORKS loads only one file per file name. Another file with the same name was loaded earlier in this assembly, so SOLIDWORKS uses that one instead of the stored path
  - red, *Not found*: SOLIDWORKS will not find the file
  - grey: virtual component (stored inside the assembly)
  - bold: something below this node is not OK
- **References of the selected file:** each stored path, the resolved path, how it was found, configurations and where the information was read from. Select a row to see every file with that name found on disk. Double-click a row to jump to the file in the tree.
- **Same file name, different paths:** every file name that is referenced with more than one path (or resolves to more than one file) anywhere in the assembly, with the file that holds each reference. This is usually the fastest way to find the cause of "resolves on its own, but not in the top-level assembly".
- **Problems only** in the toolbar hides all branches that are OK.
- **Export CSV** writes all references to a CSV file (semicolon separated, opens in Excel).

You can also drag a file onto the window, or start `SwxRefViewer.exe "C:\path\to\top.sldasm"`.

## How files are read

1. **SOLIDWORKS Document Manager (recommended).** It is installed with SOLIDWORKS (or with the free SOLIDWORKS Document Manager setup). It needs a license key, which SOLIDWORKS subscription customers can request on the SOLIDWORKS customer portal (API support, "Document Manager key"). Enter the key in **Options**, or set the environment variable `SWX_DM_LICENSE_KEY`. The tool then reads the external references and, for assemblies, the components of every configuration.
2. **Raw file scan (fallback, no license needed).** The tool searches the file for stored path strings. This is a heuristic: it may also list old paths that are still stored in the file, it cannot tell configurations or suppressed components apart, and it can miss paths in compressed parts of the file. Use it to get a first idea; use the Document Manager for reliable results.

The log tab says which reader was used, and why the Document Manager was not used when it falls back.

## How "resolved" is decided

The tool approximates the search order SOLIDWORKS uses when it opens an assembly:

1. a file with the same name that is already loaded in this assembly
2. the search folders from **Options** (enter the same folders as in SOLIDWORKS *Tools > Options > File Locations > Referenced Documents*)
3. the folder of the referencing file
4. the path stored in the referencing file
5. the stored sub folders, relative to the referencing file
6. the folder of the top-level file

It is an approximation, not SOLIDWORKS itself. That is why the details always list every file with that name that was found, so you can see which one SOLIDWORKS might pick.

## Install

Download the latest build from the repository's **Actions** tab: open the most recent successful **Build** run and download the `SwxRefViewer-<n>` artifact. Unzip it anywhere and start `SwxRefViewer.exe`.

Requirements: Windows 10 or 11 (64-bit), .NET Framework 4.8 (included in Windows). For the Document Manager reader: SOLIDWORKS or the SOLIDWORKS Document Manager installed, and a license key.

Settings are stored in `%APPDATA%\SwxRefViewer\settings.ini`.

## Build

```bash
dotnet build SwxRefViewer.slnx -c Release
dotnet test SwxRefViewer.slnx -c Release
```

The Document Manager is called late bound (COM `dynamic`), so the build needs no SOLIDWORKS assemblies and runs on the GitHub Actions Windows runner. Every push to `main` builds, tests and uploads the program as an artifact (`.github/workflows/build.yml`). The tests use Windows paths and run on Windows only.
