# Third-party assets in the Demo

The Demo's code, scenes, UI, localization catalogs, the checker test model, the ambient audio loop, the Basic 2D logo and
the project icon belong to Mainframe Engine and are MIT licensed (see the engine's `LICENSE`). Two kinds of asset in
`Content/` are **not** MIT:

| Asset | Where | Licence |
|---|---|---|
| Spine example skeleton, atlas and texture (spineboy) | `Content/Models/Spine/SpineBoy/` | © Esoteric Software LLC. [Spine Runtimes License](https://esotericsoftware.com/spine-runtimes-license): provided for evaluating and demonstrating the Spine runtimes. Using it, or the Spine runtimes, in your own product requires a [Spine Editor licence](http://esotericsoftware.com/spine-editor-license). |
| Sky panorama `sky_10_2k.png` (a [Poly Haven](https://polyhaven.com) sky, converted to an 8-bit 4096×2048 PNG) | `Content/Sky/` | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (public domain; no attribution required, given as a courtesy). |

If you build your own game from the Demo, remove the Spine example (the Spine scene, its panel and
`Content/Models/Spine/`) unless you hold a Spine licence.

The complete list of third-party components the engine itself uses, with their licences, is the engine repository's
[THIRD_PARTY_NOTICES.md](https://github.com/Mainframe-Games/mainframe-engine/blob/main/THIRD_PARTY_NOTICES.md).
