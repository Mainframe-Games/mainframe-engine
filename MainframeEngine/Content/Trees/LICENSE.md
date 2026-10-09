# Tree content licences

Keep this file with any copy of these folders.

## `Leaves/` and `Presets/`: Ez Tree (MIT)

The leaf textures (`ash.png`, `aspen.png`, `oak.png`, `pine.png`) are from Ez Tree's demo app, and the presets are Ez
Tree's preset JSON converted to `.mres` (https://github.com/dgreenheck/ez-tree, commit
`dcf309bd86bd521083d9c70f01f2de45fdc7c457`). They are under Ez Tree's MIT licence, **not** CC0:

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

## `Bark/`: ambientCG (CC0 1.0)

The bark sets are from [ambientCG](https://ambientcg.com), released under
[CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (public domain). Attribution is not required and is given
as a courtesy:

| Folder | Source |
|---|---|
| `Bark001_1K-JPG/` | https://ambientcg.com/view?id=Bark001 |
| `Bark002_1K-JPG/` | https://ambientcg.com/view?id=Bark002 |
| `Bark003_1K-JPG/` | https://ambientcg.com/view?id=Bark003 |

## Procedural leaves and bark (ADR 0172): this repository's licence

`Leaves/birch.png`, `Leaves/beech.png`, `Leaves/spruce.png`, `Leaves/fir.png` and the `Bark/Birch/` and `Bark/Beech/`
sets were drawn by the engine itself (`TreeTexturePainter.WriteContent`, `MainframeEngine/Src/Trees/TreeTexturePainter.cs`),
from no third-party image. They are under the engine's own licence; regenerate them with the painter rather than
editing them. The birch, spruce, fir and beech presets in `Presets/` are the engine's (ADR 0172), not Ez Tree's.
