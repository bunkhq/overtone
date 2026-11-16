"""Render the Discord Rich Presence art assets into design/discord-assets/.

    python scripts/gen-discord-assets.py

Upload every PNG to your Discord application (Rich Presence > Art Assets) with
its file name (without .png) as the key; see docs/DISCORD.md. The player glyphs
come from Simple Icons (CC0; the marks themselves remain their owners'
trademarks and are used only to say which player is playing).
"""
import base64, os, shutil, subprocess, sys, tempfile, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "design" / "discord-assets"
SIMPLE_ICONS = "https://cdn.jsdelivr.net/npm/simple-icons@13.21.0/icons/{}.svg"

# asset key -> (Simple Icons slug, brand colour). Keys match DiscordPresence.Service().
PLAYERS = {
    "spotify": ("spotify", "1DB954"),
    "applemusic": ("applemusic", "FA243C"),
    "tidal": ("tidal", "000000"),
    "soundcloud": ("soundcloud", "FF5500"),
    "amazonmusic": ("amazonmusic", "1E3A5F"),
    "youtubemusic": ("youtubemusic", "FF0000"),
    "chrome": ("googlechrome", "4285F4"),
    "firefox": ("firefoxbrowser", "FF7139"),
    "opera": ("opera", "FF1B2D"),
    "brave": ("brave", "FB542B"),
    "vlc": ("vlcmediaplayer", "FF8800"),
    "foobar2000": ("foobar2000", "000000"),
    "winamp": ("winamp", "F93821"),
    "mpv": ("mpv", "691F69"),
}
BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
]
SIZE = 512


def browser() -> str:
    for b in BROWSERS:
        if os.path.exists(b):
            return b
    return shutil.which("chrome") or shutil.which("msedge") or sys.exit("Need Chrome or Edge.")


def render(html: str, out: Path) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        page = Path(tmp, "a.html")
        page.write_text(html, encoding="utf-8")
        subprocess.run([browser(), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
                        "--default-background-color=00000000", f"--window-size={SIZE},{SIZE}", f"--screenshot={out}", page.as_uri()],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for key, (slug, hex_) in PLAYERS.items():
        req = urllib.request.Request(SIMPLE_ICONS.format(slug), headers={"User-Agent": "Overtone asset build"})
        svg = urllib.request.urlopen(req, timeout=20).read().decode()
        svg = svg.replace("<svg ", '<svg fill="#fff" ', 1)
        data = base64.b64encode(svg.encode()).decode()
        # Discord draws the small image in a circle, so the glyph sits on a full disc.
        html = (f"<!doctype html><body style='margin:0;background:transparent'>"
                f"<div style='width:{SIZE}px;height:{SIZE}px;border-radius:50%;background:#{hex_};display:grid;place-items:center'>"
                f"<img src='data:image/svg+xml;base64,{data}' style='width:{int(SIZE * 0.56)}px;height:{int(SIZE * 0.56)}px'></div></body>")
        render(html, OUT / f"{key}.png")
        print("wrote", key)
    # Large-image fallback when no cover URL is found: the app icon.
    icon = base64.b64encode((ROOT / "design" / "icon" / "overtone-app.svg").read_bytes()).decode()
    render(f"<!doctype html><body style='margin:0;background:transparent'><img src='data:image/svg+xml;base64,{icon}' "
           f"style='width:{SIZE}px;height:{SIZE}px;display:block'></body>", OUT / "overtone.png")
    print("wrote overtone")


if __name__ == "__main__":
    main()
