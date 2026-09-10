#!/usr/bin/env python3
"""Regenerate every icon the two apps ship, from the geometry below.

    pip install cairosvg pillow
    python3 assets/make-icons.py

Two marks in one vocabulary: a line that describes signal, with a fader cap on
it.  FaderBridge is two fader tracks tied by a span - a surface and a console,
linked, each end carrying the other's move.  Feedback Fader is a flat response
bitten by one narrow notch, the cap sitting in the trough it just pulled down.

Everything is drawn on a 44-unit grid - a 22 pt menu-bar icon at 2x - and
scaled from there, so the same geometry serves the menu bar and the 1024 px
app icon.  Colours are Tokens.Accent (teal, "your hand did this") and
Tokens.Catch (magenta, "a feedback tone was caught").

Writes:
    assets/<app>-mark.svg          the mark, monochrome master
    assets/<app>-icon.svg          the app icon, colour master
    assets/<App>.icns              macOS bundle icon
    assets/FaderBridge.ico         Windows executable icon
    src/<App>.App/Assets/tray.png  menu-bar icon: black + alpha, a macOS
                                   template image, so macOS inverts it for a
                                   dark menu bar
    src/FaderBridge.App/Assets/tray-color.png
                                   the Windows notification area, where a
                                   template image would be invisible
"""
import os, struct, cairosvg
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")

TEAL = "#35C9D6"      # Tokens.Accent
MAGENTA = "#F45D9C"   # Tokens.Catch


def cap(cx, cy, w, colour, h=7, r=3.5):
    """A fader cap: the one shape both marks have in common."""
    return f'<rect x="{cx-w/2}" y="{cy-h/2}" width="{w}" height="{h}" rx="{r}" fill="{colour}"/>'


def bridge(colour="black", track=0.42):
    return (f'<path d="M11 17 C 11 10, 33 10, 33 17" fill="none" stroke="{colour}" '
            f'stroke-width="3" opacity="{track}" stroke-linecap="round"/>'
            f'<path d="M11 17 L11 37 M33 17 L33 37" stroke="{colour}" stroke-width="3" '
            f'opacity="{track}" stroke-linecap="round"/>'
            + cap(11, 29, 13, colour) + cap(33, 24, 13, colour))


def feedback(colour="black", track=0.42):
    return (f'<path d="M6 12 L16 12 L22 26 L28 12 L38 12" fill="none" stroke="{colour}" '
            f'stroke-width="3.2" opacity="{track}" stroke-linecap="round" '
            f'stroke-linejoin="round"/>' + cap(22, 31, 18, colour))


APPS = {
    # slug:          (mark, accent, .App project, bundle name)
    "faderbridge":   (bridge, TEAL, "FaderBridge.App", "FaderBridge"),
    "feedbackfader": (feedback, MAGENTA, "FeedbackFader.App", "FeedbackFader"),
}


def mark_svg(slug, colour="black", track=0.42):
    draw = APPS[slug][0]
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 44 44" width="44" '
            f'height="44">{draw(colour, track)}</svg>')


def icon_svg(slug):
    """Apple's macOS icon grid: an 824 body inset 100 in 1024, radius 185.4."""
    draw, colour = APPS[slug][0], APPS[slug][1]
    body, inset, radius = 824, 100, 185.4
    m = body * 0.62
    mx = inset + (body - m) / 2
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#1B2230"/>
      <stop offset="0.55" stop-color="#0E121A"/>
      <stop offset="1" stop-color="#090B10"/>
    </linearGradient>
    <linearGradient id="rim" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.13"/>
      <stop offset="0.5" stop-color="#FFFFFF" stop-opacity="0.02"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0.06"/>
    </linearGradient>
  </defs>
  <rect x="{inset}" y="{inset}" width="{body}" height="{body}" rx="{radius}" fill="url(#g)"/>
  <rect x="{inset+3}" y="{inset+3}" width="{body-6}" height="{body-6}" rx="{radius-3}"
        fill="none" stroke="url(#rim)" stroke-width="6"/>
  <g transform="translate({mx} {mx}) scale({m/44})">{draw(colour, 0.55)}</g>
</svg>'''


def png(svg_text, path, px):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    cairosvg.svg2png(bytestring=svg_text.encode(), write_to=path,
                     output_width=px, output_height=px)


# icns type codes and their pixel sizes. Written directly rather than through
# iconutil, so this script runs anywhere, not only on a Mac.
ICNS = [(b"ic11", 32), (b"ic12", 64), (b"ic07", 128), (b"ic13", 256),
        (b"ic08", 256), (b"ic14", 512), (b"ic09", 512), (b"ic10", 1024)]


def write_icns(slug, path):
    chunks = b""
    for code, px in ICNS:
        tmp = f"/tmp/_icns_{slug}_{px}.png"
        png(icon_svg(slug), tmp, px)
        data = open(tmp, "rb").read()
        chunks += code + struct.pack(">I", len(data) + 8) + data
        os.remove(tmp)
    open(path, "wb").write(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)


def main():
    os.makedirs(ASSETS, exist_ok=True)
    for slug, (_, colour, project, bundle) in APPS.items():
        app_assets = os.path.join(ROOT, "src", project, "Assets")
        open(f"{ASSETS}/{slug}-mark.svg", "w").write(mark_svg(slug))
        open(f"{ASSETS}/{slug}-icon.svg", "w").write(icon_svg(slug))
        png(mark_svg(slug), f"{app_assets}/tray.png", 44)
        write_icns(slug, f"{ASSETS}/{bundle}.icns")
        print(f"{bundle}: tray.png, {bundle}.icns, two svg masters")

    # FaderBridge is the one that also runs on Windows.
    png(mark_svg("faderbridge", TEAL, 0.55),
        os.path.join(ROOT, "src", "FaderBridge.App", "Assets", "tray-color.png"), 44)
    tmp = "/tmp/_ico.png"
    png(icon_svg("faderbridge"), tmp, 256)
    Image.open(tmp).convert("RGBA").save(
        f"{ASSETS}/FaderBridge.ico", format="ICO",
        sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
    os.remove(tmp)
    print("FaderBridge: tray-color.png, FaderBridge.ico")


if __name__ == "__main__":
    main()
