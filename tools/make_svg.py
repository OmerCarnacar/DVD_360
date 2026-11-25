"""Generates docs/dvd360-animated.svg (README hero) and docs/logo.svg — pure vector, SMIL animation, no scripts."""
import math, random

random.seed(360)
C = 200.0            # centre
R = 168.0            # disc radius
R0, R1 = 0.30 * R, 0.985 * R
RINGS, SEGS = 26, 72
CYCLE = 9.0          # seconds per loop

def sector(ri, ro, a0, a1):
    def p(r, a): return f"{C + r * math.cos(a):.2f},{C + r * math.sin(a):.2f}"
    large = 1 if a1 - a0 > math.pi else 0
    return (f"M{p(ro, a0)}A{ro:.2f},{ro:.2f} 0 {large} 1 {p(ro, a1)}"
            f"L{p(ri, a1)}A{ri:.2f},{ri:.2f} 0 {large} 0 {p(ri, a0)}Z")

# pattern like a scratched disc: errors cluster in bands and streaks
green, red = [], []
area = R1 * R1 - R0 * R0
for ring in range(RINGS):
    ri = math.sqrt(R0 * R0 + area * ring / RINGS)
    ro = math.sqrt(R0 * R0 + area * (ring + 1) / RINGS)
    band = 0.55 if 9 <= ring <= 14 else 0.08 if ring < 6 else 0.22
    seg = 0
    while seg < SEGS:
        streak = math.sin(seg / SEGS * 2 * math.pi * 3 + ring * 0.4) > 0.75
        bad = random.random() < (band + (0.25 if streak else 0))
        end = seg + 1
        while end < SEGS and random.random() < 0.55 and (random.random() < band) == bad:
            end += 1
        a0 = -math.pi + 2 * math.pi * seg / SEGS
        a1 = -math.pi + 2 * math.pi * end / SEGS
        (red if bad else green).append(sector(ri, ro, a0, a1))
        seg = end

def ring_len(r): return 2 * math.pi * r

GR, ER = 190.0, 182.0                     # progress rings
fill_final, err_final = 0.86, 0.12
kt = "0;0.72;0.9;1"                       # read, hold, fade/reset

svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 400" width="400" height="400" role="img" aria-label="DVD 360: a disc being read sector by sector">
<title>DVD 360</title>
<defs>
  <radialGradient id="silver" cx="50%" cy="50%" r="50%">
    <stop offset="30%" stop-color="#aeb8c6"/><stop offset="100%" stop-color="#dfe5ee"/>
  </radialGradient>
  <radialGradient id="hub" cx="50%" cy="45%" r="60%">
    <stop offset="0%" stop-color="#2b3548"/><stop offset="100%" stop-color="#111826"/>
  </radialGradient>
  <linearGradient id="sheen" x1="0" y1="0" x2="1" y2="1">
    <stop offset="0%" stop-color="#ffffff" stop-opacity=".55"/>
    <stop offset="32%" stop-color="#ff7ad9" stop-opacity=".10"/>
    <stop offset="48%" stop-color="#7ad7ff" stop-opacity=".28"/>
    <stop offset="62%" stop-color="#b7ff7a" stop-opacity=".12"/>
    <stop offset="80%" stop-color="#ffffff" stop-opacity="0"/>
  </linearGradient>
  <linearGradient id="okRing" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#5ec8ff"/><stop offset="100%" stop-color="#2ed573"/></linearGradient>
  <linearGradient id="errRing" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#ff4d5e"/><stop offset="100%" stop-color="#ff8a6b"/></linearGradient>
  <filter id="glow" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="3" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
  <filter id="shadow" x="-20%" y="-20%" width="140%" height="140%"><feDropShadow dx="0" dy="6" stdDeviation="9" flood-color="#000" flood-opacity=".45"/></filter>
  <mask id="readSoFar" maskUnits="userSpaceOnUse">
    <circle cx="{C}" cy="{C}" r="{R0:.1f}" fill="#fff">
      <animate attributeName="r" dur="{CYCLE}s" repeatCount="indefinite" keyTimes="{kt}" values="{R0:.1f};{R1:.1f};{R1:.1f};{R0:.1f}" calcMode="spline" keySplines=".4 0 .6 1;0 0 1 1;.4 0 .6 1"/>
    </circle>
  </mask>
</defs>

<!-- progress rings: green = filled, red = error area -->
<circle cx="{C}" cy="{C}" r="{GR}" fill="none" stroke="#1c2638" stroke-width="6"/>
<circle cx="{C}" cy="{C}" r="{ER}" fill="none" stroke="#241a22" stroke-width="6"/>
<circle cx="{C}" cy="{C}" r="{GR}" fill="none" stroke="url(#okRing)" stroke-width="6" stroke-linecap="round" filter="url(#glow)"
        transform="rotate(-90 {C} {C})" stroke-dasharray="{ring_len(GR):.1f}" stroke-dashoffset="{ring_len(GR):.1f}">
  <animate attributeName="stroke-dashoffset" dur="{CYCLE}s" repeatCount="indefinite" keyTimes="{kt}"
           values="{ring_len(GR):.1f};{ring_len(GR) * (1 - fill_final):.1f};{ring_len(GR) * (1 - fill_final):.1f};{ring_len(GR):.1f}"/>
</circle>
<circle cx="{C}" cy="{C}" r="{ER}" fill="none" stroke="url(#errRing)" stroke-width="6" stroke-linecap="round" filter="url(#glow)"
        transform="rotate(-90 {C} {C})" stroke-dasharray="{ring_len(ER):.1f}" stroke-dashoffset="{ring_len(ER):.1f}">
  <animate attributeName="stroke-dashoffset" dur="{CYCLE}s" repeatCount="indefinite" keyTimes="{kt}"
           values="{ring_len(ER):.1f};{ring_len(ER) * (1 - err_final):.1f};{ring_len(ER) * (1 - err_final):.1f};{ring_len(ER):.1f}"/>
</circle>

<!-- the disc -->
<g filter="url(#shadow)">
  <g>
    <animateTransform attributeName="transform" type="rotate" from="0 {C} {C}" to="360 {C} {C}" dur="2.4s" repeatCount="indefinite"/>
    <circle cx="{C}" cy="{C}" r="{R}" fill="#c3cbd6" fill-opacity=".55"/>
    <circle cx="{C}" cy="{C}" r="{R1:.1f}" fill="url(#silver)"/>
    <g mask="url(#readSoFar)">
      <path fill="#2ed573" d="{''.join(green)}"/>
      <path fill="#ff4d5e" d="{''.join(red)}"/>
    </g>
  </g>
  <circle cx="{C}" cy="{C}" r="{R1:.1f}" fill="url(#sheen)"/>
  <circle cx="{C}" cy="{C}" r="{R - 0.5:.1f}" fill="none" stroke="#fff" stroke-opacity=".5" stroke-width="1"/>
  <circle cx="{C}" cy="{C}" r="{0.25 * R:.1f}" fill="#b4bcc8" fill-opacity=".35"/>
  <circle cx="{C}" cy="{C}" r="{0.235 * R:.1f}" fill="url(#hub)" stroke="#465572" stroke-width="1.5"/>
  <circle cx="{C}" cy="{C}" r="{0.105 * R:.1f}" fill="#0e131d" stroke="#465572" stroke-width="1"/>
</g>

<!-- laser head riding outwards with the read position -->
<g filter="url(#glow)">
  <line stroke="#5ec8ff" stroke-opacity=".75" stroke-width="3" stroke-linecap="round" x2="{C + 205 * math.cos(math.pi / 4):.1f}" y2="{C + 205 * math.sin(math.pi / 4):.1f}"
        x1="{C + R0 * math.cos(math.pi / 4):.1f}" y1="{C + R0 * math.sin(math.pi / 4):.1f}">
    <animate attributeName="x1" dur="{CYCLE}s" repeatCount="indefinite" keyTimes="{kt}" calcMode="spline" keySplines=".4 0 .6 1;0 0 1 1;.4 0 .6 1"
             values="{C + R0 * math.cos(math.pi / 4):.1f};{C + R1 * math.cos(math.pi / 4):.1f};{C + R1 * math.cos(math.pi / 4):.1f};{C + R0 * math.cos(math.pi / 4):.1f}"/>
    <animate attributeName="y1" dur="{CYCLE}s" repeatCount="indefinite" keyTimes="{kt}" calcMode="spline" keySplines=".4 0 .6 1;0 0 1 1;.4 0 .6 1"
             values="{C + R0 * math.sin(math.pi / 4):.1f};{C + R1 * math.sin(math.pi / 4):.1f};{C + R1 * math.sin(math.pi / 4):.1f};{C + R0 * math.sin(math.pi / 4):.1f}"/>
  </line>
  <circle r="6" fill="#ffffff" stroke="#5ec8ff" stroke-width="3">
    <animate attributeName="cx" dur="{CYCLE}s" repeatCount="indefinite" keyTimes="{kt}" calcMode="spline" keySplines=".4 0 .6 1;0 0 1 1;.4 0 .6 1"
             values="{C + R0 * math.cos(math.pi / 4):.1f};{C + R1 * math.cos(math.pi / 4):.1f};{C + R1 * math.cos(math.pi / 4):.1f};{C + R0 * math.cos(math.pi / 4):.1f}"/>
    <animate attributeName="cy" dur="{CYCLE}s" repeatCount="indefinite" keyTimes="{kt}" calcMode="spline" keySplines=".4 0 .6 1;0 0 1 1;.4 0 .6 1"
             values="{C + R0 * math.sin(math.pi / 4):.1f};{C + R1 * math.sin(math.pi / 4):.1f};{C + R1 * math.sin(math.pi / 4):.1f};{C + R0 * math.sin(math.pi / 4):.1f}"/>
    <animate attributeName="r" values="5;7.5;5" dur="1.1s" repeatCount="indefinite"/>
  </circle>
</g>

<text x="{C}" y="{C + 7}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="21" font-weight="700" fill="#ffffff">360</text>
</svg>
'''
open(r'D:\GLOBAL\DVD_360\docs\dvd360-animated.svg', 'w', encoding='utf-8').write(svg)

# ---------- static logo
logo = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256" role="img" aria-label="DVD 360 logo">
<title>DVD 360</title>
<defs>
  <radialGradient id="s" cx="50%" cy="50%" r="50%"><stop offset="30%" stop-color="#aeb8c6"/><stop offset="100%" stop-color="#e6ebf2"/></radialGradient>
  <linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#5ec8ff"/><stop offset="100%" stop-color="#2ed573"/></linearGradient>
  <linearGradient id="sh" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="#fff" stop-opacity=".6"/><stop offset="45%" stop-color="#7ad7ff" stop-opacity=".25"/><stop offset="75%" stop-color="#fff" stop-opacity="0"/></linearGradient>
</defs>
<circle cx="128" cy="128" r="116" fill="url(#s)"/>
<path d="M128 12 A116 116 0 1 1 26.4 184 L66.8 161 A70 70 0 1 0 128 58 Z" fill="url(#g)"/>
<path d="M26.4 184 A116 116 0 0 1 12.6 141 L58.4 136 A70 70 0 0 0 66.8 161 Z" fill="#ff4d5e"/>
<circle cx="128" cy="128" r="116" fill="url(#sh)"/>
<circle cx="128" cy="128" r="40" fill="#141b29" stroke="#465572" stroke-width="3"/>
<circle cx="128" cy="128" r="16" fill="#0e131d"/>
<circle cx="128" cy="128" r="122" fill="none" stroke="url(#g)" stroke-width="8"/>
</svg>
'''
open(r'D:\GLOBAL\DVD_360\docs\logo.svg', 'w', encoding='utf-8').write(logo)
import os
print(os.path.getsize(r'D:\GLOBAL\DVD_360\docs\dvd360-animated.svg'), 'bytes animated;', os.path.getsize(r'D:\GLOBAL\DVD_360\docs\logo.svg'), 'bytes logo')
