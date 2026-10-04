#!/usr/bin/env python3
"""Builds the editor's icon atlas (docs/design/editor.md#icons) from the curated Tabler icon list.

  python3 build/editor-icons/make_atlas.py [--tabler <@tabler/icons package dir>]

Reads   MainframeEngine.Editor/Content/icons/icons.txt     one Tabler name per line ("name" = outline, "name-filled")
        MainframeEngine.Editor/Icons/tabler/{outline,filled}/<name>.svg   the vendored SVGs (only listed ones are kept)
Writes  MainframeEngine.Editor/Content/icons/icons-{16,24,32,48}.png   white glyphs (RCSS image-color tints them)
        MainframeEngine.Editor/Content/icons/icons.rcss               sprite sheets + one class per icon

Every icon is laid out once in a sheet SVG (16-unit cells on a 20-unit pitch, so neighbours never bleed under linear
filtering) and the sheet is rendered by Inkscape at 1x (16 px), 1.5x (24 px, icon-lg), 2x (32 px, HiDPI) and 3x
(48 px, icon-lg on HiDPI). With --tabler, SVGs missing from the vendored folder are copied from an unpacked
@tabler/icons npm package (`npm pack @tabler/icons`). Needs python3 and inkscape; the outputs are committed.
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EDITOR = os.path.join(ROOT, "MainframeEngine.Editor")
LIST = os.path.join(EDITOR, "Content", "icons", "icons.txt")
OUT = os.path.join(EDITOR, "Content", "icons")
VENDOR = os.path.join(EDITOR, "Icons", "tabler")

COLUMNS = 16
CELL = 16  # icon size in sheet units (= px at 1x)
PITCH = 20  # cell pitch: 2 units of transparent gutter on every side
SCALES = [(1, 16), (1.5, 24), (2, 32), (3, 48)]  # (scale, file suffix)

# Classes the editor's markup contract uses as modifiers; an icon may not have one of these names.
RESERVED = {"sm", "lg", "3d", "2d", "ui", "audio", "physics", "net", "logic", "resource", "dir", "missing", "muted", "accent",
            "info", "warn", "error", "debug"}
NAME = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
SVG_NS = "http://www.w3.org/2000/svg"


def read_list():
    names = []
    with open(LIST, encoding="utf-8") as f:
        for number, raw in enumerate(f, 1):
            line = raw.split("#", 1)[0].strip()
            if not line:
                continue
            if not NAME.match(line):
                sys.exit(f"{LIST}:{number}: '{line}' is not a Tabler icon name")
            if line in RESERVED:
                sys.exit(f"{LIST}:{number}: '{line}' collides with a modifier class (icon-{line})")
            if line in names:
                sys.exit(f"{LIST}:{number}: '{line}' is listed twice")
            names.append(line)
    if not names:
        sys.exit(f"{LIST} lists no icons")
    return names


def source_of(name):
    """(style, file name) of an icon: "x-filled" is Tabler's filled "x", everything else is outline."""
    if name.endswith("-filled"):
        return "filled", name[: -len("-filled")] + ".svg"
    return "outline", name + ".svg"


def sync_vendor(names, tabler):
    wanted = set()
    for name in names:
        style, file = source_of(name)
        target = os.path.join(VENDOR, style, file)
        wanted.add(os.path.normpath(target))
        if os.path.exists(target):
            continue
        if tabler is None:
            sys.exit(f"{target} is missing: run with --tabler <unpacked @tabler/icons package> to vendor it")
        source = os.path.join(tabler, "icons", style, file)
        if not os.path.exists(source):
            sys.exit(f"'{name}' is not a Tabler icon ({source} not found)")
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copyfile(source, target)
        print(f"vendored {style}/{file}")
    if tabler is not None:
        shutil.copyfile(os.path.join(tabler, "LICENSE"), os.path.join(VENDOR, "LICENSE"))
    # Keep only what the list uses.
    for style in ("outline", "filled"):
        folder = os.path.join(VENDOR, style)
        if not os.path.isdir(folder):
            continue
        for file in sorted(os.listdir(folder)):
            path = os.path.normpath(os.path.join(folder, file))
            if file.endswith(".svg") and path not in wanted:
                os.remove(path)
                print(f"removed unused {style}/{file}")


def glyph(name, x, y):
    style, file = source_of(name)
    with open(os.path.join(VENDOR, style, file), encoding="utf-8") as f:
        text = f.read().replace("currentColor", "#ffffff")
    root = ET.fromstring(text)
    attributes = {k: v for k, v in root.attrib.items() if k not in ("width", "height", "class", "x", "y")}
    attributes.update({"x": str(x), "y": str(y), "width": str(CELL), "height": str(CELL)})
    if "viewBox" not in attributes:
        attributes["viewBox"] = "0 0 24 24"
    element = ET.Element(f"{{{SVG_NS}}}svg", attributes)
    for child in root:
        element.append(child)
    return element


def write_sheet(names, path):
    rows = (len(names) + COLUMNS - 1) // COLUMNS
    width, height = COLUMNS * PITCH, rows * PITCH
    ET.register_namespace("", SVG_NS)
    sheet = ET.Element(f"{{{SVG_NS}}}svg", {"width": str(width), "height": str(height), "viewBox": f"0 0 {width} {height}"})
    for index, name in enumerate(names):
        x, y = cell(index)
        sheet.append(glyph(name, x, y))
    ET.ElementTree(sheet).write(path, encoding="utf-8", xml_declaration=True)
    return width, height


def cell(index):
    gutter = (PITCH - CELL) // 2
    return (index % COLUMNS) * PITCH + gutter, (index // COLUMNS) * PITCH + gutter


def render(sheet, width, height):
    inkscape = shutil.which("inkscape")
    if inkscape is None:
        sys.exit("inkscape not found (https://inkscape.org)")
    for scale, suffix in SCALES:
        out = os.path.join(OUT, f"icons-{suffix}.png")
        subprocess.run([inkscape, sheet, "--export-type=png", f"--export-filename={out}", f"--export-width={int(width * scale)}",
                        f"--export-height={int(height * scale)}", "--export-background-opacity=0"],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def sprites(names, sheet_name, image, resolution, prefix, scale, indent):
    lines = [f"{indent}@spritesheet {sheet_name} {{", f"{indent}    src: {image};", f"{indent}    resolution: {resolution};"]
    size = int(CELL * scale)
    for index, name in enumerate(names):
        x, y = cell(index)
        lines.append(f"{indent}    {prefix}{name}: {int(x * scale)}px {int(y * scale)}px {size}px {size}px;")
    lines.append(f"{indent}}}")
    return lines


def write_rcss(names):
    lines = [
        "/*",
        " * GENERATED by build/editor-icons/make_atlas.py from icons.txt (`just editor-icons`): do not edit.",
        " * Tabler Icons (MIT, https://tabler.io/icons): see THIRD_PARTY_NOTICES.md.",
        " * Markup: <span class=\"icon icon-NAME\"/>; size, colour and family classes are in Content/Editor/theme.rcss.",
        " */",
        "",
    ]
    lines += sprites(names, "editor-icons", "icons-16.png", "1x", "i-", 1, "")
    lines += sprites(names, "editor-icons-lg", "icons-24.png", "1x", "l-", 1.5, "")
    lines += ["", "/* HiDPI (Retina): the 2x sheets replace the sprites above. */", "@media (min-resolution: 1.5x) {"]
    lines += sprites(names, "editor-icons-2x", "icons-32.png", "2x", "i-", 2, "    ")
    lines += sprites(names, "editor-icons-lg-2x", "icons-48.png", "2x", "l-", 3, "    ")
    lines += ["}", ""]
    for name in names:
        lines.append(f".icon-{name} {{ decorator: image(i-{name}); }}")
    lines.append("")
    for name in names:
        lines.append(f".icon-lg.icon-{name} {{ decorator: image(l-{name}); }}")
    with open(os.path.join(OUT, "icons.rcss"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--tabler", help="unpacked @tabler/icons package (folder holding icons/ and LICENSE)")
    args = parser.parse_args()
    names = read_list()
    sync_vendor(names, args.tabler)
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        sheet = os.path.join(work, "icons.svg")
        width, height = write_sheet(names, sheet)
        render(sheet, width, height)
    write_rcss(names)
    print(f"{len(names)} icons -> {os.path.relpath(OUT, ROOT)}/icons-{{16,24,32,48}}.png, icons.rcss")


if __name__ == "__main__":
    main()
