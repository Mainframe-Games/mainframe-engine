# Third-party notices

Mainframe Engine is MIT licensed (see [LICENSE](LICENSE)). It includes, or links at run time to, the third-party
software listed below.

- Keep these notices with any distribution of the engine, the editor, or a game built with it.
- Credits screens must show the FreeType credit, because the FreeType License has an advertising clause. The engine
  ships a ready-made credits document, `Content/UI/credits.rml` (the Sandbox's **Credits** button shows it).

| Component | Version | Licence | Where | Shipped in |
|---|---|---|---|---|
| [FreeType](https://freetype.org) | 2.14.3 | FreeType License (FTL), chosen over GPLv2 | `Native/RmlUi/external/freetype` (submodule) | `mfrmlui` native library (static) |
| zlib (bundled in FreeType's gzip module) | 1.3.1 | zlib | inside FreeType | `mfrmlui` native library (static) |
| [RmlUi](https://github.com/mikke89/RmlUi) | 6.3 | MIT | `Native/RmlUi/external/RmlUi` (submodule) | `mfrmlui` native library (static) |
| robin_hood, itlib (bundled in RmlUi Core) | — | MIT | inside RmlUi | `mfrmlui` native library (static) |
| Courier Prime Code font (embedded in the RmlUi Debugger) | — | SIL OFL 1.1 | inside RmlUi | `mfrmlui` native library (static) |
| [Lato](http://www.latofonts.com/) (Latin subset: regular, bold, italic) | 2.0 | SIL OFL 1.1 | `MainframeEngine/Content/UI/fonts` (from RmlUi's samples) | engine `Content/UI/fonts` |
| [Roboto Mono](https://github.com/googlefonts/robotomono) (regular) | — | SIL OFL 1.1 | `MainframeEngine/Content/UI/fonts` (from RmlUi's samples) | engine `Content/UI/fonts` |
| [ENet (nxrighthere fork)](https://github.com/nxrighthere/ENet-CSharp) | 2.4.8 | MIT | `Native/ENet/upstream` (submodule) | `enet` native library; managed `ENet-CSharp` NuGet package |
| [Spine Runtimes (spine-csharp)](https://github.com/EsotericSoftware/spine-runtimes) | — | Spine Runtimes License | `Plugins/Spine` (submodule) | engine assemblies |
| [RmlUi.Net](https://github.com/PourrezJ/RmlUi.Net) (design reference only, no code copied) | — | MIT | — | nothing (see `Native/RmlUi/shim/NOTICE.md`) |
| [SoundFlow](https://github.com/LSXPrime/SoundFlow) | 1.4.1 | MIT | NuGet package `SoundFlow` | `SoundFlow.dll`; its `miniaudio` native library (SoundFlow's C shim, MIT) |
| [miniaudio](https://miniaud.io) (David Reid) | bundled in SoundFlow 1.4.1 | Unlicense or MIT No Attribution (dual; SoundFlow reproduces it under MIT terms) | inside SoundFlow's native library | `libminiaudio.dylib` / `libminiaudio.so` / `miniaudio.dll` |
| [NVorbis](https://github.com/NVorbis/NVorbis) | 0.10.5 | MIT | NuGet package `NVorbis` | `NVorbis.dll` |

Managed NuGet dependencies (Silk.NET, ImGui.NET, StbImageSharp, Steamworks.NET, Jitter2 and Box2D.NET — both MIT, …) carry their own licence files in
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
