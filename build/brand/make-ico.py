#!/usr/bin/env python3
"""Packs PNG files into a Windows .ico (PNG-compressed entries, Vista+). Usage: make-ico.py out.ico in1.png in2.png ...

No dependencies: reads each PNG's size from its IHDR chunk and stores the PNG bytes as-is.
"""
import struct
import sys


def png_size(data: bytes) -> tuple[int, int]:
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        raise ValueError("not a PNG file")
    return struct.unpack(">II", data[16:24])


def main(out: str, inputs: list[str]) -> None:
    images = []
    for path in inputs:
        with open(path, "rb") as f:
            data = f.read()
        width, height = png_size(data)
        if width > 256 or height > 256:
            raise ValueError(f"{path}: ICO entries are at most 256x256")
        images.append((width, height, data))
    images.sort(key=lambda i: i[0])

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    directory = b""
    for width, height, data in images:
        # 0 means 256 in the one-byte size fields.
        directory += struct.pack("<BBBBHHII", width % 256, height % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(out, "wb") as f:
        f.write(header + directory + b"".join(i[2] for i in images))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2:])
