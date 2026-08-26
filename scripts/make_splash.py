from PIL import Image, ImageDraw, ImageFont, ImageFilter

W, H = 600, 380
CX, CY = W // 2, 150  # logo tile centre

# --- background: dark navy with a soft bluish radial glow behind the logo ---
img = Image.new("RGB", (W, H), (11, 14, 20))
glow = Image.new("L", (W, H), 0)
gd = ImageDraw.Draw(glow)
for r, a in [(230, 26), (170, 30), (120, 34), (70, 40)]:
    gd.ellipse([CX - r, CY - r, CX + r, CY + r], fill=a)
glow = glow.filter(ImageFilter.GaussianBlur(40))
img = Image.composite(Image.new("RGB", (W, H), (60, 78, 150)), img, glow)

d = ImageDraw.Draw(img)

# --- logo tile: dark rounded square with a left chevron ---
ts = 92
tx0, ty0 = CX - ts // 2, CY - ts // 2
d.rounded_rectangle([tx0, ty0, tx0 + ts, ty0 + ts], radius=22, fill=(23, 28, 38))
cvb = (122, 140, 255)
d.line([(CX + 16, CY - 22), (CX - 16, CY), (CX + 16, CY + 22)], fill=cvb, width=12, joint="curve")


def font(path, size):
    try:
        return ImageFont.truetype(path, size)
    except Exception:
        return ImageFont.load_default()


FB = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
FR = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
wordmark_f = font(FB, 40)
tag_f = font(FR, 15)

# --- wordmark "BENTOFLY", letter-spaced, centred ---
word = "BENTOFLY"
spacing = 10
widths = [d.textlength(ch, font=wordmark_f) for ch in word]
total = sum(widths) + spacing * (len(word) - 1)
x = (W - total) / 2
y = CY + 62
for ch, wch in zip(word, widths):
    d.text((x, y), ch, font=wordmark_f, fill=(238, 241, 248))
    x += wch + spacing

# --- tagline + small underline accent ---
tag = "Your Flight Simulator career, elevated."
tw = d.textlength(tag, font=tag_f)
d.text(((W - tw) / 2, y + 58), tag, font=tag_f, fill=(138, 151, 192))
d.rounded_rectangle([CX - 18, y + 86, CX + 18, y + 89], radius=2, fill=(122, 140, 255))

img.convert("RGBA").save("/home/matt/Career/app/Callsign.Desktop/splash.png")
print("wrote splash.png", img.size)
