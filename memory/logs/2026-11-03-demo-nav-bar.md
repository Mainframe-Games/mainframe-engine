---
summary: >-
  The Demo's nav bar is two rows: six categories on top (3D, 2D, Physics, Animation, Audio, UI) and the open
  category's scenes under them. It replaces the A6 bar, where sixteen tabs at 14 dp touched each other and the FPS
  readout. Every Demo screenshot re-taken.
---

# 2026-11-03: Demo nav bar — categories and a scene row

Branch `feature/demo-nav-bar`, from `main` at c9e1fa51. No plan or proposal: a small change to the Demo only.

## What changed

- `DemoScenes`: `DemoSceneInfo` has a `Category`; `DemoScenes.Categories` (`DemoCategoryInfo`: id, title) and
  `CategoryIndex`. `All` is regrouped by category: basic_3d, particles, shaders | basic_2d, sprites_2d, tiles_2d,
  navigation_2d | physics_2d, physics_3d | spine, animation_3d | audio_2d, audio_3d, sound_fx, adaptive_music | ui.
  The main scene (basic_3d) stays first.
- `DemoNav`: two bound lists, `categories` and `scenes` (the open category's). The open category follows the current
  scene; a category click (`OpenCategory`) opens the scene last open in it, else its first. Number keys 1–9 open
  categories (they opened the first ten scenes; 0 is gone). Page Down/Up and RB/LB still step every scene, crossing
  categories. `ActiveCategory` and `ShownSceneIds` for tests.
- `Assets/Nav/nav.rml`/`.rcss`: a 40 dp top row (logo, category tabs, then FPS with a fixed 64 dp width, language,
  gear, F12) and a 32 dp scene row of underlined tabs aligned under the first category. No bold on the active tab,
  so labels do not shift when it changes. Scene panels moved from `top: 60dp` to `88dp` (`Assets/UI/demo.rcss`,
  `showcase.rcss`).
- Strings: "3D", "2D", "Physics" are new (`es`: 3D, 2D, Física); "Animation" and "Audio" already had translations.
- Tests: registry order, categories in order and in contiguous runs; `DemoNavTests` for the second row, the
  rendered rows with one active tab each, a category reopening its last scene, every scene reachable from the bar,
  number keys opening categories. 110 Demo tests pass.
- `just demo-screenshots`: all 17 PNGs changed, every one through the bar and the lower panels; inspected, no panel
  clipped at 1280 × 720.
- Docs: `demo.md` (Navigation, the Navigation 2D row, Tests), and the one-line mentions of number keys or tabs in
  `tile-maps.md`, `navigation.md`, `animated-sprites.md`, `cameras-and-input.md`, `game-ui.md`, `localization.md`.

## Decisions

- Two rows rather than a dropdown: RmlUi's `select` shows a value, not a menu of actions, and a hand-made popup needs
  its own focus handling. Two rows of plain widget buttons keep `tab-index`/`nav: auto` focus navigation for free.
  Cost: the bar is 72 dp instead of 44, so the panels moved down 28 dp.
- Sprites 2D sits under 2D, not Animation: it is the 2D scenes' hero and the Tiles 2D scene reuses it.
- A single-scene category (UI) still gets a scene row, so the layout does not jump between categories.

## Follow-ups

- `feature/g9-10-joints` also changes `physics_2d.png` and the Demo's `.pot`/`.po`/`.mo`: whichever lands second
  re-runs `just l10n-extract`/`l10n-compile` and re-takes `physics_2d.png` (and should re-take all of them if it lands
  after this, since the bar is in every image).
