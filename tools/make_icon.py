"""Generate the InkFlow application icon (multi-resolution .ico)."""
from PIL import Image, ImageDraw, ImageFilter

S = 4  # supersampling factor
BASE = 256
N = BASE * S

def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return m

# Diagonal gradient between two brand colors.
c1 = (124, 108, 255)   # violet
c2 = (61, 220, 180)    # mint
grad = Image.new("RGB", (N, N))
px = grad.load()
for y in range(N):
    for x in range(N):
        px[x, y] = lerp(c1, c2, (x + y) / (2 * (N - 1)))

icon = Image.new("RGBA", (N, N), (0, 0, 0, 0))
mask = rounded_mask(N, int(N * 0.235))
icon.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(icon)
# Soft inner highlight at the top-left.
hl = Image.new("RGBA", (N, N), (0, 0, 0, 0))
hld = ImageDraw.Draw(hl)
hld.ellipse((-N * 0.45, -N * 0.65, N * 0.85, N * 0.35), fill=(255, 255, 255, 52))
hl = hl.filter(ImageFilter.GaussianBlur(N * 0.12))
icon = Image.alpha_composite(icon, Image.composite(hl, Image.new("RGBA", (N, N), (0, 0, 0, 0)), mask))
d = ImageDraw.Draw(icon)

# Three writing lines, left aligned, decreasing length.
lines = [
    (0.20, 0.35, 0.80, 0.35, 0.075, 245),
    (0.20, 0.52, 0.66, 0.52, 0.070, 225),
    (0.20, 0.69, 0.52, 0.69, 0.065, 195),
]
for x1, y1, x2, y2, h, alpha in lines:
    d.rounded_rectangle(
        (x1 * N, (y1 - h / 2) * N, x2 * N, (y1 + h / 2) * N),
        radius=h * N / 2,
        fill=(255, 255, 255, alpha),
    )

# Ink dot accent.
r = 0.058 * N
cx, cy = 0.775 * N, 0.69 * N
d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(255, 255, 255, 255))

icon = icon.resize((BASE, BASE), Image.LANCZOS)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
icon.save(
    r"D:\Code\write-helper\src\InkFlow\Assets\app.ico",
    format="ICO",
    sizes=[(s, s) for s in sizes],
)
# Preview image for docs.
icon.resize((256, 256), Image.LANCZOS).save(r"D:\Code\write-helper\tools\icon-preview.png")
print("icon written")
