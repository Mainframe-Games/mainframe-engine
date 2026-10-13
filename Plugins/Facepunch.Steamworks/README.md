# Facepunch.Steamworks 2.4.1

C# Steamworks wrapper by Facepunch Studios, MIT ([LICENSE](LICENSE)). **Vendored: do not modify.**

- **Source:** <https://github.com/Facepunch/Facepunch.Steamworks>, release 2.4.1, the `net6.0` build. Copied from the
  vendored copy in the studio's Speed Golf repository.
- **Files:**
  - `Facepunch.Steamworks.Win64.dll` + `.xml`: Windows x64.
  - `Facepunch.Steamworks.Posix.dll` + `.xml`: macOS and Linux.

## Why two builds

Facepunch ships one build per platform family. They differ in struct packing (8 bytes on Windows, 4 on macOS/Linux) and
in the native library they import (`steam_api64` vs `libsteam_api`), so one engine assembly cannot use either build on
every platform.

`MainframeEngine.Steamworks` (the only code that references these DLLs) is built twice under one assembly name, once
against each build. The engine compiles against the Posix build, both shim builds are copied to
`<output>/steamworks/{win64,posix}/` with their Facepunch DLL, and `SteamworksLoader` loads this OS's at run time. See
[Steamworks → Layers](../../docs/design/steamworks.md#layers).

## Natives

The matching Valve `steam_api` natives (the Steamworks SDK `redistributable_bin` this release was built against) live
in `MainframeEngine/runtimes/<rid>/native`: `steam_api64.dll`, `libsteam_api.so` and the universal
`libsteam_api.dylib` (osx-x64 and osx-arm64). Refresh them together with these DLLs, never one without the other. See
[Steamworks → Natives](../../docs/design/steamworks.md#natives) and [Native libraries](../../docs/design/natives.md).
