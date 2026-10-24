# -*- coding: utf-8 -*-
"""
生成「小组积分」插件的封面与图标。

风格参照 ClassIsland 其他插件的图标：扁平、几何、无插画、无发光、无粒子。
所有元素都是算出来的，改文案重跑即可，不依赖任何图像生成服务。

用法：
    python make_cover.py
输出：
    cover.png          1600x900  横版封面（README 顶部）
    icon.png            512x512  插件图标（插件列表里显示的那个）
"""

from PIL import Image, ImageDraw, ImageFont

FONT_BOLD = "C:/Windows/Fonts/msyhbd.ttc"   # 微软雅黑 Bold
FONT_REG = "C:/Windows/Fonts/msyh.ttc"      # 微软雅黑 Regular

SS = 4                    # 超采样倍数：Pillow 画圆角没有抗锯齿，放大画完再缩回去

# 配色：深色底 + 一个橙色强调色（和悬浮球 #D85A30 同色）
BG_TOP = (31, 36, 45)
BG_BOTTOM = (17, 20, 26)
INK = (255, 255, 255)
INK_SUB = (154, 163, 178)
INK_DIM = (110, 118, 134)
ACCENT = (216, 90, 48)
# 三根柱子由暗到亮，读起来就是「名次从后到前」
BARS = [(132, 62, 33), (192, 84, 44), (242, 153, 122)]


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def vgradient(w, h, top, bottom):
    """竖直渐变。逐行画线，900 行而已，够快。"""
    img = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        d.line([(0, y), (w, y)], fill=lerp(top, bottom, y / max(1, h - 1)))
    return img


def font(path, size):
    return ImageFont.truetype(path, size * SS)


def draw_bars(d, left, baseline, widths, gap, colors, heights, radius):
    """画那三根渐高圆角柱：插件功能的唯一图形符号。"""
    x = left
    for w, c, hgt in zip(widths, colors, heights):
        d.rounded_rectangle(
            [x, baseline - hgt, x + w, baseline],
            radius=radius, fill=c)
        x += w + gap


def make_cover(out, title="小组积分",
               subtitle="ClassIsland 课堂小组计分插件",
               features="悬浮球 · 实时排行榜 · 按周按月对比",
               author="作者 铂金"):
    w, h = 1600 * SS, 900 * SS
    img = vgradient(w, h, BG_TOP, BG_BOTTOM).convert("RGB")
    d = ImageDraw.Draw(img)

    f_title = font(FONT_BOLD, 138)
    f_sub = font(FONT_REG, 33)
    f_feat = font(FONT_REG, 26)
    f_author = font(FONT_REG, 25)

    x = 118 * SS
    top = 246 * SS
    d.text((x, top), title, font=f_title, fill=INK)
    d.text((x + 4 * SS, (top // SS + 198) * SS), subtitle, font=f_sub, fill=INK_SUB)
    d.text((x + 4 * SS, (top // SS + 254) * SS), features, font=f_feat, fill=INK_DIM)
    # 落款放左下角，不跟标题抢视线
    d.text((x + 4 * SS, 782 * SS), author, font=f_author, fill=INK_DIM)

    # 右侧：三根渐高柱，就是「排行榜」三个字最省事的画法。
    # 尺寸撑到跟标题同一档，否则右半边会显得空。
    draw_bars(
        d,
        left=1006 * SS, baseline=630 * SS,
        widths=[106 * SS] * 3, gap=46 * SS,
        colors=BARS, heights=[206 * SS, 296 * SS, 386 * SS],
        radius=22 * SS)

    img = img.resize((1600, 900), Image.LANCZOS)
    img.save(out)
    return out


def make_icon(out):
    """插件图标。列表里只有 48 像素宽，所以元素必须少、笔画必须粗。"""
    size = 512
    w = h = size * SS
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 底色：橙色圆角方块
    d.rounded_rectangle([0, 0, w - 1, h - 1], radius=112 * SS, fill=ACCENT)

    # 三根白色柱子，居中、底对齐。撑满一点，缩小到 48 像素时才不至于糊成一团。
    bar_w, gap = 72, 32
    total = bar_w * 3 + gap * 2
    left = (size - total) // 2
    baseline = 392
    heights = [136, 204, 272]

    x = left
    for hgt in heights:
        d.rounded_rectangle(
            [x * SS, (baseline - hgt) * SS, (x + bar_w) * SS, baseline * SS],
            radius=16 * SS, fill=(255, 255, 255, 255))
        x += bar_w + gap

    img = img.resize((size, size), Image.LANCZOS)
    img.save(out)
    return out


if __name__ == "__main__":
    import os
    here = os.path.dirname(os.path.abspath(__file__))
    print(make_cover(os.path.join(here, "cover.png")))
    print(make_icon(os.path.join(here, "icon.png")))
