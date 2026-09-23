# LEAP (Letter-Encoded Automatic multilingual Phonemizer)

*note: this repository is written by codex*

LEAP is an OpenUtau phonemizer for the IV voicebank family. It routes Hangul
lyrics to Korean CVVC, hiragana and katakana to Japanese Presamp, and Latin
lyrics to English ARPA+. It appears in the phonemizer menu as **MULTI LEAP**.

The repository has one implementation at `src/LEAPhonemizer.cs` and one build
project, `LEAPhonemizer.csproj`. The source handles the known API difference
between official OpenUtau and UtauV when reading project resolution.

## Build

Install .NET 10 SDK to build both targets, and install OpenUtau so that
`OpenUtau.Core.dll` and `OpenUtau.Plugin.Builtin.dll` are available. The
project references `C:\Program Files\OpenUtau` by default.

```powershell
dotnet build .\LEAPhonemizer.csproj -c Release -t:Rebuild
```

Outputs:

- `bin/Release/net8.0/LEAPhonemizer.dll`
- `bin/Release/net10.0/LEAPhonemizer.dll`

Official OpenUtau on .NET 8 should use the `net8.0` DLL. If OpenUtau is
installed elsewhere, pass the folder containing its API DLLs:

```powershell
dotnet build .\LEAPhonemizer.csproj -c Release -t:Rebuild `
  -p:OpenUtauBin="C:\path\to\OpenUtau"
```

The same source can also be compiled against UtauV's API DLLs by setting
`OpenUtauBin` to its `bin` folder. Set a separate `BaseOutputPath` if you
need to retain both host-specific builds; otherwise they use the same output
paths.

## Installation

Copy the appropriate `LEAPhonemizer.dll` to the host's `Plugins` folder,
then restart the host. Do not copy the `.deps.json` file. The plugin keeps
the assembly name `LEAPhonemizer` and type `LEAP.LEAPhonemizer` so existing
projects can continue to refer to it.

## Voicebank alias map

For a voicebank with non-standard Korean or Japanese aliases, place
`iv_legacy_aliases.tsv` in the singer root. See
`samples/iv_legacy_aliases.example.tsv` for the three-column format
(`language`, `legacy`, `native`). The map is optional.

This repository contains source code only. Voicebank WAV, OTO, artwork, and
the real singer-specific alias map belong in the voicebank distribution.
