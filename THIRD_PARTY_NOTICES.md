# Third-party notices

Mainframe Engine's own code is MIT licensed (see [LICENSE](LICENSE)). It includes, or links at run time to, the
third-party software and assets listed below, each under its own licence. **Not everything in this repository is MIT:**
the Spine Runtimes (`Plugins/Spine`) and the Spine example assets are Esoteric Software's (see
[Spine Runtimes](#spine-runtimes) and [Spine example assets](#spine-example-assets)).

- Keep these notices with any distribution of the engine, the editor, or a game built with it.
- Credits screens must show the FreeType credit, because the FreeType License has an advertising clause. The engine
  ships a ready-made credits document, `Content/UI/credits.rml` (load it from a **Credits** button in your game's UI).

| Component | Version | Licence | Where | Shipped in |
|---|---|---|---|---|
| [FreeType](https://freetype.org) | 2.14.3 | FreeType License (FTL), chosen over GPLv2 | `Native/RmlUi/external/freetype` (submodule) | `mfrmlui` native library (static) |
| zlib (bundled in FreeType's gzip module) | 1.3.1 | zlib | inside FreeType | `mfrmlui` native library (static) |
| [RmlUi](https://github.com/mikke89/RmlUi) | 6.3 | MIT | `Native/RmlUi/external/RmlUi` (submodule) | `mfrmlui` native library (static) |
| [ThorVG](https://github.com/thorvg/thorvg) (the copy vendored by Godot 4.7.2, `thirdparty/thorvg`) | 1.0.3 | MIT | `Native/Svg/thorvg` | `mfsvg` native library (static) |
| [Godot Engine](https://godotengine.org) (algorithms ported to C#: canvas ordering/tessellation, Camera2D, window stretch, SVG loading, `fix_alpha_edges`; GodotSharp's C# math: `Mathf`, `Vector2I`, `Rect2I`, `Color`/`Colors`, the `Vector2` helpers) | 4.7.2 | MIT | `MainframeEngine/Src/Scene/Canvas`, `Rendering/Canvas`, `Scene/ContentScale.cs`, `Imaging/Svg.cs`, `Math/` | engine |
| robin_hood, itlib (bundled in RmlUi Core) | — | MIT | inside RmlUi | `mfrmlui` native library (static) |
| Courier Prime Code font (embedded in the RmlUi Debugger) | — | SIL OFL 1.1 | inside RmlUi | `mfrmlui` native library (static) |
| [Lato](http://www.latofonts.com/) (Latin subset: regular, bold, italic) | 2.0 | SIL OFL 1.1 | `MainframeEngine/Content/UI/fonts` (from RmlUi's samples) | engine `Content/UI/fonts` |
| [Roboto Mono](https://github.com/googlefonts/robotomono) (regular) | — | SIL OFL 1.1 | `MainframeEngine/Content/UI/fonts` (from RmlUi's samples) | engine `Content/UI/fonts` |
| [ENet (nxrighthere fork)](https://github.com/nxrighthere/ENet-CSharp) | 2.4.8 | MIT | `Native/ENet/upstream` (submodule) | `enet` native library; managed `ENet-CSharp` NuGet package |
| [GetText.NET](https://github.com/perpetualKid/GetText.NET) (fork of NGettext) | 10.0.1 | MIT | NuGet package `GetText.NET` | engine assemblies (runtime `.mo` catalogs and plural rules) |
| [GetText.NET.Extractor](https://github.com/perpetualKid/GetText.NET) | 10.0.1 | MIT | local dotnet tool (`.config/dotnet-tools.json`) | nothing: development tool for C# string extraction |
| [Spine Runtimes (spine-csharp)](https://github.com/EsotericSoftware/spine-runtimes) | — | Spine Runtimes License | `Plugins/Spine` (submodule) | engine assemblies |
| [RmlUi.Net](https://github.com/PourrezJ/RmlUi.Net) (design reference only, no code copied) | — | MIT | — | nothing (see `Native/RmlUi/shim/NOTICE.md`) |
| [SoundFlow](https://github.com/LSXPrime/SoundFlow) | 1.4.1 | MIT | NuGet package `SoundFlow` | `SoundFlow.dll`; its `miniaudio` native library (SoundFlow's C shim, MIT) |
| [miniaudio](https://miniaud.io) (David Reid) | bundled in SoundFlow 1.4.1 | Unlicense or MIT No Attribution (dual; SoundFlow reproduces it under MIT terms) | inside SoundFlow's native library | `libminiaudio.dylib` / `libminiaudio.so` / `miniaudio.dll` |
| [NVorbis](https://github.com/NVorbis/NVorbis) | 0.10.5 | MIT | NuGet package `NVorbis` | `NVorbis.dll` |
| [libogg](https://github.com/xiph/ogg) | 1.3.6 | BSD-3-Clause | `Native/PluginHost/external/ogg` (submodule) | editor only: `mfplughost` helper executable (static) |
| [libvorbis / libvorbisenc / libvorbisfile](https://github.com/xiph/vorbis) | 1.3.7 | BSD-3-Clause | `Native/PluginHost/external/vorbis` (submodule) | editor only: `mfplughost` helper executable (static) |
| [VST 3 SDK](https://github.com/steinbergmedia/vst3sdk) (base, pluginterfaces, public.sdk hosting; no VSTGUI) | 3.8.1 | MIT | `Native/PluginHost/external/vst3sdk` (submodule) | editor only: `mfplughost` helper executable (static) |
| [RtMidi](https://github.com/thestk/rtmidi) (Gary P. Scavone) | 6.0.0 | MIT (with a non-binding request to send modifications upstream) | `Native/PluginHost/external/rtmidi` (submodule) | editor only: `mfplughost` helper executable (static; CoreMIDI / WinMM / ALSA) |
| [ZzFX](https://github.com/KilledByAPixel/ZzFX) (Frank Force; `buildSamples` ported to C#) | 1.4.0 | MIT | `MainframeEngine/Src/Audio/Synthesis/Zzfx.cs`; `build/zzfx-reference.mjs` (vendored JavaScript, reference vectors) | engine |
| [Ez Tree](https://github.com/dgreenheck/ez-tree) (Daniel Greenheck; the tree generator `tree.js` and `rng.js` ported to C#, the 15 tree and bush presets, the leaf textures) | 1.1.0 (`dcf309b`) | MIT | `MainframeEngine/Src/Trees/Generation/`; `MainframeEngine/Content/Trees/Presets` (converted to `.mres`), `MainframeEngine/Content/Trees/Leaves`; `build/ez-tree-reference.mjs` (runs Ez Tree for the parity fixture) | engine; engine `Content/Trees` |
| [three.js](https://threejs.org) (the `Vector3`, `Quaternion` and `Euler` math Ez Tree calls, ported to C# in doubles) | 0.167.1 | MIT | `MainframeEngine/Src/Trees/Generation/ThreeMath.cs` | engine |
| [ambientCG](https://ambientcg.com) bark textures `Bark001`–`Bark003` (1K JPG: colour, OpenGL normal, roughness; the sets Ez Tree's presets use) | — | CC0 1.0 | `MainframeEngine/Content/Trees/Bark` | engine `Content/Trees/Bark` |
| [Tabler Icons](https://tabler.io/icons) (only the icons the editor uses) | 3.48.0 | MIT | `MainframeEngine.Editor/Icons/tabler` (SVGs, from the `@tabler/icons` npm package) | editor `Content/icons` (rasterized atlas) |
| Spine example skeleton, atlas and texture (spineboy) | — | © Esoteric Software; Spine Runtimes License / Spine Editor License, **not MIT** | `Examples/Demo/Content/Models/Spine/SpineBoy`, `Tests/Content/Models/Spine/SpineBoy` | Demo and tests only (see [below](#spine-example-assets)) |
| [Poly Haven](https://polyhaven.com) sky panorama (`sky_10_2k.png`) | — | CC0 1.0 | `Examples/Demo/Content/Sky`, `Tests/Content/Sky` | Demo and tests only |

Managed NuGet dependencies (Silk.NET, StbImageSharp, Steamworks.NET, Jitter2 and Box2D.NET — both MIT, …) carry their own licence files in
their packages and are not repeated here.

## FreeType

The `mfrmlui` native library uses the FreeType font engine under the **FreeType License (FTL)** (`docs/FTL.TXT` in
the FreeType sources). The FTL requires this credit in the documentation of products that use it:

> Portions of this software are copyright © 2026 The FreeType Project (https://freetype.org). All rights reserved.

FreeType's bundled zlib (used for compressed fonts) is under the zlib licence:

```text
Copyright (C) 1995-2024 Jean-loup Gailly and Mark Adler

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
claim that you wrote the original software. If you use this software
in a product, an acknowledgment in the product documentation would be
appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.

Jean-loup Gailly        Mark Adler
jloup@gzip.org          madler@alumni.caltech.edu
```

## GetText.NET

Applies to the `GetText.NET` runtime package and the `GetText.NET.Extractor` tool.

```text
The MIT License (MIT)

Original Source Code NGettext
Copyright (c) 2012 Vitaly Zilnik

GetText.NET including updates and additions (WindowsForms, Extractor)
Copyright (c) 2020 perpetualKid

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

## RmlUi

```text
MIT License

Copyright (c) 2008-2014 CodePoint Ltd, Shift Technology Ltd, and contributors
Copyright (c) 2019-2026 The RmlUi Team, and contributors

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

### Third-party code in RmlUi Core

```text
RmlUi Core contains thirdparty libraries. They are listed below along with
their full licenses - all MIT licensed.

---

robin_hood unordered map & set
https://github.com/martinus/robin-hood-hashing

Licensed under the MIT License <http://opensource.org/licenses/MIT>.
SPDX-License-Identifier: MIT
Copyright (c) 2018-2020 Martin Ankerl <http://martin.ankerl.com>

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

---

itlib
https://github.com/iboB/itlib

MIT License

Copyright(c) 2016-2019 Chobolabs Inc.
Copyright(c) 2020-2023 Borislav Stanimirov

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files(the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and / or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions :

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT.IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### Font embedded in the RmlUi Debugger

```text
RmlUi Debugger includes thirdparty font assets. They are listed below along
with their full license.

---

'Courier Prime Code' and 'Courier Prime Code Italic'.

Copyright (c) 2013, Quote-Unquote Apps (http://quoteunquoteapps.com),
with Reserved Font Name Courier Prime.

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL


SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

## UI fonts (Lato, Roboto Mono)

The game UI's bundled fonts are licensed under the SIL Open Font License 1.1; the full licence texts ship next to the
fonts, in `MainframeEngine/Content/UI/fonts/OFL-Lato.txt` and `OFL-RobotoMono.txt`.

- Lato: Copyright (c) 2010-2015, Łukasz Dziedzic (dziedzic@typoland.com), with Reserved Font Name Lato.
- Roboto Mono: Copyright 2015 The Roboto Mono Project Authors (https://github.com/googlefonts/robotomono).

The OFL allows bundling and redistribution with software; the fonts may not be sold on their own, and modified
versions may not use the reserved font names.

## ThorVG

The SVG rasteriser `mfsvg` statically links ThorVG 1.0.3 (the exact sources Godot 4.7.2 vendors in `thirdparty/thorvg`).

```
MIT License

Copyright (c) 2020 - 2026 ThorVG Project

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

## Godot Engine

Parts of the 2D canvas, camera, window stretch and image code are ports of Godot Engine 4.7.2 (C++ → C#). `MainframeEngine/Src/Math/` holds GodotSharp's C# math types from the same release (`Mathf`, `Vector2I`, `Rect2I`, `Color`, `Colors`, `Side`; the `Vector2` members as extensions on `System.Numerics.Vector2`).

```
Copyright (c) 2014-present Godot Engine contributors (see AUTHORS.md).
Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## ENet

ENet library (C), from ENet-CSharp `Source/Native/enet.h` and `enet.c`:

```text
ENet reliable UDP networking library
Copyright (c) 2018 Lee Salzman, Vladyslav Hrytsenko, Dominik Madarász, Stanislav Denisov

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

ENet-CSharp:

```text
MIT License

Copyright (c) 2018 Stanislav Denisov (nxrighthere@gmail.com)

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

## Spine Runtimes

`Plugins/Spine` (spine-csharp) is distributed under the Spine Runtimes License Agreement. The text below is
reproduced from its source file headers. **Every user of a product that integrates the Spine Runtimes must hold
their own Spine Editor licence.**

```text
Spine Runtimes License Agreement
Last updated July 28, 2023. Replaces all prior versions.

Copyright (c) 2013-2023, Esoteric Software LLC

Integration of the Spine Runtimes into software or otherwise creating
derivative works of the Spine Runtimes is permitted under the terms and
conditions of Section 2 of the Spine Editor License Agreement:
http://esotericsoftware.com/spine-editor-license

Otherwise, it is permitted to integrate the Spine Runtimes into software or
otherwise create derivative works of the Spine Runtimes (collectively,
"Products"), provided that each user of the Products must obtain their own
Spine Editor license and redistribution of the Products in any form must
include this license and copyright notice.

THE SPINE RUNTIMES ARE PROVIDED BY ESOTERIC SOFTWARE LLC "AS IS" AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL ESOTERIC SOFTWARE LLC BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES,
BUSINESS INTERRUPTION, OR LOSS OF USE, DATA, OR PROFITS) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THE
SPINE RUNTIMES, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Spine example assets

© Esoteric Software LLC. These are Spine example skeletons (`.json`), atlases (`.atlas`) and textures (`.png`) from the
[Spine Runtimes](https://github.com/EsotericSoftware/spine-runtimes) examples, provided under the
[Spine Runtimes License](https://esotericsoftware.com/spine-runtimes-license) for evaluating and demonstrating the
runtimes. **They are not covered by this repository's MIT licence**; using them (or the Spine Runtimes) in your own
product requires a [Spine Editor licence](http://esotericsoftware.com/spine-editor-license). Copies in this repository:

- `Examples/Demo/Content/Models/Spine/SpineBoy/spineboy-pro.{atlas,json,png}` (the Demo's Spine scene) and
  `Tests/Content/Models/Spine/SpineBoy/spineboy-pro.{atlas,json,png}` (linked into `Tests/MainframeEngine.Tests` and
  `Tests/MainframeEngine.RenderTests.Host`; the `spine*` render-test goldens show it)

## Poly Haven sky panoramas

The panoramic sky `sky_10_2k.png` (in `Examples/Demo/Content/Sky/` and `Tests/Content/Sky/`)
is one of the Poly Haven sky panoramas (CC0), converted to an 8-bit 4096×2048 PNG. Poly Haven assets are released
under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (public domain dedication; see
https://polyhaven.com/license): no attribution is required, and it is given here as a courtesy.

## SoundFlow

From the `SoundFlow` 1.4.1 package (`LICENSE.md`; its `SOUNDFLOW-THIRD-PARTY-NOTICES.txt` also lists PortMidi,
WebRTC APM and FFmpeg, which belong to optional SoundFlow extensions the engine does not use or ship):

```text
MIT License

Copyright (c) 2025 LSXPrime

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the “Software”), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## miniaudio

Compiled into SoundFlow's native library. miniaudio is dual-licensed (public domain via the Unlicense, or MIT No
Attribution); SoundFlow's notice reproduces it under MIT terms:

```text
Copyright (c) David Reid

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

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

## NVorbis

From the `NVorbis` 0.10.5 package (`LICENSE`):

```text
MIT License

Copyright (c) 2020 Andrew Ward

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

## libogg and libvorbis

The editor's music helper `mfplughost` (not shipped with games) statically links libogg 1.3.6 and libvorbis 1.3.7
(including libvorbisenc and libvorbisfile) for Ogg Vorbis encoding. Both use the same BSD-3-Clause licence; libogg is
"Copyright (c) 2002, Xiph.org Foundation", libvorbis "Copyright (c) 2002-2020 Xiph.org Foundation".

```
Copyright (c) 2002-2020 Xiph.org Foundation

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## ZzFX

`MainframeEngine/Src/Audio/Synthesis/Zzfx.cs` ports `ZZFX.buildSamples` from ZzFX 1.4.0 (`ZzFX.js`, commit
`751f17139d689b1320f0c4173031b038959e4bf8`); `build/zzfx-reference.mjs` vendors that function to generate test vectors:

```text
ZzFX MIT License

Copyright (c) 2019 - Frank Force

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

## Ez Tree

`MainframeEngine/Src/Trees/Generation/` ports Ez Tree's generator (`src/lib/tree.js`, `rng.js`) from Ez Tree 1.1.0
(commit `dcf309bd86bd521083d9c70f01f2de45fdc7c457`). The tree and bush presets in `MainframeEngine/Content/Trees/Presets`
are its `src/lib/presets/*.json` converted to `.mres`, and the leaf textures in `MainframeEngine/Content/Trees/Leaves`
(`ash`, `aspen`, `oak`, `pine`) are its demo app's `src/app/public/textures/leaves`. The leaf textures are under this
MIT licence, not CC0: keep this notice wherever they are copied (also `MainframeEngine/Content/Trees/LICENSE.md`).

```text
MIT License

Copyright (c) 2024 Daniel Greenheck

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

## three.js

`MainframeEngine/Src/Trees/Generation/ThreeMath.cs` ports the three.js 0.167.1 math that Ez Tree's generator calls
(`Vector3`, `Quaternion`, `Euler` and `Matrix4.compose`), so the port computes what three.js computes:

```text
The MIT License

Copyright © 2010-2024 three.js authors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## ambientCG bark textures

The bark texture sets in `MainframeEngine/Content/Trees/Bark` (`Bark001_1K-JPG`, `Bark002_1K-JPG`, `Bark003_1K-JPG`:
`_Color`, `_NormalGL`, `_Roughness`) come from [ambientCG](https://ambientcg.com), by way of Ez Tree's demo app. They are
released under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (public domain dedication): no
attribution is required, and it is given here as a courtesy:
[Bark001](https://ambientcg.com/view?id=Bark001), [Bark002](https://ambientcg.com/view?id=Bark002),
[Bark003](https://ambientcg.com/view?id=Bark003).

## Tabler Icons

The editor's icons are [Tabler Icons](https://tabler.io/icons) 3.48.0 (the `@tabler/icons` npm package): the SVGs the
editor uses are vendored in `MainframeEngine.Editor/Icons/tabler` and rasterized into `Content/icons` by
`just editor-icons`. From the package's `LICENSE`:

```text
MIT License

Copyright (c) 2020-2026 Paweł Kuna

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

## VST 3 SDK

The editor's music helper `mfplughost` (not shipped with games) statically links the hosting parts of Steinberg's VST 3
SDK 3.8.1 (`base`, `pluginterfaces`, `public.sdk/source/vst/hosting`; VST is a registered trademark of Steinberg Media
Technologies GmbH). From `Native/PluginHost/external/vst3sdk/LICENSE.txt`:

```
MIT License

Copyright (c) 2026, Steinberg Media Technologies GmbH

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

## RtMidi

The editor's music helper `mfplughost` (not shipped with games) statically links RtMidi 6.0.0 for MIDI keyboard input.
From `Native/PluginHost/external/rtmidi/LICENSE`:

```

RtMidi: realtime MIDI i/o C++ classes
Copyright (c) 2003-2023 Gary P. Scavone

Permission is hereby granted, free of charge, to any person
obtaining a copy of this software and associated documentation files
(the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software,
and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

Any person wishing to distribute modifications to the Software is
asked to send the modifications to the original developer so that
they can be incorporated into the canonical version.  This is,
however, not a binding provision of this license.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
