# -*- coding: utf-8 -*-
"""压接高度自动调整系统 — 培训 PPT 生成脚本"""

from pptx import Presentation
from pptx.util import Inches, Pt, Emu
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import PP_ALIGN, MSO_AUTO_SIZE
from pptx.dml.color import RGBColor

# ── 配色方案（工业蓝） ──
C_PRIMARY   = RGBColor(0x1A, 0x3C, 0x5E)   # 深蓝（标题/背景）
C_SECONDARY = RGBColor(0x2E, 0x86, 0xAB)   # 中蓝（强调）
C_ACCENT    = RGBColor(0xE8, 0x6F, 0x2C)   # 橙色（警示/重点）
C_GREEN     = RGBColor(0x27, 0xAE, 0x60)   # 绿色（正常/稳定）
C_RED       = RGBColor(0xE7, 0x4C, 0x3C)   # 红色（停机/异常）
C_YELLOW    = RGBColor(0xF3, 0x9C, 0x12)   # 黄色（降级/注意）
C_WHITE     = RGBColor(0xFF, 0xFF, 0xFF)
C_LIGHT_BG  = RGBColor(0xF5, 0xF7, 0xFA)   # 浅灰背景
C_DARK_TEXT  = RGBColor(0x2C, 0x3E, 0x50)
C_GRAY      = RGBColor(0x7F, 0x8C, 0x8D)
C_LIGHT_GRAY = RGBColor(0xBD, 0xC3, 0xC7)
C_TABLE_HDR  = RGBColor(0x1A, 0x3C, 0x5E)
C_TABLE_ALT  = RGBColor(0xEB, 0xF5, 0xFB)

prs = Presentation()
prs.slide_width  = Inches(13.333)
prs.slide_height = Inches(7.5)

# ── 工具函数 ──

def add_bg(slide, color):
    bg = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, 0, 0, prs.slide_width, prs.slide_height)
    bg.fill.solid()
    bg.fill.fore_color.rgb = color
    bg.line.fill.background()
    spTree = bg._element.getparent()
    spTree.remove(bg._element)
    spTree.insert(2, bg._element)

def add_text(slide, text, x, y, w, h, size=18, bold=False, color=C_DARK_TEXT,
             align=PP_ALIGN.LEFT, valign=None, font_name="Calibri"):
    tb = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = tb.text_frame
    tf.word_wrap = True
    p = tf.paragraphs[0]
    p.text = text
    p.alignment = align
    r = p.runs[0]
    r.font.size = Pt(size)
    r.font.bold = bold
    r.font.color.rgb = color
    r.font.name = font_name
    return tb

def add_rich_text(slide, lines, x, y, w, h, default_size=18, default_color=C_DARK_TEXT):
    """lines: list of (text, size, bold, color)"""
    tb = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = tb.text_frame
    tf.word_wrap = True
    for i, (text, size, bold, color) in enumerate(lines):
        p = tf.paragraphs[0] if i == 0 else tf.add_paragraph()
        p.text = text
        p.alignment = PP_ALIGN.LEFT
        r = p.runs[0]
        r.font.size = Pt(size or default_size)
        r.font.bold = bold
        r.font.color.rgb = color or default_color
        r.font.name = "Calibri"
    return tb

def add_bullet_list(slide, items, x, y, w, h, size=16, color=C_DARK_TEXT, spacing=Pt(8)):
    tb = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = tb.text_frame
    tf.word_wrap = True
    for i, item in enumerate(items):
        p = tf.paragraphs[0] if i == 0 else tf.add_paragraph()
        p.text = item
        p.space_after = spacing
        r = p.runs[0]
        r.font.size = Pt(size)
        r.font.color.rgb = color
        r.font.name = "Calibri"
    return tb

def add_table(slide, headers, rows, x, y, w, h, col_widths=None):
    tbl = slide.shapes.add_table(len(rows)+1, len(headers),
        Inches(x), Inches(y), Inches(w), Inches(h)).table
    # header
    for c, hdr in enumerate(headers):
        cell = tbl.cell(0, c)
        cell.text = hdr
        cell.fill.solid()
        cell.fill.fore_color.rgb = C_TABLE_HDR
        for p in cell.text_frame.paragraphs:
            for r in p.runs:
                r.font.size = Pt(13)
                r.font.bold = True
                r.font.color.rgb = C_WHITE
                r.font.name = "Calibri"
    # data
    for ri, row in enumerate(rows):
        for ci, val in enumerate(row):
            cell = tbl.cell(ri+1, ci)
            cell.text = str(val)
            if ri % 2 == 1:
                cell.fill.solid()
                cell.fill.fore_color.rgb = C_TABLE_ALT
            for p in cell.text_frame.paragraphs:
                for r in p.runs:
                    r.font.size = Pt(12)
                    r.font.color.rgb = C_DARK_TEXT
                    r.font.name = "Calibri"
    if col_widths:
        for i, cw in enumerate(col_widths):
            tbl.columns[i].width = Inches(cw)
    return tbl

def add_shape_box(slide, x, y, w, h, fill_color, text="", text_size=14, text_color=C_WHITE, bold=True):
    shape = slide.shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(x), Inches(y), Inches(w), Inches(h))
    shape.fill.solid()
    shape.fill.fore_color.rgb = fill_color
    shape.line.fill.background()
    if text:
        tf = shape.text_frame
        tf.word_wrap = True
        p = tf.paragraphs[0]
        p.text = text
        p.alignment = PP_ALIGN.CENTER
        r = p.runs[0]
        r.font.size = Pt(text_size)
        r.font.bold = bold
        r.font.color.rgb = text_color
        r.font.name = "Calibri"
    return shape

def add_arrow_down(slide, x, y, h=0.3):
    shape = slide.shapes.add_shape(MSO_SHAPE.DOWN_ARROW, Inches(x), Inches(y), Inches(0.3), Inches(h))
    shape.fill.solid()
    shape.fill.fore_color.rgb = C_SECONDARY
    shape.line.fill.background()

def add_notes(slide, text):
    notes_tf = slide.notes_slide.notes_text_frame
    notes_tf.text = text


# ============================================================
#  SLIDE 1 — 封面
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_PRIMARY)
add_text(s, "压接高度自动调整系统", 0.8, 1.8, 11.7, 1.5, size=44, bold=True, color=C_WHITE, align=PP_ALIGN.CENTER)
add_text(s, "Run-to-Run 闭环控制  ·  内部培训", 0.8, 3.3, 11.7, 0.8, size=24, color=C_LIGHT_GRAY, align=PP_ALIGN.CENTER)
add_text(s, "从数据读取到 PLC 指令下发的完整流程", 0.8, 4.3, 11.7, 0.6, size=18, color=C_LIGHT_GRAY, align=PP_ALIGN.CENTER)
# 底部装饰线
line = s.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(4), Inches(3.15), Inches(5.3), Inches(0.04))
line.fill.solid(); line.fill.fore_color.rgb = C_ACCENT; line.line.fill.background()
add_notes(s, "开场白：本次培训覆盖系统从CSV读取到PLC指令下发的完整闭环流程，重点讲解五级优先级仲裁机制。")


# ============================================================
#  SLIDE 2 — 目录
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "目录", 0.8, 0.4, 11.7, 0.8, size=36, bold=True, color=C_PRIMARY)
line = s.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(0.8), Inches(1.15), Inches(2), Inches(0.04))
line.fill.solid(); line.fill.fore_color.rgb = C_ACCENT; line.line.fill.background()

toc_items = [
    ("01", "系统概述与物理布局"),
    ("02", "端到端数据流总览"),
    ("03", "数据读取与统计计算"),
    ("04", "五级优先级仲裁（核心）"),
    ("05", "发散检测详解"),
    ("06", "漂移检测详解"),
    ("07", "调整量计算"),
    ("08", "PLC 指令下发"),
    ("09", "状态机与参数速查"),
]
for i, (num, title) in enumerate(toc_items):
    yy = 1.6 + i * 0.58
    add_text(s, num, 1.2, yy, 0.6, 0.5, size=20, bold=True, color=C_SECONDARY)
    add_text(s, title, 2.0, yy, 8, 0.5, size=18, color=C_DARK_TEXT)
add_notes(s, "目录页，按顺序讲解。")


# ============================================================
#  SLIDE 3 — 系统概述
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "01  系统概述", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# 物理布局图
add_shape_box(s, 3.5, 1.8, 6.3, 1.2, C_PRIMARY, "压 接 机", text_size=22, text_color=C_WHITE)
add_shape_box(s, 1.5, 3.5, 4.5, 1.0, C_SECONDARY, "铆压#1（压头 A）\n产品1 + 产品2", text_size=14)
add_shape_box(s, 7.3, 3.5, 4.5, 1.0, C_SECONDARY, "铆压#2（压头 B）\n产品3 + 产品4", text_size=14)
add_arrow_down(s, 6.5, 3.0, 0.4)
add_arrow_down(s, 3.5, 3.0, 0.4)
add_arrow_down(s, 9.3, 3.0, 0.4)

add_bullet_list(s, [
    "每个压头独立分析、独立调整，互不干扰",
    "核心目标：将压接高度稳定控制在规格中心点附近",
    "过程漂移时及时修正，过程发散时报警停机",
], 0.8, 5.0, 11.7, 2.0, size=16)
add_notes(s, "物理布局：两个压头各自独立。每个压头同时压接两个产品。系统对每个压头独立分析和调整。")


# ============================================================
#  SLIDE 4 — 端到端数据流
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "02  端到端数据流总览", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

steps = [
    ("① CSV 文件", "监视目录/YYYY-MM-DD/子目录/*.csv", C_GRAY),
    ("② 数据读取", "解析每行，取最近 200 条有效数据", C_SECONDARY),
    ("③ 数据分流", "铆压#1 → 头A  |  铆压#2 → 头B", C_SECONDARY),
    ("④ 统计计算", "截尾均值 μ、偏差 Deviation、斜率 Slope", C_SECONDARY),
    ("⑤ 五级仲裁", "P1安全 → P2置信 → P3发散 → P4漂移/越界 → P5稳定", C_PRIMARY),
    ("⑥ PLC 指令", "停机 > 调整（优先级）", C_ACCENT),
    ("⑦ PLC 执行", "压头位移", C_GREEN),
]
for i, (title, desc, color) in enumerate(steps):
    yy = 1.5 + i * 0.78
    add_shape_box(s, 1.0, yy, 2.2, 0.55, color, title, text_size=13, text_color=C_WHITE)
    add_text(s, desc, 3.5, yy, 9, 0.55, size=14, color=C_DARK_TEXT)
    if i < len(steps) - 1:
        add_arrow_down(s, 1.9, yy + 0.55, 0.2)

add_notes(s, "这是整个系统的数据流。从CSV文件开始，经过数据读取、统计计算、五级仲裁，最终发送PLC指令。")


# ============================================================
#  SLIDE 5 — 数据读取
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "03  数据读取 — CSV 解析", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

add_table(s,
    ["列索引", "内容", "映射字段"],
    [
        ["0", "时间戳", "Timestamp"],
        ["1-4", "产品1~4 结果", "Product1~4Result (true/false)"],
        ["7, 10, 13, 16", "产品3,4,1,2 高度", "ProductXLength (mm)"],
    ],
    0.8, 1.5, 7, 2.2, col_widths=[1.5, 2.5, 3])

add_bullet_list(s, [
    "有效性判断：4 个产品 Result 全部为 true 且 Length ≠ 999",
    "任一产品无效 → 整行排除，4 个产品都不参与本轮计算",
    "数据窗口：只取最近 WindowSize（默认 200）条有效数据",
    "数据按时间正序排列，用于后续趋势分析",
], 0.8, 4.2, 11.7, 2.8, size=15)
add_notes(s, "CSV解析细节。注意有效性判断是整行级别的，不是单个产品级别。")


# ============================================================
#  SLIDE 6 — 统计计算
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "03  统计计算 — 截尾均值 · 偏差 · 斜率", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# 截尾均值
add_shape_box(s, 0.8, 1.5, 3.8, 0.5, C_SECONDARY, "截尾均值 TrimmedMean", text_size=14)
add_bullet_list(s, [
    "排序 → 去掉头尾 5% → 求平均",
    "消除极端值（飞溅、测量异常）影响",
], 0.8, 2.1, 3.8, 1.2, size=13)

# 偏差
add_shape_box(s, 4.9, 1.5, 3.8, 0.5, C_ACCENT, "偏差 Deviation", text_size=14)
add_bullet_list(s, [
    "TargetMidpoint = (上限+下限)/2 = 12.60mm",
    "Deviation = μ − 12.60",
    "正 = 偏高，负 = 偏低",
], 4.9, 2.1, 3.8, 1.5, size=13)

# 斜率
add_shape_box(s, 9.0, 1.5, 3.5, 0.5, C_PRIMARY, "斜率 Slope", text_size=14)
add_bullet_list(s, [
    "最小二乘线性回归",
    "slope > 0 → 上升趋势",
    "slope < 0 → 下降趋势",
], 9.0, 2.1, 3.5, 1.5, size=13)

# 各层均值
add_text(s, "各层均值的含义", 0.8, 3.9, 5, 0.5, size=18, bold=True, color=C_PRIMARY)
add_table(s,
    ["均值", "计算范围", "用途"],
    [
        ["μ₁（产品1截尾均值）", "仅产品1数据", "发散检测输入"],
        ["μ₂（产品2截尾均值）", "仅产品2数据", "发散检测输入"],
        ["μ（合并截尾均值）", "产品1 ∪ 产品2", "偏差计算、调整量计算"],
    ],
    0.8, 4.5, 11.7, 2.0, col_widths=[3.5, 3.2, 5])
add_notes(s, "三种统计量：截尾均值消除极端值、偏差衡量离目标多远、斜率衡量趋势方向。")


# ============================================================
#  SLIDE 7 — 五级仲裁总览
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_PRIMARY)
add_text(s, "04  五级优先级仲裁 — 核心机制", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_WHITE)
add_text(s, "每拍每压头执行一次，按优先级从高到低匹配，命中即止", 0.8, 1.1, 11.7, 0.5, size=16, color=C_LIGHT_GRAY)

levels = [
    ("P1", "安全护栏", "均值越界 / σ突增 / 数据失活", "→ Outlier 停机", C_RED),
    ("P2", "置信不足", "样本数 < 50", "→ WarmUp 等待", C_GRAY),
    ("P3", "发散严重", "|D| > 2T (0.04mm)", "→ 停机维护", C_RED),
    ("P4", "越界/漂移", "偏差越界 或 漂移确认", "→ 计算调整量 Δ", C_ACCENT),
    ("P5", "稳定", "以上均不满足", "→ Stable 不动作", C_GREEN),
]
for i, (pri, name, cond, action, color) in enumerate(levels):
    yy = 1.8 + i * 1.05
    # 优先级标签
    add_shape_box(s, 0.8, yy, 0.8, 0.7, color, pri, text_size=18, text_color=C_WHITE)
    # 名称
    add_text(s, name, 1.8, yy, 2.0, 0.7, size=18, bold=True, color=C_WHITE)
    # 条件
    add_text(s, cond, 4.0, yy, 4.5, 0.7, size=15, color=C_LIGHT_GRAY)
    # 动作
    add_shape_box(s, 8.8, yy+0.05, 3.7, 0.6, color, action, text_size=14, text_color=C_WHITE, bold=False)
    # 箭头
    if i < len(levels) - 1:
        add_text(s, "↓ 未命中则继续", 5.0, yy + 0.7, 3, 0.3, size=11, color=C_LIGHT_GRAY, align=PP_ALIGN.CENTER)

add_notes(s, "这是整个系统的核心。五级优先级仲裁，命中即止。P1最高，P5最低。")


# ============================================================
#  SLIDE 8 — P1 安全护栏
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "P1  安全护栏（最高优先级）", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_RED)

add_table(s,
    ["检查项", "触发条件", "含义"],
    [
        ["合理性阈", "μ < 12.40 或 μ > 12.80 mm", "均值严重偏离，可能是测量错误或模具损坏"],
        ["σ 突增", "σ > 基线σ × 3.0", "过程波动突然变大，可能是模具松动"],
        ["数据失活", "最后数据距今 > 60s", "数据源中断，无法信任当前判断"],
    ],
    0.8, 1.5, 11.7, 2.5, col_widths=[2, 3.5, 6.2])

add_shape_box(s, 0.8, 4.5, 11.7, 1.0, C_RED,
    "触发 → State = Outlier → 发停机指令，不做任何调整", text_size=18, text_color=C_WHITE)

add_bullet_list(s, [
    "σ 基线取历史最小值（只在 Stable 和 Drift 时更新）",
    "合理性范围远大于规格范围（12.40~12.80 vs 12.55~12.65），只拦截极端异常",
], 0.8, 5.8, 11.7, 1.5, size=15)
add_notes(s, "安全护栏是最后防线。三个检查项任一触发即停机。σ基线取历史最小值，只在稳态和漂移时更新。")


# ============================================================
#  SLIDE 9 — P2 P3
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "P2 置信不足  &  P3 发散严重", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# P2
add_shape_box(s, 0.8, 1.5, 5.5, 0.5, C_GRAY, "P2  置信不足", text_size=16)
add_bullet_list(s, [
    "触发条件：有效样本数 < MinSamplesPerEpoch (50)",
    "场景：刚换模、刚开机、epoch 刚重置",
    "产出：State = WarmUp，不动作，继续采集",
], 0.8, 2.2, 5.5, 2.0, size=14)

# P3
add_shape_box(s, 6.8, 1.5, 5.7, 0.5, C_RED, "P3  发散严重 (Stop)", text_size=16)
add_bullet_list(s, [
    "触发条件：|D| > 2T = 0.04mm",
    "D = μ₁ − μ₂（两产品截尾均值之差）",
    "含义：两个产品高度差异过大，机械有问题",
    "单靠调整压头无法同时修正两个产品",
    "产出：停机，需人工维护模具",
], 6.8, 2.2, 5.7, 2.8, size=14)

# 发散三级图
add_text(s, "发散三级判定", 0.8, 5.0, 5, 0.5, size=18, bold=True, color=C_PRIMARY)
add_shape_box(s, 0.8, 5.6, 3.5, 0.6, C_GREEN, "|D| < T → Normal\n全速控制", text_size=12)
add_shape_box(s, 4.6, 5.6, 3.8, 0.6, C_YELLOW, "T ≤ |D| ≤ 2T → Degraded\nK 减半，更保守", text_size=12, text_color=C_DARK_TEXT)
add_shape_box(s, 8.7, 5.6, 3.8, 0.6, C_RED, "|D| > 2T → Stop\n停机维护", text_size=12)
add_text(s, "T = DivergenceThreshold = 0.02mm", 0.8, 6.4, 5, 0.4, size=13, color=C_GRAY)
add_notes(s, "P2是数据量不够时的保护。P3是发散检测的最严重级别。发散三级：Normal/Degraded/Stop。")


# ============================================================
#  SLIDE 10 — P4 越界与漂移
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "P4  越界 与 漂移确认", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_ACCENT)
add_text(s, '两个条件是"或"关系，任一满足即触发调整', 0.8, 1.0, 11.7, 0.4, size=16, color=C_GRAY)

# 越界
add_shape_box(s, 0.8, 1.6, 5.5, 0.5, C_ACCENT, "条件A：偏差越界", text_size=16)
add_bullet_list(s, [
    "Deviation > +0.02mm → 偏高越界",
    "Deviation < −0.02mm → 偏低越界",
    "死区 [−0.02, +0.02] 内不触发",
    "处理的是突变（阶跃变化）",
], 0.8, 2.3, 5.5, 2.0, size=14)

# 漂移
add_shape_box(s, 6.8, 1.6, 5.7, 0.5, C_SECONDARY, "条件B：漂移确认 (DriftScore)", text_size=16)
add_bullet_list(s, [
    "连续 3 拍斜率同号（全正或全负）",
    "且 平均|slope| ≥ 0.0001 mm/拍",
    "数据先对齐（减各自均值）再算斜率",
    "处理的是渐变（温度漂移、模具磨损）",
], 6.8, 2.3, 5.7, 2.0, size=14)

# 触发后
add_text(s, "触发后 → 计算调整量 Δ = K × Deviation", 0.8, 4.8, 11.7, 0.5, size=18, bold=True, color=C_ACCENT)

add_notes(s, "P4有两个触发条件：越界（突变）和漂移（渐变），是或关系。任一满足就进入调整量计算。")


# ============================================================
#  SLIDE 11 — 调整量计算
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "07  调整量计算 — R2R 控制律", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# 公式
add_shape_box(s, 0.8, 1.5, 11.7, 1.2, C_PRIMARY,
    "Δ = K × Deviation\nK = ControllerGain (0.5)，Degraded 时 K × 0.5 = 0.25",
    text_size=18, text_color=C_WHITE)

# 流程
add_text(s, "计算流程", 0.8, 3.0, 5, 0.4, size=20, bold=True, color=C_PRIMARY)

flow_steps = [
    ("1. 死区判断", "偏差在 [−0.02, +0.02]？ → Δ=0", C_GRAY),
    ("2. 确定有效K", "Normal: K=0.5 | Degraded: K=0.25", C_SECONDARY),
    ("3. 计算原始Δ", "rawDelta = K × Deviation", C_SECONDARY),
    ("4. 步长限幅", "Clamp(Δ, −0.01, +0.01)", C_ACCENT),
]
for i, (title, desc, color) in enumerate(flow_steps):
    yy = 3.5 + i * 0.7
    add_shape_box(s, 0.8, yy, 2.5, 0.5, color, title, text_size=13, text_color=C_WHITE)
    add_text(s, desc, 3.5, yy, 6, 0.5, size=14, color=C_DARK_TEXT)

# 方向说明
add_text(s, "调整方向", 8, 3.0, 4, 0.4, size=18, bold=True, color=C_PRIMARY)
add_bullet_list(s, [
    "Δ > 0 → 压头下降（压更深，高度降低）",
    "Δ < 0 → 压头上升（压更浅，高度升高）",
    "Δ = 0 → 不动作",
], 8, 3.5, 4.5, 1.5, size=14)

add_notes(s, "调整量计算公式。先检查死区，再确定K值，计算原始Δ，最后限幅。正Δ=压头下降，负Δ=压头上升。")


# ============================================================
#  SLIDE 12 — 计算示例
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "07  调整量计算 — 示例", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# 示例1
add_shape_box(s, 0.8, 1.5, 5.5, 0.5, C_GREEN, "示例1：偏高，Normal 模式", text_size=14)
add_table(s,
    ["步骤", "计算", "结果"],
    [
        ["截尾均值 μ", "—", "12.623 mm"],
        ["目标点", "(12.65+12.55)/2", "12.600 mm"],
        ["偏差", "12.623 − 12.600", "+0.023 mm"],
        ["原始Δ", "0.5 × 0.023", "+0.0115 mm"],
        ["限幅后", "Clamp(+0.0115, −0.01, +0.01)", "+0.010 mm"],
        ["方向", "Δ > 0", "压头下降 0.01mm"],
    ],
    0.8, 2.1, 5.5, 3.5, col_widths=[1.5, 2.5, 1.5])

# 示例2
add_shape_box(s, 6.8, 1.5, 5.7, 0.5, C_YELLOW, "示例2：偏低，Degraded 模式", text_size=14, text_color=C_DARK_TEXT)
add_table(s,
    ["步骤", "计算", "结果"],
    [
        ["截尾均值 μ", "—", "12.585 mm"],
        ["偏差", "12.585 − 12.600", "−0.015 mm"],
        ["有效K", "0.5 × 0.5", "0.25"],
        ["原始Δ", "0.25 × (−0.015)", "−0.00375 mm"],
        ["限幅后", "未触发限幅", "−0.00375 mm"],
        ["方向", "Δ < 0", "压头上升 0.00375mm"],
    ],
    6.8, 2.1, 5.7, 3.5, col_widths=[1.5, 2.5, 1.7])

add_text(s, "注意：Degraded 模式下 K 减半，调整量更保守，避免在模具异常时过度修正", 0.8, 6.2, 11.7, 0.5, size=14, color=C_ACCENT)
add_notes(s, "两个计算示例。示例1是正常模式下的偏高修正，示例2是降级模式下的偏低修正。注意K减半的效果。")


# ============================================================
#  SLIDE 13 — 漂移检测详解
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "06  漂移检测详解 — DriftScore", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# 流程
add_text(s, "检测流程", 0.8, 1.3, 5, 0.4, size=20, bold=True, color=C_PRIMARY)

drift_steps = [
    ("① 线性回归", "对当前窗口数据计算 slope"),
    ("② 存入历史", "保留最近 6 个 slope 值"),
    ("③ 方向检查", "最近 3 个 slope 全正或全负？"),
    ("④ 幅度检查", "平均|slope| ≥ 0.0001 mm/拍？"),
    ("⑤ 确认漂移", "③④ 同时满足 → driftConfirmed = true"),
]
for i, (title, desc) in enumerate(drift_steps):
    yy = 1.8 + i * 0.6
    add_shape_box(s, 0.8, yy, 1.8, 0.45, C_SECONDARY, title, text_size=12, text_color=C_WHITE)
    add_text(s, desc, 2.8, yy, 5, 0.45, size=14, color=C_DARK_TEXT)

# 数据对齐
add_text(s, "为什么要数据对齐？", 7.5, 1.3, 5, 0.4, size=18, bold=True, color=C_PRIMARY)
add_bullet_list(s, [
    "同一压头下两个产品可能有固定高度落差",
    "不对齐 → 斜率被产品间差异干扰 → 误判漂移",
    "对齐方法：每个产品减去各自均值",
    "对齐后只保留波动趋势，消除固定落差",
], 7.5, 1.8, 5.3, 2.5, size=14)

# 漂移 vs 越界
add_text(s, "漂移 vs 越界", 7.5, 4.5, 5, 0.4, size=18, bold=True, color=C_PRIMARY)
add_table(s,
    ["", "越界", "漂移"],
    [
        ["触发方式", "单拍偏差超出容差", "连续多拍趋势一致"],
        ["含义", "突然偏离（阶跃变化）", "渐进偏移（温度/磨损）"],
        ["关系", "或关系，任一满足触发调整", "或关系，任一满足触发调整"],
    ],
    7.5, 5.0, 5.3, 1.8, col_widths=[1.3, 2, 2])

add_notes(s, "漂移检测的核心：连续3拍斜率同号且幅度足够。数据对齐是为了消除产品间固定落差的干扰。")


# ============================================================
#  SLIDE 14 — 发散检测详解
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "05  发散检测详解 — DivergenceGrader", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# 计算
add_shape_box(s, 0.8, 1.5, 11.7, 0.8, C_PRIMARY,
    "D = μ₁ − μ₂（产品1截尾均值 − 产品2截尾均值）\n衡量同一压头下两个产品的高度差异",
    text_size=16, text_color=C_WHITE)

# 三级判定
add_text(s, "三级判定", 0.8, 2.8, 5, 0.4, size=20, bold=True, color=C_PRIMARY)
add_table(s,
    ["条件", "等级", "动作", "K 倍率"],
    [
        ["|D| < 0.02mm", "Normal", "全速控制", "K = 0.5"],
        ["0.02 ≤ |D| ≤ 0.04mm", "Degraded", "继续控制，K 减半", "K = 0.25"],
        ["|D| > 0.04mm", "Stop", "停机维护", "不计算"],
    ],
    0.8, 3.3, 11.7, 1.8, col_widths=[3, 2, 3.5, 3.2])

# 物理含义
add_text(s, "物理含义", 0.8, 5.5, 5, 0.4, size=18, bold=True, color=C_PRIMARY)
add_bullet_list(s, [
    "Normal：两产品高度一致 → 模具状态良好",
    "Degraded：两产品开始有差异 → 可能轻微磨损，降低增益避免过度修正",
    "Stop：两产品差异过大 → 单靠调整压头无法同时修正，必须停机检查模具",
], 0.8, 6.0, 11.7, 1.3, size=14)
add_notes(s, "发散检测衡量同一压头下两个产品的差异。三级判定：Normal/Degraded/Stop。发散不直接触发调整，但会调制K值。")


# ============================================================
#  SLIDE 15 — PLC 指令下发
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "08  PLC 指令下发", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# 优先级
add_shape_box(s, 0.8, 1.5, 11.7, 0.7, C_RED,
    "核心原则：停机指令 > 调整指令", text_size=20, text_color=C_WHITE)

# 逻辑流程
add_text(s, "指令判断逻辑", 0.8, 2.6, 5, 0.4, size=20, bold=True, color=C_PRIMARY)

logic_steps = [
    ("检查停机", "任一压头 RequiresStop = true？", C_RED),
    ("是 → 发停机", "SendStopAsync(reason)，跳过所有调整", C_RED),
    ("否 → 检查调整", "压头A/B 各自 RequiresAction = true？", C_SECONDARY),
    ("发调整指令", "SendAdjustmentAsync(headIndex, Δ)", C_GREEN),
]
for i, (title, desc, color) in enumerate(logic_steps):
    yy = 3.1 + i * 0.75
    add_shape_box(s, 0.8, yy, 2.5, 0.55, color, title, text_size=13, text_color=C_WHITE)
    add_text(s, desc, 3.5, yy, 6, 0.55, size=14, color=C_DARK_TEXT)

# 触发条件表
add_text(s, "触发条件", 8, 2.6, 4.5, 0.4, size=18, bold=True, color=C_PRIMARY)
add_table(s,
    ["场景", "停机？", "调整？"],
    [
        ["安全护栏触发", "Yes", "No"],
        ["发散 Stop", "Yes", "No"],
        ["越界/漂移(Δ≠0)", "No", "Yes"],
        ["越界/漂移(Δ=0)", "No", "No"],
        ["Stable", "No", "No"],
    ],
    8, 3.1, 4.5, 2.8, col_widths=[2, 1, 1.5])

add_notes(s, "PLC指令下发的核心原则：停机优先于调整。如果任一压头需要停机，所有调整指令都被跳过。")


# ============================================================
#  SLIDE 16 — 状态机
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "09  状态机总结", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

# HeadState
add_text(s, "压头状态 (HeadState)", 0.8, 1.3, 5, 0.4, size=20, bold=True, color=C_PRIMARY)
add_shape_box(s, 0.8, 1.8, 2.5, 0.7, C_GRAY, "WarmUp\n数据采集中", text_size=12)
add_shape_box(s, 3.8, 1.8, 2.5, 0.7, C_GREEN, "Stable\n稳态，不动作", text_size=12)
add_shape_box(s, 6.8, 1.8, 2.5, 0.7, C_ACCENT, "Drift\n执行调整", text_size=12)
add_shape_box(s, 9.8, 1.8, 2.5, 0.7, C_RED, "Outlier\n停机报警", text_size=12)

# 转换表
add_text(s, "状态转换表", 0.8, 3.0, 5, 0.4, size=18, bold=True, color=C_PRIMARY)
add_table(s,
    ["当前状态", "条件", "下一状态", "动作"],
    [
        ["任意", "安全护栏触发", "Outlier", "停机"],
        ["任意", "样本不足", "WarmUp", "等待"],
        ["任意", "发散 Stop", "Drift(Stop)", "停机"],
        ["任意", "越界或漂移确认", "Drift", "计算调整量"],
        ["任意", "以上均不满足", "Stable", "不动作"],
    ],
    0.8, 3.5, 11.7, 2.8, col_widths=[2, 3, 2.5, 4.2])

# DivergenceLevel
add_text(s, "发散等级 (DivergenceLevel)", 0.8, 6.5, 5, 0.4, size=18, bold=True, color=C_PRIMARY)
add_shape_box(s, 6, 6.5, 2, 0.5, C_GREEN, "Normal", text_size=12)
add_shape_box(s, 8.2, 6.5, 2, 0.5, C_YELLOW, "Degraded", text_size=12, text_color=C_DARK_TEXT)
add_shape_box(s, 10.4, 6.5, 2, 0.5, C_RED, "Stop", text_size=12)

add_notes(s, "状态机总结。四个压头状态：WarmUp/Stable/Drift/Outlier。三个发散等级：Normal/Degraded/Stop。")


# ============================================================
#  SLIDE 17 — 参数速查
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "09  关键参数速查表", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

add_table(s,
    ["参数", "默认值", "说明"],
    [
        ["UpperLimit / LowerLimit", "12.65 / 12.55 mm", "规格上下限"],
        ["UpperTolerance / LowerTolerance", "0.02 mm", "死区范围"],
        ["ControllerGain (K)", "0.5", "比例增益"],
        ["MaxStepPerAdjust", "0.01 mm", "单次最大步长"],
        ["DivergenceThreshold (T)", "0.02 mm", "发散判定阈值"],
        ["DegradedGainFactor", "0.5", "降级模式 K 折减"],
        ["DriftConfirmCycles (N)", "3", "漂移确认连续拍数"],
        ["DriftSlopeThreshold", "0.0001 mm/拍", "斜率最小幅度门限"],
        ["TrimPercentage", "5%", "截尾比例"],
        ["WindowSize", "200", "分析窗口大小"],
        ["MinSamplesPerEpoch", "50", "最少样本数"],
        ["SafetyUpperBound / LowerBound", "12.80 / 12.40 mm", "合理性范围"],
        ["SigmaSpikeFactor", "3.0", "σ 突增倍数"],
        ["DataStaleTimeoutSeconds", "60 s", "数据失活超时"],
    ],
    0.8, 1.3, 11.7, 5.8, col_widths=[3.5, 2.5, 5.7])
add_notes(s, "全部15个配置参数的默认值和说明。")


# ============================================================
#  SLIDE 18 — 代码模块对照
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_WHITE)
add_text(s, "09  代码模块对照表", 0.8, 0.4, 11.7, 0.8, size=32, bold=True, color=C_PRIMARY)

add_table(s,
    ["模块", "文件", "职责"],
    [
        ["数据读取", "CsvDataProvider.cs", "解析 CSV，返回有效数据列表"],
        ["统计计算", "TrimmedMeanCalculator.cs", "截尾均值、标准差、斜率"],
        ["发散分级", "DivergenceGrader.cs", "D 值计算，Normal/Degraded/Stop"],
        ["漂移检测", "DriftDetector.cs", "斜率历史维护，漂移确认"],
        ["安全护栏", "SafetyGuard.cs", "合理性阈、σ突增、数据失活"],
        ["调整量计算", "R2RController.cs", "Δ = K × Deviation，死区和限幅"],
        ["决策仲裁", "DecisionArbiter.cs", "五级优先级仲裁，产出 HeadDecision"],
        ["PLC 通信", "IPlcAdapter.cs", "调整/停机/位置读取接口"],
        ["UI 调度", "MainViewModel.cs", "数据刷新、仲裁调用、PLC 指令下发"],
    ],
    0.8, 1.3, 11.7, 4.5, col_widths=[2, 3.5, 6.2])

add_notes(s, "代码模块与职责的对照关系。每个模块可独立复用。")


# ============================================================
#  SLIDE 19 — 结束页
# ============================================================
s = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(s, C_PRIMARY)
add_text(s, "Thank You", 0.8, 2.5, 11.7, 1.2, size=48, bold=True, color=C_WHITE, align=PP_ALIGN.CENTER)
add_text(s, "压接高度自动调整系统  ·  内部培训", 0.8, 3.8, 11.7, 0.6, size=20, color=C_LIGHT_GRAY, align=PP_ALIGN.CENTER)
add_text(s, "如有疑问请联系控制系统团队", 0.8, 4.6, 11.7, 0.5, size=16, color=C_LIGHT_GRAY, align=PP_ALIGN.CENTER)
# 底部装饰线
line = s.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(4), Inches(3.65), Inches(5.3), Inches(0.04))
line.fill.solid(); line.fill.fore_color.rgb = C_ACCENT; line.line.fill.background()


# ── 保存 ──
output_path = r"E:\TE_connectivity\5_VibeCoding\PressHeightAutoAdjustment\docs\压接高度自动调整系统_培训.pptx"
prs.save(output_path)
print(f"PPT saved to: {output_path}")
