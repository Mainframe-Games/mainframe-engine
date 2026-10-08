# ADR 0143 — Console strategy: PS5 + Xbox Series, own GPU API with D3D12 first, NativeAOT, private console repos

- **Date:** 2026-10-08
- **Status:** accepted (design; readiness work only until developer-program access)
- **Milestone:** none (shapes M11; reuses M12/M13/G4 seams)
- **Spec:** docs/design/future/consoles.md

## Context

The user wants to ship Mainframe Games titles on PlayStation and Xbox eventually. The engine today:

- records raw Vulkan from nodes and systems (51 files under `MainframeEngine/Src` use `Silk.NET.Vulkan`); M11 plans a
  WebGPU-shaped abstraction with WebGPU as the only second backend;
- compiles 41 GLSL shaders to SPIR-V with `glslc`;
- runs on the JIT, with SDL2 through Silk.NET 2.x, SoundFlow/miniaudio, ENet and Steamworks.

Public facts (2026-10-08, sources in the spec):

- Xbox requires Direct3D 12. PS5 has its own, undocumented-in-public graphics API. Neither offers Vulkan.
- Consoles forbid JIT. Microsoft's NativeAOT does not list consoles. FNA ships all console builds on NativeAOT, with a
  public `NativeAOT-Xbox` port for GDK licensees. Microsoft released XBOX GDK.NET (MIT) on 2026-09-28. No public .NET
  path exists for PS5.
- SDL3 supports Xbox publicly and PS5 through a free NDA fork. SoundFlow, miniaudio and ENet list no consoles.
- Console SDKs and certification requirements are under NDA; this repository is public.

## Decision

- **Targets:** PS5 and Xbox Series X|S (Series S minimum spec). No last-gen, no Switch for now.
- **Audience:** Mainframe Games' own titles. Console code is private to the studio.
- **Repositories:** the public engine holds seams, the Vulkan and D3D12 backends, shaders and public-fact design only.
  Private `mainframe-xbox` / `mainframe-ps5` repositories (created after the program agreements) hold platform code,
  pin a tagged engine release and never patch engine sources. Nothing learnt under NDA enters the public repository.
- **Rendering:** own the GPU API.
  - M11's `IGpuDevice` is designed for explicit APIs (bind groups → descriptor sets / root tables, declared pass
    usage → derived barriers, an enumerable pipeline set, capabilities instead of versions, presentation behind the
    device).
  - **D3D12 is M11's first new backend**, built publicly on Windows and tested in CI on WARP; WebGPU follows. The
    Xbox backend is a private delta on top; the PS5 backend is private.
  - One shader source compiled offline to SPIR-V and DXIL; Slang is the proposed language (separate ADR in M11).
  - Rejected: SDL3 GPU as the rendering layer (renderer rewrite, no subpasses/bindless, a ceiling on planned
    features) and Vulkan-only plus a porting studio (cheap now, most expensive later).
- **Platform layer:** SDL3 replaces SDL2 on every platform (separate ADR; bindings are a new dependency). New seams
  `IUserPlatform`, `IAudioOutput` and `IGlyphProvider` join the planned `IAppPlatform`, `IContentFileSystem`,
  `ISaveStorage` and M13 service backends, each with a desktop/null implementation.
- **Runtime:** NativeAOT on both consoles. The engine stays `IsAotCompatible` with zero warnings, and CI publishes the
  template game with NativeAOT on desktop.
- **Roadmap:** no console milestone yet. Readiness work happens through M11, M12 and a console-ready plumbing
  checklist; ports start with private spikes C3.1–C3.5 after access.

## Consequences

- M11 changes scope: D3D12 backend, WARP render tests and goldens, pipeline manifest, shader-language migration.
- New dependency decisions are pending: the shader compiler (Slang, build-only) and the SDL3 C# bindings.
- Lanes follow the [console-ready plumbing checklist](../../docs/design/future/consoles.md#console-ready-plumbing-checklist)
  (also summarised in `memory/context/lane-agent-brief.md`).
- Risks (detailed in the spec): no public PS5 .NET runtime, NativeAOT-Xbox lagging upstream .NET, an API that ends up
  WebGPU- or Vulkan-shaped, shader migration churn, SDL3 migration regressions, NDA leakage, late certification
  requirements, and the cost of two extra GPU backends.
