#!/usr/bin/env python3
"""Generate the README art in docs/assets/: animated hero, run-flow and architecture SVGs (light + dark, shown through
<picture>). Usage: python3 scripts/readme-assets.py [out-dir]

The SVGs animate with SMIL only (no scripts, no external fonts: GitHub renders them through <img>). The snippets, values,
error texts and timings drawn here are real output of the C# runtime (Roslyn 4.8, .NET 10); keep them in sync with the
README and the contract.
"""
import os
import sys

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "docs", "assets")
MONO = "ui-monospace,SFMono-Regular,'SF Mono',Menlo,Consolas,'Liberation Mono',monospace"
SANS = "-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif"

PALETTES = {
    "dark": dict(bg="#0d1117", card="#161b22", card2="#1c2330", bd="#30363d", tx="#e6edf3", mu="#8b949e",
                 ac="#39d0d8", vi="#a371f7", gr="#3fb950", orr="#f0883e", pk="#ff7b72", ye="#e3b341", g1="#3fb950", g2="#39d0d8"),
    "light": dict(bg="#ffffff", card="#f6f8fa", card2="#eef2f6", bd="#d0d7de", tx="#1f2328", mu="#59636e",
                  ac="#08707a", vi="#7341c9", gr="#1a7f37", orr="#a94400", pk="#cf222e", ye="#8a5c00", g1="#1a7f37", g2="#08707a"),
}
CW = 0.602  # monospace advance per em


def style(p):
    return f"""<style>
.bg{{fill:{p['bg']}}}.card{{fill:{p['card']}}}.card2{{fill:{p['card2']}}}.bd{{stroke:{p['bd']}}}
.tx{{fill:{p['tx']}}}.mu{{fill:{p['mu']}}}.ac{{fill:{p['ac']}}}.vi{{fill:{p['vi']}}}.gr{{fill:{p['gr']}}}
.or{{fill:{p['orr']}}}.pk{{fill:{p['pk']}}}.ye{{fill:{p['ye']}}}
.sac{{stroke:{p['ac']}}}.svi{{stroke:{p['vi']}}}.smu{{stroke:{p['mu']}}}.sgr{{stroke:{p['gr']}}}.spk{{stroke:{p['pk']}}}.sye{{stroke:{p['ye']}}}
.mono{{font-family:{MONO}}}.sans{{font-family:{SANS}}}
</style>"""


def svg(w, h, p, body, title):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" '
            f'aria-label="{title}"><title>{title}</title>{style(p)}'
            f'<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{p["g1"]}"/>'
            f'<stop offset="1" stop-color="{p["g2"]}"/></linearGradient></defs>{body}</svg>')


def kt(*xs):
    return ";".join(f"{x:.4f}".rstrip("0").rstrip(".") if x not in (0, 1) else str(int(x)) for x in xs)


def frame(W, H):
    return [f'<rect class="bg" width="{W}" height="{H}"/>',
            f'<rect class="card bd" x="1" y="1" width="{W-2}" height="{H-2}" rx="18" stroke-width="1.5"/>']


def spans(parts):
    return "".join(f'<tspan class="{c}">{t}</tspan>' for c, t in parts)


def plen(parts):
    return sum(len(t.replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&")) for _, t in parts)


# ---------------------------------------------------------------- hero
def hero(p):
    W, H, D = 1200, 320, 11.0
    b = frame(W, H)
    # faint background: brackets and a caret, pulsing
    for i, (x, y, t) in enumerate(((530, 64, "{ }"), (560, 140, "( )"), (522, 214, "=&gt;"), (566, 282, "[ ]"), (580, 92, ";"))):
        b.append(f'<text x="{x}" y="{y}" class="mono ac" font-size="22" opacity=".25">{t}<animate attributeName="opacity" '
                 f'values=".1;.55;.1" dur="3.4s" begin="{i*0.6:.2f}s" repeatCount="indefinite"/></text>')
    # wordmark
    b.append(f'<text x="58" y="140" class="sans" font-size="86" font-weight="800" fill="url(#g)" letter-spacing="-2">Code-Exec</text>')
    b.append(f'<text x="62" y="186" class="sans tx" font-size="26" font-weight="600">Run code inside the live host</text>')
    b.append(f'<text x="62" y="222" class="sans mu" font-size="17">Short C# and Python snippets from an AI agent,</text>')
    b.append(f'<text x="62" y="246" class="sans mu" font-size="17">one contract, guarded, result back as text.</text>')
    x = 62
    for label, cls in (("C#", "vi"), ("Python", "ye"), ("Roslyn 3.8 → 5.x", "ac")):
        w = 22 + len(label) * 7.9
        b.append(f'<rect x="{x}" y="268" width="{w:.0f}" height="26" rx="13" class="card2 bd" stroke-width="1"/>'
                 f'<text x="{x + w/2:.0f}" y="286" text-anchor="middle" class="mono {cls}" font-size="13" font-weight="600">{label}</text>')
        x += w + 10
    # terminal
    tx0, ty0, tw, th = 610, 30, 560, 260
    b.append(f'<rect x="{tx0}" y="{ty0}" width="{tw}" height="{th}" rx="12" class="bg bd" stroke-width="1.2"/>')
    for i, c in enumerate(("pk", "ye", "gr")):
        b.append(f'<circle cx="{tx0 + 20 + i*16}" cy="{ty0 + 18}" r="5" class="{c}" opacity=".85"/>')
    b.append(f'<text x="{tx0 + tw - 16}" y="{ty0 + 22}" text-anchor="end" class="mono mu" font-size="11">script-execute · C#</text>')

    lines = [
        [("ac", "› "), ("vi", "var "), ("tx", "sb = "), ("vi", "new "), ("ac", "StringBuilder"), ("tx", "("), ("gr", '"ab"'), ("tx", ");")],
        [("tx", "  "), ("vi", "class "), ("ac", "Helper "), ("tx", "{ "), ("vi", "public static int "), ("tx", "Twice("),
         ("vi", "int "), ("tx", "v) =&gt; v * "), ("or", "2"), ("tx", "; }")],
        [("tx", "  sb.Append(Helper.Twice("), ("or", "21"), ("tx", "));")],
        [("tx", "  sb.ToString()"), ("mu", "   // last expression = result")],
    ]
    fs = 13.5
    cw = fs * CW
    lx = tx0 + 20
    ly = [ty0 + 58 + i * 22 for i in range(len(lines))]
    # typewriter windows on a shared loop
    win = [(0.03, 0.12), (0.13, 0.27), (0.28, 0.35), (0.36, 0.42)]
    end = 0.95
    fade = f'<animate attributeName="opacity" values="1;1;0" keyTimes="{kt(0,end,1)}" dur="{D}s" repeatCount="indefinite"/>'
    g = [f'<g>{fade}']
    for i, (parts, (ta, tb)) in enumerate(zip(lines, win)):
        n = plen(parts)
        w = n * cw + 4
        b.append(f'<clipPath id="c{i}"><rect x="{lx-2}" y="{ly[i]-15}" height="21" width="{w:.0f}"><animate attributeName="width" '
                 f'values="0;0;{w:.0f};{w:.0f}" keyTimes="{kt(0,ta,tb,1)}" dur="{D}s" repeatCount="indefinite"/></rect></clipPath>')
        g.append(f'<text x="{lx}" y="{ly[i]}" class="mono" font-size="{fs}" textLength="{n*cw:.0f}" lengthAdjust="spacing" '
                 f'clip-path="url(#c{i})" xml:space="preserve">{spans(parts)}</text>')
    g.append('</g>')
    b.extend(g)
    # cursor follows the typing
    cx, cy, ck = [lx], [ly[0] - 12], [0]
    for i, (parts, (ta, tb)) in enumerate(zip(lines, win)):
        n = plen(parts)
        cx += [lx, lx + n * cw]
        cy += [ly[i] - 12, ly[i] - 12]
        ck += [ta, tb]
    cx.append(cx[-1]); cy.append(cy[-1]); ck.append(1)
    b.append(f'<rect width="8" height="16" class="ac" x="{lx}" y="{ly[0]-12}">'
             f'<animate attributeName="x" values="{";".join(f"{v:.0f}" for v in cx)}" keyTimes="{kt(*ck)}" dur="{D}s" repeatCount="indefinite"/>'
             f'<animate attributeName="y" values="{";".join(f"{v:.0f}" for v in cy)}" keyTimes="{kt(*ck)}" calcMode="discrete" dur="{D}s" repeatCount="indefinite"/>'
             f'<animate attributeName="opacity" values="1;0;1" dur="1s" repeatCount="indefinite"/></rect>')
    # divider + "running" hint
    dy = ty0 + 150
    b.append(f'<line x1="{lx}" y1="{dy}" x2="{tx0+tw-20}" y2="{dy}" class="smu" stroke-width="1" stroke-dasharray="3 5" opacity=".6"/>')
    b.append(f'<text x="{lx}" y="{dy+26}" class="mono ac" font-size="{fs}" opacity="0">compiling against the host …'
             f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,0.44,0.45,0.52,0.53,1)}" dur="{D}s" repeatCount="indefinite"/></text>')
    # result (real output)
    rows = [
        [("gr", "✓ success   "), ("mu", "value        "), ("tx", "ab42")],
        [("mu", "            autoImported "), ("ac", "System.Text")],
        [("mu", "            compile "), ("or", "609"), ("mu", " ms · execute "), ("or", "2"), ("mu", " ms")],
    ]
    for i, parts in enumerate(rows):
        a = 0.53 + i * 0.04
        b.append(f'<text x="{lx}" y="{dy + 26 + i*23}" class="mono" font-size="{fs}" xml:space="preserve" opacity="0">{spans(parts)}'
                 f'<animate attributeName="opacity" values="0;0;1;1;0" keyTimes="{kt(0,a,a+0.03,end,1)}" dur="{D}s" repeatCount="indefinite"/></text>')
    return svg(W, H, p, "".join(b), "Code-Exec: a C# snippet typed into an agent tool and the result it returns")


# ---------------------------------------------------------------- run flow
def flow(p):
    W, H, D = 1200, 400, 12.0
    b = frame(W, H)
    CY = 196

    def box(x, y, w, h, title, subs, tcls="tx"):
        s = f'<rect x="{x}" y="{y-h/2}" width="{w}" height="{h}" rx="12" class="card2 bd" stroke-width="1.3"/>'
        top = y - (len(subs) * 17) / 2 - 2
        s += f'<text x="{x+w/2}" y="{top+4:.0f}" text-anchor="middle" class="sans {tcls}" font-size="15" font-weight="700">{title}</text>'
        for k, sub in enumerate(subs):
            s += f'<text x="{x+w/2}" y="{top + 24 + 17*k:.0f}" text-anchor="middle" class="mono mu" font-size="12.5">{sub}</text>'
        return s

    for x, t in ((30, "AGENT"), (240, "PREPARE"), (500, "RUN, INSIDE THE HOST"), (930, "RESULT")):
        b.append(f'<text x="{x}" y="40" class="sans mu" font-size="12" font-weight="700" letter-spacing="1.5">{t}</text>')

    # snippet box with cycling code
    b.append(f'<rect x="30" y="{CY-56}" width="170" height="112" rx="12" class="bg bd" stroke-width="1.2"/>')
    b.append(f'<text x="44" y="{CY-32}" class="mono mu" font-size="12">snippet.cs</text>')
    runs = [  # code lines, color, outcome row index, start time
        (["sb.Append(…);", "sb.ToString()"], "gr", 0, 0.00),
        (["int F(int n) =>", " 1 + F(n - 1);", "F(10_000_000)"], "pk", 1, 0.33),
        (["while (true)", "{ }"], "ye", 2, 0.66),
    ]
    for code, c, _, t0 in runs:
        t1 = t0 + 0.31
        anim = f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,t0+0.001,t0+0.02,t1-0.02,t1,1)}" dur="{D}s" repeatCount="indefinite"/>'
        lines = "".join(f'<tspan x="44" dy="{0 if k == 0 else 18}">{l.replace(">", "&gt;")}</tspan>' for k, l in enumerate(code))
        b.append(f'<text y="{CY-8}" class="mono tx" font-size="12.5" opacity="0" xml:space="preserve">{lines}{anim}</text>')

    b.append(box(240, CY, 210, 112, "Prepare", ["wrap statements", "auto-import names", "inject guards", "compile · your Roslyn"], "ac"))
    b.append(box(500, CY, 210, 112, "Run", ["host's main thread", "own load context", "stack + time budget"], "vi"))
    b.append(box(756, CY - 92, 130, 62, "Convert", ["ValueConverter"], "tx"))
    outs = [(CY - 92, "gr", "✓ value", "ab42"), (CY + 8, "pk", "Runtime", "Recursion too deep"), (CY + 100, "ye", "Timeout", "budget spent")]
    for y, c, k, v in outs:
        b.append(f'<rect x="930" y="{y-26}" width="240" height="52" rx="12" class="card2 bd" stroke-width="1.3"/>'
                 f'<text x="948" y="{y-3}" class="mono {c}" font-size="14" font-weight="700">{k}</text>'
                 f'<text x="948" y="{y+16}" class="mono mu" font-size="12.5">{v}</text>')
    # guard markers in the run box
    paths = {
        "a": "M200,%d L240,%d" % (CY, CY),
        "b": "M450,%d L500,%d" % (CY, CY),
        "ok1": f"M710,{CY} C735,{CY} 731,{CY-92} 756,{CY-92}",
        "ok2": f"M886,{CY-92} L930,{CY-92}",
        "rt": f"M710,{CY} C820,{CY} 820,{CY+8} 930,{CY+8}",
        "to": f"M710,{CY} C820,{CY} 820,{CY+100} 930,{CY+100}",
    }
    for d in paths.values():
        b.append(f'<path d="{d}" fill="none" class="smu" stroke-width="1.6" opacity=".7"/>')

    def packet(d, ts, te, c):
        return (f'<circle r="6" class="{c}" opacity="0"><animateMotion path="{d}" keyPoints="0;0;1;1" keyTimes="{kt(0,ts,te,1)}" calcMode="linear" dur="{D}s" repeatCount="indefinite"/>'
                f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,ts,ts+0.005,te-0.005,te,1)}" dur="{D}s" repeatCount="indefinite"/></circle>')

    def glow(x, y, w, h, t, cls="sac", hold=0.05):
        return (f'<rect x="{x}" y="{y-h/2}" width="{w}" height="{h}" rx="12" fill="none" class="{cls}" stroke-width="2" opacity="0">'
                f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,t,t+0.01,t+hold,t+hold+0.02,1)}" dur="{D}s" repeatCount="indefinite"/></rect>')

    for code, c, out, t0 in runs:
        b.append(packet(paths["a"], t0 + 0.03, t0 + 0.05, c))
        b.append(glow(240, CY, 210, 112, t0 + 0.05))
        b.append(packet(paths["b"], t0 + 0.10, t0 + 0.12, c))
        b.append(glow(500, CY, 210, 112, t0 + 0.12, "svi", 0.06))
        if out == 0:
            b.append(packet(paths["ok1"], t0 + 0.19, t0 + 0.22, c))
            b.append(glow(756, CY - 92, 130, 62, t0 + 0.22, "sac", 0.02))
            b.append(packet(paths["ok2"], t0 + 0.245, t0 + 0.26, c))
            t_hit = t0 + 0.26
        else:
            b.append(packet(paths["rt" if out == 1 else "to"], t0 + 0.19, t0 + 0.24, c))
            t_hit = t0 + 0.24
            label = "stack guard" if out == 1 else "time budget"
            b.append(f'<text x="605" y="{CY+78}" text-anchor="middle" class="mono {c}" font-size="13" font-weight="700" opacity="0">⛔ {label}'
                     f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,t0+0.15,t0+0.16,t0+0.24,t0+0.25,1)}" dur="{D}s" repeatCount="indefinite"/></text>')
        y = outs[out][0]
        b.append(glow(930, y, 240, 52, t_hit, {"gr": "sgr", "pk": "spk", "ye": "sye"}[c], 0.05))
    b.append(f'<text x="600" y="{H-28}" text-anchor="middle" class="sans mu" font-size="14">'
             f'Runaway recursion and endless loops stop the <tspan class="tx">run</tspan>, never the host; the next snippet runs normally.</text>')
    return svg(W, H, p, "".join(b), "How a snippet runs: prepared and compiled, run inside the host under guards, and returned as a value, a runtime error or a timeout")


# ---------------------------------------------------------------- architecture
def architecture(p):
    W, H, D = 1200, 330, 7.0
    b = frame(W, H)
    CY = 158

    def box(x, y, w, h, title, sub, tcls="tx"):
        s = f'<rect x="{x}" y="{y-h/2}" width="{w}" height="{h}" rx="12" class="card2 bd" stroke-width="1.3"/>'
        s += f'<text x="{x+w/2}" y="{y-4 if sub else y+5}" text-anchor="middle" class="sans {tcls}" font-size="15" font-weight="700">{title}</text>'
        if sub:
            s += f'<text x="{x+w/2}" y="{y+16}" text-anchor="middle" class="mono mu" font-size="12.5">{sub}</text>'
        return s

    b.append(box(30, CY, 170, 70, "Agent tool call", "code · timeoutMs", "ac"))
    b.append(box(260, CY, 210, 96, "One contract", "spec/contract.md", "vi"))
    b.append(f'<text x="365" y="{CY+35}" text-anchor="middle" class="mono mu" font-size="12.5">+ fixtures/golden</text>')
    rt = [(CY - 70, "C# runtime", "Core + Roslyn adapter", "vi"), (CY + 70, "Python runtime", "code_exec, stdlib only", "ye")]
    for y, t, s, c in rt:
        b.append(box(540, y, 230, 64, t, s, c))
    hosts = [(CY - 70, "Unity · Godot · .NET", "Mono · CoreCLR · net48"), (CY + 70, "Unreal Editor", "any embedded Python")]
    for y, t, s in hosts:
        b.append(box(840, y, 200, 64, t, s, "tx"))
    b.append(box(1080, CY, 100, 70, "Result", "JSON", "gr"))
    b.append(f'<text x="600" y="{H-22}" text-anchor="middle" class="sans mu" font-size="13.5">'
             f'One request/response shape and one set of golden cases; both runtimes are tested against them.</text>')
    paths = ["M200,%d L260,%d" % (CY, CY)]
    for y, *_ in rt:
        paths.append(f"M470,{CY} C505,{CY} 505,{y} 540,{y}")
        paths.append(f"M770,{y} L840,{y}")
        paths.append(f"M1040,{y} C1060,{y} 1060,{CY} 1080,{CY}")
    for d in paths:
        b.append(f'<path d="{d}" fill="none" class="smu" stroke-width="1.6" opacity=".7"/>')
    seq = [(paths[0], 0.02, 0.12, "ac")]
    for i, (y, _, _, c) in enumerate(rt):
        seq.append((paths[1 + 3*i], 0.30, 0.42, c))
        seq.append((paths[2 + 3*i], 0.52, 0.62, c))
        seq.append((paths[3 + 3*i], 0.72, 0.82, "gr"))
    for d, ts, te, c in seq:
        b.append(f'<circle r="6" class="{c}" opacity="0"><animateMotion path="{d}" keyPoints="0;0;1;1" keyTimes="{kt(0,ts,te,1)}" calcMode="linear" dur="{D}s" repeatCount="indefinite"/>'
                 f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,ts,ts+0.01,te-0.01,te,1)}" dur="{D}s" repeatCount="indefinite"/></circle>')
    glows = [((260, CY, 210, 96), 0.12), ((1080, CY, 100, 70), 0.82)]
    for y, *_ in rt:
        glows.append(((540, y, 230, 64), 0.42))
    for y, *_ in hosts:
        glows.append(((840, y, 200, 64), 0.62))
    for (x, y, w, h), t in glows:
        b.append(f'<rect x="{x}" y="{y-h/2}" width="{w}" height="{h}" rx="12" fill="none" class="sac" stroke-width="2" opacity="0">'
                 f'<animate attributeName="opacity" values="0;0;1;0;0" keyTimes="{kt(0,t-0.01,t+0.02,t+0.16,1)}" dur="{D}s" repeatCount="indefinite"/></rect>')
    return svg(W, H, p, "".join(b), "Code-Exec architecture: one contract and golden cases, a C# runtime for .NET hosts and a Python runtime for Unreal and embedded interpreters")


os.makedirs(OUT, exist_ok=True)
for name, fn in (("hero", hero), ("flow", flow), ("architecture", architecture)):
    for theme, pal in PALETTES.items():
        with open(os.path.join(OUT, f"{name}-{theme}.svg"), "w", encoding="utf-8", newline="\n") as f:
            f.write(fn(pal) + "\n")
print("ok")
