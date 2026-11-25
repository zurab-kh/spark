# Renders the Spark logo (same geometry as spark-logo.svg) for macOS:
#   src/Spark.Mac/macos/spark.icns    - app icon on the macOS grid (824 px tile in a 1024 canvas, soft shadow)
#   src/Spark.Mac/Assets/tray-template.png - menu bar template image (black bolt on transparency)
# Usage: python tools/branding/make-mac-assets.py
import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
BOLT = [(58, 14), (27, 55), (47, 55), (40, 86), (73, 42), (53, 42)]
STOPS = [(0.0, (0xFF, 0xB3, 0x00)), (0.5, (0xFF, 0x6A, 0x1A)), (1.0, (0xFF, 0x2D, 0x7A))]


def gradient(size):
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            for (t0, c0), (t1, c1) in zip(STOPS, STOPS[1:]):
                if t <= t1:
                    k = (t - t0) / (t1 - t0)
                    px[x, y] = tuple(round(a + (b - a) * k) for a, b in zip(c0, c1))
                    break
    return img


def app_icon(canvas=1024):
    tile, offset = 824, 100
    img = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    shadow = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        (offset, offset + 12, offset + tile, offset + tile + 12), radius=185, fill=(0, 0, 0, 90))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(14)))
    mask = Image.new("L", (tile, tile), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, tile - 1, tile - 1), radius=185, fill=255)
    face = gradient(tile).convert("RGBA")
    face.putalpha(mask)
    ImageDraw.Draw(face).polygon([(x * tile / 100, y * tile / 100) for x, y in BOLT], fill="white")
    img.alpha_composite(face, (offset, offset))
    return img


def icns(master):
    # PNG-encoded entries: ic07 128, ic08 256, ic09 512, ic10 1024 (512@2x), ic11 32 (16@2x), ic12 64 (32@2x),
    # ic13 256 (128@2x), ic14 512 (256@2x).
    chunks = b""
    for kind, size in [("ic11", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256), ("ic08", 256),
                       ("ic14", 512), ("ic09", 512), ("ic10", 1024)]:
        buf = io.BytesIO()
        master.resize((size, size), Image.LANCZOS).save(buf, "PNG")
        data = buf.getvalue()
        chunks += kind.encode() + struct.pack(">I", len(data) + 8) + data
    return b"icns" + struct.pack(">I", len(chunks) + 8) + chunks


def tray_template(size=64):
    big = Image.new("RGBA", (size * 4, size * 4), (0, 0, 0, 0))
    s = size * 4 / 100
    # Bolt slightly enlarged, as in the Windows tray icon, so it stays legible at menu bar size.
    points = [((50 + (x - 50) * 1.12) * s, (50 + (y - 50) * 1.12) * s) for x, y in BOLT]
    ImageDraw.Draw(big).polygon(points, fill=(0, 0, 0, 255))
    return big.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    mac = ROOT / "src" / "Spark.Mac"
    (mac / "macos").mkdir(parents=True, exist_ok=True)
    (mac / "Assets").mkdir(parents=True, exist_ok=True)
    (mac / "macos" / "spark.icns").write_bytes(icns(app_icon()))
    tray_template().save(mac / "Assets" / "tray-template.png")
    print("ok")
