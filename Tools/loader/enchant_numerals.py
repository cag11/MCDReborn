"""IV and V for the enchantment badge, drawn in the game's own numeral style: 11-row slab serifs, 11-wide
strokes, a silver gradient top to bottom and a dark shadow 7 rows below. Our drawing, no game pixels."""
from PIL import Image

TOP, BOTTOM = 34, 86            # glyph rows
SLAB = 11
STROKE = 11
SHADOW_DROP, SHADOW = 7, (3, 8, 13, 105)

def colour(y):
    t = (y - TOP) / (BOTTOM - TOP)
    a, b = (244, 244, 244), (184, 178, 178)
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3)) + (255,)

def glyph_I():
    m = set()
    for y in range(TOP, BOTTOM + 1):
        if y < TOP + SLAB or y > BOTTOM - SLAB:
            m |= {(x, y) for x in range(0, 32)}
        else:
            m |= {(x, y) for x in range(10, 10 + STROKE)}
    return m, 32

def glyph_V():
    m = set(); W = 40
    for y in range(TOP, TOP + SLAB):
        m |= {(x, y) for x in range(0, 17)} | {(x, y) for x in range(W - 17, W)}
    first, last = TOP + SLAB, BOTTOM
    for y in range(first, last + 1):
        t = (y - first) / (last - first)
        left = round(3 + (W / 2 - STROKE / 2 - 3) * t)
        m |= {(x, y) for x in range(left, left + STROKE)}
        m |= {(W - 1 - x, y) for x in range(left, left + STROKE)}
    return m, W

def draw(glyphs, gap=8, size=128):
    parts = [g() for g in glyphs]
    width = sum(w for _, w in parts) + gap * (len(parts) - 1)
    x0 = (size - width) // 2
    mask = set()
    for m, w in parts:
        mask |= {(x + x0, y) for x, y in m}
        x0 += w + gap
    im = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    for x, y in mask:
        s = (x, y + SHADOW_DROP)
        if s not in mask and 0 <= s[1] < size:
            im.putpixel(s, SHADOW)
    for x, y in mask:
        im.putpixel((x, y), colour(y))
    return im

if __name__ == '__main__':
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    draw([glyph_I, glyph_V]).save(out + '/T_MCDRebornEnchantPlateIV.png')
    draw([glyph_V]).save(out + '/T_MCDRebornEnchantPlateV.png')


#--- the small numerals of the upgrade tiers list (level_N_normal_text / _hover_text): flat, no shadow ---
def draw_small(letters, fill, size=200):
    top, bottom, slab, stroke = 82, 117, 8, 7

    def small_I():
        m = set()
        for y in range(top, bottom + 1):
            xs = range(0, 21) if (y < top + slab or y > bottom - slab) else range(7, 7 + stroke)
            m |= {(x, y) for x in xs}
        return m, 21

    def small_V():
        m = set(); W = 27
        for y in range(top, top + slab):
            m |= {(x, y) for x in range(0, 11)} | {(x, y) for x in range(W - 11, W)}
        first, last = top + slab, bottom
        for y in range(first, last + 1):
            t = (y - first) / (last - first)
            left = round(2 + (W / 2 - stroke / 2 - 2) * t)
            m |= {(x, y) for x in range(left, left + stroke)}
            m |= {(W - 1 - x, y) for x in range(left, left + stroke)}
        return m, W

    shapes = {'I': small_I, 'V': small_V}
    parts = [shapes[c]() for c in letters]
    gap = 7
    width = sum(w for _, w in parts) + gap * (len(parts) - 1)
    x0 = (size - width) // 2
    im = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    for m, w in parts:
        for x, y in m:
            im.putpixel((x + x0, y), fill[:3] + ((141,) if y == top else (255,)))
        x0 += w + gap
    return im


NORMAL, HOVER = (231, 217, 198, 255), (255, 255, 255, 255)

if __name__ == '__main__':
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    for letters, name in (('IV', 'IV'), ('V', 'V')):
        draw_small(letters, NORMAL).save(out + '/T_MCDRebornEnchantSmall%s.png' % name)
        draw_small(letters, HOVER).save(out + '/T_MCDRebornEnchantSmall%sHover.png' % name)
