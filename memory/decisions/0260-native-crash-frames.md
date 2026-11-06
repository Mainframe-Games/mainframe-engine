# ADR 0260 — Native crash frames: image offset and build id, symbolicated with the platform's tools

- **Date:** 2026-11-07
- **Status:** accepted
- **Docs:** [Crash reporting → Symbols](../../docs/design/crash-reporting.md#symbols), [ADR 0259](0259-native-aot-exports.md),
  [ADR 0221](0221-native-crash-capture-in-house.md)

## Context

Crash reports symbolicate offline from an MVID, a method token and an IL offset per frame (G15.8). A NativeAOT build
(ADR 0259) has none of them: `StackFrame.GetMethod()` is null and there is no IL. Its frames are addresses in one
native image, and its symbols are a `.dSYM` (macOS), `.dbg` (Linux) or `.pdb` (Windows) written by the SDK.

## Decision

- A frame of a process without dynamic code that has a native image records `nativeImage` (the image's file name),
  `nativeOffset` (the instruction pointer minus the image base, hex) and `nativeImageId` (the Mach-O `LC_UUID`, the
  ELF GNU build id, or the PE CodeView GUID and age, read from the image's headers in memory). The method name still
  comes from the stack-trace metadata NativeAOT keeps by default.
- `symbols: separate` moves the native symbol file into the symbols zip with the PDBs; `symbols: ship` leaves it in the
  publish.
- `mf-symbolicate` resolves native frames from the symbols folder with the platform's own tools, in this order:
  `llvm-symbolizer` (any OS, when on `PATH`), else `atos` on macOS, `addr2line` on Linux. A symbol file whose id
  differs from the frame's is not used (as a rebuilt assembly's PDB is not, by MVID). On Windows a `.pdb` is matched by
  name only.

## Ruled out

- **Reading DWARF or PDB in `mf-symbolicate`:** a DWARF line-table and PDB reader is a project of its own; the platform
  tools are installed wherever these builds are made (Xcode command line tools, binutils, LLVM).
- **Native minidumps for managed exceptions:** the report stays a JSON file a person can read and scrub (ADR 0221).

## Consequences

- Windows NativeAOT frames symbolicate only with `llvm-symbolizer` installed.
- The report format gains three optional frame fields; older readers ignore them.

## Revisit when

- A crash backend (M13) symbolicates server-side: it would take the image id and offset as they are.
