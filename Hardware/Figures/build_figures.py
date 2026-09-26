"""Build original English engineering schematics as SVG and PNG.

Run with Python 3 and Pillow. All geometry is schematic, not to scale.
Fonts: Arial on Windows; DejaVu Sans on Linux.
"""

from pathlib import Path
from html import escape
import math

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parent
W, H = 1600, 950
INK = "#172D35"
MUTED = "#52656B"
LINE = "#B7C6C8"
TEAL = "#007D83"
AMBER = "#B97015"
GREEN = "#208259"
LIGHT = "#F5F8F8"


def font(size, bold=False):
    candidates = [
        Path("C:/Windows/Fonts") / ("arialbd.ttf" if bold else "arial.ttf"),
        Path("/usr/share/fonts/truetype/dejavu")
        / ("DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"),
    ]
    for candidate in candidates:
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size)
    raise RuntimeError("Install Arial or DejaVu Sans to build the figures.")


class Drawing:
    def __init__(self):
        self.im = Image.new("RGB", (W, H), "white")
        self.draw = ImageDraw.Draw(self.im)
        self.svg = [
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">',
            '<rect width="1600" height="950" fill="white"/>',
        ]

    def line(self, points, color=TEAL, width=4, dashed=False):
        coords = " ".join(f"{x},{y}" for x, y in points)
        dash = ' stroke-dasharray="10 9"' if dashed else ""
        self.svg.append(f'<polyline points="{coords}" fill="none" stroke="{color}" stroke-width="{width}" stroke-linejoin="round"{dash}/>')
        if not dashed:
            self.draw.line(points, fill=color, width=width, joint="curve")
        else:
            for (x1, y1), (x2, y2) in zip(points, points[1:]):
                length = math.hypot(x2 - x1, y2 - y1)
                for s in range(0, int(length), 19):
                    a, b = s / length, min(s + 10, length) / length
                    self.draw.line([(x1 + a * (x2 - x1), y1 + a * (y2 - y1)),
                                    (x1 + b * (x2 - x1), y1 + b * (y2 - y1))], fill=color, width=width)

    def arrow(self, points, color=TEAL, width=4):
        self.line(points, color, width)
        x, y = points[-1]
        px, py = points[-2]
        angle = math.atan2(y - py, x - px)
        length, half = 17, 8
        head = [(x, y),
                (x - length * math.cos(angle) + half * math.sin(angle),
                 y - length * math.sin(angle) - half * math.cos(angle)),
                (x - length * math.cos(angle) - half * math.sin(angle),
                 y - length * math.sin(angle) + half * math.cos(angle))]
        self.polygon(head, color, color)

    def polygon(self, points, fill="white", stroke=INK, width=2):
        coords = " ".join(f"{x},{y}" for x, y in points)
        self.svg.append(f'<polygon points="{coords}" fill="{fill}" stroke="{stroke}" stroke-width="{width}"/>')
        self.draw.polygon(points, fill=fill, outline=stroke, width=width)

    def rect(self, x, y, w, h, fill="white", stroke=LINE, width=2):
        self.svg.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="{fill}" stroke="{stroke}" stroke-width="{width}"/>')
        self.draw.rectangle((x, y, x + w, y + h), fill=fill, outline=stroke, width=width)

    def ellipse(self, x, y, w, h, fill="white", stroke=INK, width=3):
        self.svg.append(f'<ellipse cx="{x+w/2}" cy="{y+h/2}" rx="{w/2}" ry="{h/2}" fill="{fill}" stroke="{stroke}" stroke-width="{width}"/>')
        self.draw.ellipse((x, y, x+w, y+h), fill=fill, outline=stroke, width=width)

    def text(self, x, y, text, size=30, color=INK, bold=False, anchor="middle"):
        lines = text.split("\n")
        f = font(size, bold)
        gap = size * 1.24
        for i, content in enumerate(lines):
            yy = y + (i - (len(lines) - 1) / 2) * gap
            self.svg.append(
                f'<text x="{x}" y="{yy}" fill="{color}" font-family="Arial, DejaVu Sans, sans-serif" font-size="{size}" font-weight="{700 if bold else 400}" text-anchor="{anchor}" dominant-baseline="central">{escape(content)}</text>'
            )
            align = {"middle": "mm", "start": "lm", "end": "rm"}[anchor]
            self.draw.text((x, yy), content, font=f, fill=color, anchor=align)

    def box(self, x, y, w, h, title, detail="", accent=TEAL):
        self.rect(x, y, w, h)
        self.line([(x, y), (x+w, y)], accent, 5)
        if detail:
            self.text(x+w/2, y+h*0.36, title, 32, bold=True)
            self.text(x+w/2, y+h*0.70, detail, 28, MUTED)
        else:
            self.text(x+w/2, y+h/2, title, 32, bold=True)

    def note(self, text="Schematic; not to scale."):
        self.text(1550, 925, text, 24, MUTED, anchor="end")

    def save(self, name):
        self.svg.append("</svg>")
        (ROOT / f"{name}.svg").write_text("\n".join(self.svg), encoding="utf-8")
        self.im.save(ROOT / f"{name}.png", dpi=(250, 250))


def architecture():
    d = Drawing()
    d.text(60, 70, "PHOTON ACQUISITION AND CONTROL", 27, MUTED, bold=True, anchor="start")
    d.box(790, 35, 300, 105, "MUS40M-G", "Parallel imaging")
    d.arrow([(940, 140), (940, 230)])
    d.text(1050, 182, "USB", 27, MUTED)
    for x, title, detail in [
        (60, "H10682", "PMT"), (425, "CH297", "Photon counter"),
        (790, "Host PC", "Acquire / forward"), (1155, "PYNQ-Z2", "Threshold / gate"),
    ]:
        d.box(x, 230, 300, 140, title, detail)
    for x in (360, 725, 1090):
        d.arrow([(x, 300), (x+65, 300)])
    d.text(575, 411, "USB / serial", 28, MUTED)
    d.text(1305, 190, "TCP / Ethernet", 28, MUTED)
    d.arrow([(1305, 370), (1305, 515)])
    d.text(1420, 458, "0-3.3 V", 28, TEAL)
    d.box(1155, 515, 300, 130, "DG1022Z", "Waveform source")
    d.box(700, 515, 300, 130, "ATA-2081", "HV amplifier", AMBER)
    d.box(245, 515, 300, 130, "DEP electrodes", "Sorting chip", AMBER)
    d.arrow([(1155, 580), (1000, 580)])
    d.text(1078, 537, "8 kHz", 28, TEAL)
    d.arrow([(700, 580), (545, 580)], AMBER)
    d.text(621, 537, "HV", 28, AMBER)
    d.text(395, 682, "Target / waste routing", 29, MUTED)
    d.line([(60, 725), (1540, 725)], LINE, 2)
    d.text(60, 760, "INDEPENDENT FLUID DELIVERY", 27, MUTED, bold=True, anchor="start")
    d.box(60, 805, 540, 85, "LSP02-3B dual-channel pump", accent=AMBER)
    d.arrow([(600, 847), (890, 847)], AMBER)
    d.text(745, 794, "CH1 aqueous\nCH2 oil", 27, AMBER)
    d.box(890, 805, 650, 85, "Generation / reinjection fluid paths", accent=AMBER)
    d.note()
    d.save("system_architecture")


def optical():
    d = Drawing()
    d.box(665, 35, 290, 100, "700 nm LED", "Bright-field")
    d.arrow([(810, 135), (810, 245)], AMBER)
    d.rect(575, 245, 450, 90)
    d.line([(575, 290), (1025, 290)], LINE, 2)
    d.text(1110, 290, "Microfluidic\nchannel", 29, MUTED, anchor="start")
    for cx in (724, 790, 856):
        d.ellipse(cx, 275, 38, 30, "#D7EBE8", TEAL, 2)
    d.ellipse(720, 420, 180, 35, "white", LINE, 3)
    d.text(960, 435, "Objective", 31, INK, bold=True, anchor="start")
    d.rect(685, 570, 250, 110, LIGHT, LINE, 2)
    d.line([(745, 651), (871, 588)], INK, 5)
    d.text(660, 704, "Dichroic routing", 30, INK, bold=True, anchor="end")
    d.text(660, 744, "DMLP505R excitation split", 27, MUTED, anchor="end")
    d.box(45, 568, 290, 110, "488 nm laser", "Excitation")
    d.arrow([(335, 623), (790, 623)], TEAL, 5)
    d.arrow([(790, 623), (790, 335)], TEAL, 5)
    d.arrow([(812, 335), (812, 570)], GREEN, 5)
    d.line([(812, 570), (812, 680)], GREEN, 5)
    d.arrow([(812, 680), (812, 782)], GREEN, 5)
    d.rect(690, 786, 240, 27, "#DDECE3", GREEN, 3)
    d.text(1080, 790, "Emission filter", 30, GREEN, anchor="start")
    d.text(1080, 828, "Matched to configuration", 26, MUTED, anchor="start")
    d.arrow([(812, 813), (812, 857)], GREEN, 5)
    d.box(665, 858, 290, 54, "H10682 PMT")
    d.arrow([(832, 335), (832, 610), (1190, 610)], AMBER, 5)
    d.rect(1190, 561, 25, 99, "#EEF1F2", MUTED, 3)
    d.text(1202, 516, "ND filter", 29, MUTED)
    d.arrow([(1215, 610), (1300, 610)], AMBER, 5)
    d.box(1300, 554, 260, 112, "MUS40M-G", "Camera")
    d.text(1130, 707, "Observation branch", 29, AMBER)
    for y, color, label in [(75, TEAL, "Excitation"), (122, GREEN, "Fluorescence"), (169, AMBER, "Observation")]:
        d.line([(50, y), (115, y)], color, 5)
        d.text(139, y, label, 28, MUTED, anchor="start")
    d.text(50, 837, "Functional optical paths", 27, MUTED, anchor="start")
    d.text(50, 874, "eGFP configuration", 27, MUTED, anchor="start")
    d.note()
    d.save("optical_path")


def wiring():
    d = Drawing()
    d.text(50, 40, "ACQUISITION AND HOST CONNECTIONS", 27, MUTED, bold=True, anchor="start")
    d.box(50, 90, 300, 100, "H10682 PMT", "Signal out")
    d.box(510, 90, 300, 100, "CH297", "Count input")
    d.arrow([(350, 140), (510, 140)])
    d.text(430, 101, "Coax", 27, MUTED)
    d.rect(1140, 90, 380, 370)
    d.line([(1140, 90), (1520, 90)], TEAL, 5)
    d.text(1330, 137, "Host PC", 36, bold=True)
    d.arrow([(810, 140), (1070, 140), (1070, 217), (1140, 217)])
    d.text(970, 97, "USB / serial", 28, MUTED)
    d.text(1330, 217, "CH297 serial port", 29, MUTED)
    d.text(970, 183, "19200, 8N1", 26, MUTED)
    d.box(50, 275, 350, 100, "MUS40M-G", "Camera")
    d.arrow([(400, 325), (1140, 325)])
    d.text(745, 286, "USB", 28, MUTED)
    d.text(1330, 325, "Camera USB port", 29, MUTED)
    d.text(1330, 409, "Pump USB port", 29, MUTED)
    d.text(50, 413, "LSP02-3B / COMM PORT", 28, INK, bold=True, anchor="start")
    d.rect(50, 443, 350, 142)
    d.text(225, 485, "Pin 5: RS485 A", 29, INK)
    d.text(225, 550, "Pin 4: RS485 B", 29, INK)
    d.rect(650, 443, 300, 142)
    d.text(800, 420, "CM253 adapter", 30, bold=True)
    d.text(690, 485, "A", 30, TEAL)
    d.text(690, 550, "B", 30, TEAL)
    d.text(843, 515, "USB", 30, MUTED)
    d.line([(400, 485), (650, 485)])
    d.line([(400, 550), (650, 550)])
    d.text(525, 452, "A to A", 27, MUTED)
    d.text(525, 584, "B to B", 27, MUTED)
    d.arrow([(950, 515), (1080, 515), (1080, 409), (1140, 409)])
    d.text(1050, 563, "RS485 / 8E1", 26, MUTED)
    d.arrow([(1495, 460), (1495, 650)])
    d.text(1380, 544, "Ethernet\nTCP 5000", 28, MUTED)
    d.text(1330, 613, "PYNQ-Z2 / PMOD B", 30, bold=True)
    d.rect(1140, 650, 380, 110)
    d.text(1330, 681, "Pin 1: 0-3.3 V", 29, TEAL)
    d.text(1330, 731, "GND", 29, MUTED)
    d.text(822, 613, "DG1022Z", 31, bold=True)
    d.rect(655, 650, 335, 110)
    d.text(822, 681, "External trigger", 29, TEAL)
    d.text(822, 731, "Signal ground", 29, MUTED)
    d.arrow([(1140, 681), (990, 681)])
    d.line([(1140, 731), (990, 731)], MUTED, 3)
    d.text(1065, 651, "Gate", 26, TEAL)
    d.text(1065, 759, "GND", 26, MUTED)
    d.arrow([(822, 760), (822, 818)])
    d.text(978, 797, "8 kHz / BNC", 27, MUTED)
    d.box(655, 818, 335, 92, "ATA-2081", "Signal in / HV out", AMBER)
    d.box(50, 818, 345, 92, "DEP electrodes", "Sorting chip", AMBER)
    d.arrow([(655, 864), (395, 864)], AMBER, 6)
    d.text(525, 828, "HV output", 28, AMBER)
    d.text(1220, 852, "Insulated HV lead\nSeparate from logic wiring", 27, AMBER)
    d.note()
    d.save("wiring_map")


def workflow():
    d = Drawing()
    d.text(60, 76, "DROPLET PREPARATION", 28, MUTED, bold=True, anchor="start")
    steps = [
        (60, "01", "Generate", "Water-in-oil droplets", "CH1 aqueous\nCH2 oil"),
        (575, "02", "Incubate", "30 \u00b0C / 16 h", "Collect droplets\nCulture in tubes"),
        (1090, "03", "Reinject", "Sorting-chip inlet", "Prime with fluorinated oil\nDeliver cultured droplets"),
    ]
    for x, num, title, detail, note in steps:
        d.text(x, 159, num, 38, AMBER, bold=True, anchor="start")
        d.box(x, 207, 450, 132, title, detail, AMBER)
        d.text(x+225, 404, note, 29, MUTED)
    d.arrow([(510, 273), (575, 273)], AMBER)
    d.arrow([(1025, 273), (1090, 273)], AMBER)
    d.arrow([(1315, 447), (1315, 496), (30, 496), (30, 724), (60, 724)], AMBER)
    d.text(60, 560, "FLUORESCENCE SELECTION", 28, MUTED, bold=True, anchor="start")
    stages = [
        (60, "04", "Detect", "PMT / CH297", "Photon-count samples"),
        (466, "05", "Gate", "PYNQ-Z2", "Threshold + hysteresis"),
        (872, "06", "Sort", "DEP actuation", "Target / waste routing"),
        (1278, "07", "Record", "Session archive", "Video + counts + events"),
    ]
    for x, num, title, detail, note in stages:
        width = 325 if x < 1200 else 262
        d.text(x, 610, num, 33, TEAL, bold=True, anchor="start")
        d.box(x, 658, width, 132, title, detail)
        if x >= 1278:
            d.text(x+width/2, 845, "Video + counts\n+ events", 28, MUTED)
        else:
            if title == "Gate":
                note = "Threshold\n+ hysteresis"
            elif title == "Detect":
                note = "Photon-count\nsamples"
            d.text(x+width/2, 845, note, 28, MUTED)
    for x in (385, 791, 1197):
        d.arrow([(x, 724), (x+81, 724)])
    d.note()
    d.save("workflow")


if __name__ == "__main__":
    for builder in (architecture, optical, wiring, workflow):
        builder()
    print("Built 4 original schematics: SVG + 1600 x 950 PNG.")
