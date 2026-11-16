"""Makes the app icons from a big render of the example character, 새싹이.

Input:  tools/app-icon/sprout-render.png  (the idle pose, transparent; rendered from
        tools/example-character/sprout.glb with the sheet maker's lights, 1200 px tall)
Output: mac/AppIcon.icns       (mac/build.sh puts it in the app)
        windows/TabPet.ico     (TabPet.csproj makes it the exe's icon)
        tools/app-icon/AppIcon-1024.png (to look at)

The look: the face from the top of the leaves down, on a soft green, in the usual
macOS rounded square. 16 and 32 px are drawn without the shadow and with a tighter
crop so the leaves and eyes still show.
Run from the repo: python3 tools/app-icon/make-icon.py   (needs Pillow; iconutil comes with macOS)
"""
import os, subprocess, tempfile
from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SRC = f'{HERE}/sprout-render.png'
TOP, BOTTOM = (0xdf, 0xf3, 0xd2), (0xa9, 0xdc, 0x8f)    # light to deeper green

def face(im, k):
    """From the top of the leaves down, as a square k times as wide as the head."""
    im = im.crop(im.getbbox())
    w, h = im.size
    side = int(max(w, 0.68 * h) * k)
    cx = w // 2
    out = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    out.alpha_composite(im.crop((max(0, cx - side // 2), 0, min(w, cx + side // 2), min(h, side))),
                        (max(0, side // 2 - cx), 0))
    return out

def tile(S, inset, radius, shadow):
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    box = (inset, inset, S - inset, S - inset)
    if shadow:
        sh = Image.new('RGBA', (S, S), (0, 0, 0, 0))
        ImageDraw.Draw(sh).rounded_rectangle((box[0], box[1] + S * 0.012, box[2], box[3] + S * 0.012), radius=radius, fill=(0, 0, 0, 90))
        img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(S * 0.014)))
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)
    grad = Image.new('RGBA', (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / S
        gd.line((0, y, S, y), fill=tuple(int(TOP[i] * (1 - t) + BOTTOM[i] * t) for i in range(3)) + (255,))
    img.paste(grad, (0, 0), mask)
    return img, mask

def icon(size, shadow=True):
    small = size <= 32
    S = 1024                                   # draw at 1024, then shrink
    inset = 40 if small else (100 if shadow else 60)
    img, mask = tile(S, inset, 200 if small else 185, shadow=shadow and not small)
    f = face(Image.open(SRC).convert('RGBA'), 0.9 if small else 1.0)
    span = S - 2 * inset
    k = span * (1.0 if small else 0.86) / f.width
    f = f.resize((round(f.width * k), round(f.height * k)), Image.LANCZOS)
    layer = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    layer.alpha_composite(f, ((S - f.width) // 2, S - inset - f.height))   # sits on the bottom edge
    layer.putalpha(Image.composite(layer.getchannel('A'), Image.new('L', (S, S), 0), mask))
    img.alpha_composite(layer)
    return img.resize((size, size), Image.LANCZOS)

def main():
    with tempfile.TemporaryDirectory() as tmp:
        iconset = f'{tmp}/AppIcon.iconset'
        os.makedirs(iconset)
        for pt in (16, 32, 128, 256, 512):
            icon(pt).save(f'{iconset}/icon_{pt}x{pt}.png')
            icon(pt * 2).save(f'{iconset}/icon_{pt}x{pt}@2x.png')
        subprocess.run(['iconutil', '-c', 'icns', iconset, '-o', f'{REPO}/mac/AppIcon.icns'], check=True)
    # Windows draws no shadow under icons and wants the picture to fill more of the square.
    sizes = [256, 128, 64, 48, 32, 24, 16]
    imgs = [icon(s, shadow=False) for s in sizes]
    imgs[0].save(f'{REPO}/windows/TabPet.ico', format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[1:])
    icon(1024).save(f'{HERE}/AppIcon-1024.png', optimize=True)
    print('mac/AppIcon.icns, windows/TabPet.ico')

if __name__ == '__main__':
    main()
