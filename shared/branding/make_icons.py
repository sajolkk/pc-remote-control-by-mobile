"""Draws the PC-Remote logo and writes every icon file both apps need.

    python shared/branding/make_icons.py

Needs Pillow. The logo is drawn here rather than kept as artwork so that changing a colour or a
proportion is one edit followed by one run, and every size stays in step.

Writes:
    shared/branding/logo.png                              1024 px master, for stores and docs
    windows/assets/pc-remote.ico                          exe, tray, installer and shortcut icon
    mobile/.../res/mipmap-*/ic_launcher.png               legacy launcher icons
    mobile/.../res/mipmap-*/ic_launcher_foreground.png    adaptive-icon foreground (Android 8+)
"""

from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parents[2]
ANDROID_RES = REPO / "mobile/remote_control_app/android/app/src/main/res"

# Drawn large and scaled down: Pillow does not anti-alias shapes, the downscale does.
CANVAS = 2048

TOP = (59, 130, 246)       # blue 500
BOTTOM = (29, 78, 216)     # blue 700
WHITE = (255, 255, 255)
SCREEN = (219, 234, 254)   # blue 100, the lit screens


def gradient(size):
    """A vertical blue gradient, the logo's background."""
    column = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / (size - 1)
        column.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)))
    return column.resize((size, size))


def glyph(size, scale=1.0):
    """The monitor-and-phone mark, white on transparent, centred in a square of `size`.

    `scale` shrinks it about the centre: the adaptive icon keeps it inside Android's safe zone.
    """
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    def box(x0, y0, x1, y1):
        # Coordinates are fractions of the square, measured from its centre so `scale` applies.
        return [round(size * (0.5 + (v - 0.5) * scale)) for v in (x0, y0, x1, y1)]

    def r(fraction):
        return round(size * fraction * scale)

    # Monitor: bezel, lit screen, neck and foot.
    draw.rounded_rectangle(box(0.17, 0.25, 0.77, 0.63), radius=r(0.045), fill=WHITE)
    draw.rounded_rectangle(box(0.205, 0.285, 0.735, 0.595), radius=r(0.02), fill=SCREEN + (255,))
    draw.rectangle(box(0.43, 0.63, 0.51, 0.71), fill=WHITE)
    draw.rounded_rectangle(box(0.33, 0.70, 0.61, 0.745), radius=r(0.02), fill=WHITE)

    # Phone, in front of the monitor's corner. Its outline is punched out of the monitor first so
    # the two shapes read as separate objects at 16 px.
    gap = Image.new("L", (size, size), 0)
    ImageDraw.Draw(gap).rounded_rectangle(box(0.575, 0.415, 0.855, 0.835), radius=r(0.06), fill=255)
    layer.paste((0, 0, 0, 0), mask=gap)

    draw = ImageDraw.Draw(layer)
    draw.rounded_rectangle(box(0.60, 0.44, 0.83, 0.81), radius=r(0.045), fill=WHITE)
    draw.rounded_rectangle(box(0.628, 0.485, 0.802, 0.745), radius=r(0.015), fill=SCREEN + (255,))
    draw.rounded_rectangle(box(0.685, 0.765, 0.745, 0.785), radius=r(0.01), fill=SCREEN + (255,))

    return layer


def rounded_logo(size):
    """The full logo: gradient tile with rounded corners, glyph on top."""
    tile = gradient(size).convert("RGBA")
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=round(size * 0.22), fill=255)
    tile.putalpha(mask)
    tile.alpha_composite(glyph(size))
    return tile


def scaled(image, size):
    return image.resize((size, size), Image.LANCZOS)


def main():
    master = rounded_logo(CANVAS)

    branding = REPO / "shared/branding"
    scaled(master, 1024).save(branding / "logo.png")

    assets = REPO / "windows/assets"
    assets.mkdir(parents=True, exist_ok=True)
    scaled(master, 256).save(
        assets / "pc-remote.ico",
        sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)],
    )

    # Adaptive icons are 108 dp with only the middle 66 dp guaranteed visible, so the glyph is
    # drawn smaller and the launcher supplies the shape and the background colour.
    foreground = glyph(CANVAS, scale=0.62)

    for density, factor in (("mdpi", 1), ("hdpi", 1.5), ("xhdpi", 2), ("xxhdpi", 3), ("xxxhdpi", 4)):
        folder = ANDROID_RES / f"mipmap-{density}"
        folder.mkdir(parents=True, exist_ok=True)
        scaled(master, round(48 * factor)).save(folder / "ic_launcher.png")
        scaled(foreground, round(108 * factor)).save(folder / "ic_launcher_foreground.png")

    print("icons written")


if __name__ == "__main__":
    main()
