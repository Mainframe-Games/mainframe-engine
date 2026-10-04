# mfrmlui shim — provenance and attribution

The plan (docs/design/future/game-ui.md) was to fork the C shim (`RmlUi.Native`) of
[PourrezJ/RmlUi.Net](https://github.com/PourrezJ/RmlUi.Net) (itself a fork of
chicken-with-lips/rmlui.net and Chorizite/RmlUi.Net). Its licence was checked on 2026-10-05
(commit `dc27d65`): **MIT**, so forking would have been allowed.

The shim was nevertheless **written in-house**, against the RmlUi 6.3 API. No source file was copied. The
upstream shim's ABI did not meet our requirements:

- It passes C++ types across the boundary (`Rml::Vector2i&`, `Rml::Rectanglei`, `Rml::Event&`, `bool`, …).
- It has no public C header, no ABI versioning and no exception barrier.
- It does not null-check handles.
- It has a separate export per C++ class method rather than a flat API.
- It targets RmlUi 6.2, and lacks the clip-mask, layer, filter and shader callbacks.

See memory/decisions/0002-rmlui-native-shim.md.

Some ideas came from RmlUi.Net:

- the callback-forwarding interface proxies;
- passing variants as opaque handles;
- the FreeType CMake set-up (seeding the `FT_DISABLE_*` options with `FORCE` so a universal macOS build does not
  pick up single-architecture Homebrew dependencies).

Its licence is reproduced here as a courtesy:

```
MIT License

Copyright (c) 2022 chicken-with-lips
Copyright (c) 2024 K. 'ashi/eden' J.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
