"""Rasterise the SVG icon masters to Windows .ico files.

Renders each master with headless Chrome or Edge (no extra Python deps beyond
Pillow), then packs the PNGs into multi-resolution .ico files.

    python scripts/gen-ico.py

Writes apps/windows/Overtone/Assets/{overtone.ico, tray.ico}.
"""
import base64, io, os, shutil, subprocess, sys, tempfile
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
ICON = ROOT / "design" / "icon"
OUT = ROOT / "apps" / "windows" / "Overtone" / "Assets"

BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]

def browser() -> str:
    for b in BROWSERS:
        if os.path.exists(b):
            return b
    found = shutil.which("chrome") or shutil.which("msedge")
    if not found:
        sys.exit("Need Chrome or Edge to rasterise SVGs.")
    return found

def render(svg: Path, sizes: list[int]) -> dict[int, Image.Image]:
    """One sheet, each size at a known x offset, rendered at 1x and cropped."""
    data = base64.b64encode(svg.read_bytes()).decode()
    gap, x, cells = 8, 0, []
    for s in sizes:
        cells.append((x, s))
        x += s + gap
    width, height = max(x, 520), max(sizes) + 2  # headless enforces a min width
    imgs = "".join(
        f'<img src="data:image/svg+xml;base64,{data}" style="position:absolute;left:{cx}px;top:0;width:{s}px;height:{s}px">'
        for cx, s in cells
    )
    html = f"<!doctype html><html><body style='margin:0;background:transparent'>{imgs}</body></html>"
    with tempfile.TemporaryDirectory() as tmp:
        page, shot = Path(tmp, "sheet.html"), Path(tmp, "sheet.png")
        page.write_text(html, encoding="utf-8")
        subprocess.run([browser(), "--headless=new", "--disable-gpu", "--hide-scrollbars",
                        "--force-device-scale-factor=1", "--default-background-color=00000000",
                        f"--window-size={width},{height}", f"--screenshot={shot}", page.as_uri()],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        sheet = Image.open(shot).convert("RGBA")
        return {s: sheet.crop((cx, 0, cx + s, s)).copy() for cx, s in cells}

def write_ico(path: Path, images: dict[int, Image.Image]) -> None:
    """ICO with PNG-compressed entries (supported since Windows Vista)."""
    entries = []
    for size in sorted(images):
        buf = io.BytesIO()
        images[size].save(buf, "PNG")
        entries.append((size, buf.getvalue()))
    header = (0).to_bytes(2, "little") + (1).to_bytes(2, "little") + len(entries).to_bytes(2, "little")
    offset = 6 + 16 * len(entries)
    directory, blobs = b"", b""
    for size, png in entries:
        dim = 0 if size >= 256 else size
        directory += bytes([dim, dim, 0, 0]) + (1).to_bytes(2, "little") + (32).to_bytes(2, "little")
        directory += len(png).to_bytes(4, "little") + offset.to_bytes(4, "little")
        offset += len(png)
        blobs += png
    path.write_bytes(header + directory + blobs)

def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    small = render(ICON / "overtone-app-small.svg", [16, 20, 24, 32])
    large = render(ICON / "overtone-app.svg", [40, 48, 64, 256])
    write_ico(OUT / "overtone.ico", {**small, **large})
    large[256].save(OUT / "overtone-256.png")
    write_ico(OUT / "tray.ico", render(ICON / "overtone-tray.svg", [16, 20, 24, 32, 40, 48]))
    print("wrote", OUT / "overtone.ico", OUT / "tray.ico")

if __name__ == "__main__":
    main()
