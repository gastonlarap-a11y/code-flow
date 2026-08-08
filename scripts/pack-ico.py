"""Packs square PNGs into one multi-resolution .ico.

macOS ships no tool that writes .ico, and the repository adds no npm dependency for one, so the
container is built here. Windows Vista and later accept PNG-compressed entries verbatim, which is
what electron-builder hands to NSIS — the older BMP-with-AND-mask encoding buys nothing on any
Windows this app targets.

Called by `scripts/build-icons.sh`; the sizes come from the file names it passes.
"""

import struct
import sys
from pathlib import Path

ICONDIR = "<HHH"
ICONDIRENTRY = "<BBBBHHII"


def pack(sources: list[Path], destination: Path) -> None:
    images = [path.read_bytes() for path in sources]
    # `ico-48.png` → 48. The caller names them; a stem that is not a size is a bug in the caller.
    sizes = [int(path.stem.rsplit("-", maxsplit=1)[-1]) for path in sources]

    offset = struct.calcsize(ICONDIR) + len(images) * struct.calcsize(ICONDIRENTRY)
    entries = bytearray()
    for size, image in zip(sizes, images, strict=True):
        # 256 is stored as 0: the field is a single byte and 256 does not fit in one.
        dimension = 0 if size >= 256 else size
        entries += struct.pack(ICONDIRENTRY, dimension, dimension, 0, 0, 1, 32, len(image), offset)
        offset += len(image)

    destination.write_bytes(
        struct.pack(ICONDIR, 0, 1, len(images)) + bytes(entries) + b"".join(images)
    )


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("usage: pack-ico.py <out.ico> <png…>", file=sys.stderr)
        raise SystemExit(64)

    out = Path(sys.argv[1])
    pack([Path(argument) for argument in sys.argv[2:]], out)
    print(f"    {out.name} — {out.stat().st_size} bytes, {len(sys.argv) - 2} sizes")
