#!/usr/bin/env python3
"""Generates the two Trebuchet desktop wallpapers as SVG.

    python3 make_wallpaper.py left  3440 1440 left.svg     # the core: pipeline and semantics
    python3 make_wallpaper.py right 1920 1080 right.svg    # targets and interop: .NET, C++, the boundary
Render with: rsvg-convert -w W -h H page.svg -o page.png
"""
import sys
from xml.sax.saxutils import escape

PAGE, W, H, OUT = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]

# ---------------------------------------------------------------- palette (dark, matches the strategy doc)
BG, BG2 = "#11161b", "#151b21"
INK, INK2, INK3 = "#dfe4e9", "#aeb7c0", "#66727e"
RULE = "#2a333c"
BLUE, BLUE_DIM = "#7fb3d9", "#3d6a8c"
TEAL, AMBER, GREEN, PINK, VIOLET = "#6cc8b5", "#d5a44f", "#9ccf7a", "#e29ab8", "#b39ddb"
BOX, BOX_EDGE = "#1a222a", "#33404c"
SANS = "Noto Sans, Ubuntu, DejaVu Sans, sans-serif"
MONO = "Noto Sans Mono, DejaVu Sans Mono, Liberation Mono, monospace"

ZOOM = {"left": 1.22, "right": 1.28}[PAGE]
s = H / 1440.0 * ZOOM  # scale relative to the 1440-high design, zoomed so the page fills
def px(v): return f"{v * s:.1f}"

parts = []
add = parts.append

def background(title, subtitle, corner):
    add(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">')
    add(f'<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{BG2}"/><stop offset="1" stop-color="{BG}"/></linearGradient>'
        f'<marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="{7*s:.1f}" markerHeight="{7*s:.1f}" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="{BLUE_DIM}"/></marker>'
        f'<pattern id="grid" width="{48*s:.1f}" height="{48*s:.1f}" patternUnits="userSpaceOnUse"><path d="M {48*s:.1f} 0 L 0 0 0 {48*s:.1f}" fill="none" stroke="#1c242c" stroke-width="1"/></pattern></defs>')
    add(f'<rect width="{W}" height="{H}" fill="url(#g)"/><rect width="{W}" height="{H}" fill="url(#grid)" opacity="0.5"/>')
    y = 96 * s
    add(f'<text x="{px(96)}" y="{y:.1f}" font-family="{SANS}" font-size="{px(34)}" font-weight="600" fill="{INK}">{escape(title)}</text>')
    add(f'<text x="{96*s + 14*s + len(title)*19.5*s:.1f}" y="{y:.1f}" font-family="{SANS}" font-size="{px(20)}" fill="{INK3}">{escape(subtitle)}</text>')
    add(f'<text x="{W - 96*s:.1f}" y="{y:.1f}" text-anchor="end" font-family="{MONO}" font-size="{px(16)}" fill="{INK3}">{escape(corner)}</text>')

def footer(text):
    add(f'<text x="{px(96)}" y="{H - 40*s:.1f}" font-family="{SANS}" font-size="{px(13)}" fill="{INK3}">{escape(text)}</text>')

# A panel: title, then lines. Each line is text or (text, kind); kinds: sans (default), mono, head (a small bold sub-heading).
def panel(x, y, w, title, lines, accent=BLUE, title_size=21, line_gap=24, min_h=0):
    h = 44 * s
    for ln in lines:
        kind = ln[1] if isinstance(ln, tuple) else "sans"
        h += (line_gap + (8 if kind == "head" else 0)) * s
    h += 14 * s
    h = max(h, min_h)
    add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{px(8)}" fill="{BOX}" stroke="{BOX_EDGE}" stroke-width="{max(1, 1.5*s):.1f}"/>')
    add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{px(5)}" height="{h:.1f}" rx="{px(2)}" fill="{accent}"/>')
    add(f'<text x="{x + 22*s:.1f}" y="{y + 32*s:.1f}" font-family="{SANS}" font-size="{px(title_size)}" font-weight="600" fill="{INK}">{escape(title)}</text>')
    yy = y + 32 * s
    for ln in lines:
        text, kind = (ln if isinstance(ln, tuple) else (ln, "sans"))
        if kind == "head":
            yy += (line_gap + 8) * s
            add(f'<text x="{x + 22*s:.1f}" y="{yy:.1f}" font-family="{SANS}" font-size="{px(13)}" font-weight="600" fill="{accent}" letter-spacing="{px(1.2)}">{escape(text.upper())}</text>')
        elif kind == "mono":
            yy += line_gap * s
            add(f'<text x="{x + 22*s:.1f}" y="{yy:.1f}" font-family="{MONO}" font-size="{px(14)}" fill="{INK3}" xml:space="preserve">{escape(text)}</text>')
        else:
            yy += line_gap * s
            add(f'<text x="{x + 22*s:.1f}" y="{yy:.1f}" font-family="{SANS}" font-size="{px(15.5)}" fill="{INK2}">{escape(text)}</text>')
    return y + h

def arrow(x1, y1, x2, y2):
    add(f'<path d="M{x1:.1f},{y1:.1f} L{x2:.1f},{y2:.1f}" fill="none" stroke="{BLUE_DIM}" stroke-width="{max(1.5, 2*s):.1f}" marker-end="url(#arrow)"/>')

def chip(x, y, text, col):
    w = (len(text) * 8.4 + 22) * s
    add(f'<rect x="{x:.1f}" y="{y - 17*s:.1f}" width="{w:.1f}" height="{px(24)}" rx="{px(4)}" fill="{col}" fill-opacity="0.16" stroke="{col}" stroke-opacity="0.6"/>')
    add(f'<text x="{x + w/2:.1f}" y="{y:.1f}" text-anchor="middle" font-family="{MONO}" font-size="{px(13.5)}" font-weight="500" fill="{col}">{escape(text)}</text>')
    return x + w

import re
KEYWORDS = {"module", "use", "record", "entity", "union", "shape", "fn", "handler", "service", "root", "resource", "scoped",
            "match", "if", "then", "else", "with", "extern", "and", "or", "not", "init", "supervise"}
FLAGS = {"Pure", "Nondet", "Write", "Suspend"}
TOKEN = re.compile(r'(?P<comment>//.*)|(?P<string>"[^"]*")|(?P<arrow>->|=>)|(?P<bang>!)|(?P<q>\?)|(?P<lambda>\\)|(?P<word>[A-Za-z_][A-Za-z0-9_]*)|(?P<num>\d+)|(?P<other>.)')

def highlight(line):
    out, after_bang = [], False
    for m in TOKEN.finditer(line):
        kind, text = m.lastgroup, m.group()
        col = INK
        if kind == "comment": col = INK3
        elif kind == "string": col = GREEN
        elif kind in ("arrow", "q", "lambda"): col = PINK
        elif kind == "bang": col = AMBER; after_bang = True
        elif kind == "word":
            if after_bang and text in FLAGS: col = AMBER
            elif text in KEYWORDS: col = BLUE
            elif text[0].isupper() or text == "unit": col = TEAL
        elif kind == "num": col = GREEN
        else: col = INK2
        if kind not in ("bang", "word") and not (kind == "other" and text == " "): after_bang = False
        out.append(f'<tspan fill="{col}">{escape(text)}</tspan>')
    return "".join(out)

def code_panel(x, y, w, h, title, code):
    add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{px(8)}" fill="{BOX}" stroke="{BOX_EDGE}" stroke-width="{max(1, 1.5*s):.1f}"/>')
    add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{px(5)}" height="{h:.1f}" rx="{px(2)}" fill="{INK3}"/>')
    add(f'<text x="{x + 22*s:.1f}" y="{y + 32*s:.1f}" font-family="{SANS}" font-size="{px(19)}" font-weight="600" fill="{INK}">{escape(title)}</text>')
    lines = code.strip("\n").split("\n")
    avail = h - 56 * s
    lh = min(24 * s, avail / len(lines))
    fs = lh * 0.74
    for i, ln in enumerate(lines):
        yy = y + 48 * s + (i + 1) * lh
        add(f'<text x="{x + 22*s:.1f}" y="{yy:.1f}" font-family="{MONO}" font-size="{fs:.1f}" xml:space="preserve">{highlight(ln)}</text>')

def band(x, y, w, h, title, col):
    add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{px(10)}" fill="none" stroke="{col}" stroke-opacity="0.45" stroke-width="{max(1, 1.5*s):.1f}" stroke-dasharray="{px(6)},{px(6)}"/>')
    add(f'<text x="{x + 18*s:.1f}" y="{y - 10*s:.1f}" font-family="{SANS}" font-size="{px(13)}" fill="{col}" letter-spacing="{px(1.5)}">{escape(title.upper())}</text>')

CODE = """
service BookingService(store: RoomStore, clock: Clock, ids: fn() -> BookingId ! Nondet)
  fn request(cmd: RequestBooking) -> Result[Booking, BookingError]
    history = store.load(cmd.room)?
    room = fold(history, Room.empty(cmd.room), apply)
    if overlaps(room, cmd.slot) then error(SlotTaken(cmd.room, cmd.slot))
    else
      booking = Booking(ids(), cmd.room, cmd.guest, cmd.slot, BookingStatus.Held)
      store.append(cmd.room, [BookingRequested(booking)])?
      ok(booking)

root main
  clock: SystemClock
  store: PgRoomStore
  api: BookingApi
"""

# ================================================================ LEFT: the core
def left_page():
    background("Trebuchet", "the core: one front end, one checker, three targets", "core · left screen")
    M = 96 * s
    top = 168 * s
    gap = 26 * s

    # ---- pipeline row
    stages = [
        (".treb sources", ["tree-structured: a line's indented", "children are its last call's arguments", "module · use · one type namespace"], INK3),
        ("Front end", ["Lexer: INDENT / DEDENT, no tabs", "Parser → AST, Printer round-trips", "fmt keeps comments; #line origin"], BLUE),
        ("Type checker", ["Unifier, generics [T], lazy instantiation", "structural shapes, sealed unions", "exhaustive match, Result and ?"], TEAL),
        ("Effect checker", ["Nondet · Write · Suspend, inferred", "fixpoint over call sites", "! clause = upper bound, ! Pure = none"], AMBER),
        ("Roots", ["compile-time composition", "singleton · scoped · resource", "captive check, factory synthesis"], VIOLET),
    ]
    n = len(stages)
    total_w = W - 2 * M - 320 * s   # leave room for the fan-out column
    bw = (total_w - (n - 1) * gap) / n
    bottoms = []
    for i, (t, lines, col) in enumerate(stages):
        x = M + i * (bw + gap)
        bottoms.append(panel(x, top, bw, t, lines, accent=col, title_size=19, line_gap=22))
        if i > 0:
            arrow(x - gap + 2 * s, top + 40 * s, x - 2 * s, top + 40 * s)
    row_bottom = max(bottoms)

    # the three backends, continued on the right screen
    fx = M + total_w + 46 * s
    fw = W - M - fx
    panel(fx, top, fw, "Targets → right screen", [
        "Interpreter: the executable spec",
        "C# emitter: .NET, async, DI, JSON",
        "C++ emitter: header-only, coroutines",
    ], accent=GREEN, title_size=19, line_gap=22)
    arrow(fx - 46 * s + 4 * s, top + 40 * s, fx - 3 * s, top + 40 * s)

    # ---- semantics row
    sy = row_bottom + 64 * s
    add(f'<text x="{M:.1f}" y="{sy - 14*s:.1f}" font-family="{SANS}" font-size="{px(13)}" fill="{INK3}" letter-spacing="{px(1.5)}">WHAT THE CHECKER ENFORCES</text>')
    sem = [
        ("Values", TEAL, [
            "record: deeply immutable, structural ==",
            "entity: identity, the only reference equality",
            "union: sealed variants, nullary or with fields",
            "shape: structural interface, satisfied by a",
            "  service, a shape, or a record of functions",
            "generics [T] on records, unions, and fns",
            "recursive types allowed; boxed on C++",
            ("with", "head"),
            ("q = p with", "mono"), ("  addr.city: \"y\"", "mono"),
            "copy on write, flat then deep",
        ]),
        ("Effects", AMBER, [
            "three orthogonal flags, no IO alias",
            "Nondet reads the world: clock, random, a store",
            "Write changes it: handlers and service methods",
            "Suspend may block: async on .NET, coroutine on C++",
            "inferred from the body; a fn never Writes",
            "shape members, fn types, and externs declare",
            ("polymorphism", "head"),
            "an unannotated fn parameter contributes the",
            "  effects of its argument, per call site",
            "emitters generate sync and Async twins",
        ]),
        ("Errors", PINK, [
            "Result[T, E] for what the caller can handle",
            "postfix ? propagates Error out of the function",
            "fail(x) panics: unrecoverable, no catch in a fn",
            ("supervisor points", "head"),
            ("outcome = supervise orders.place(cmd)", "mono"),
            ("outcome.mapError(crashed)?", "mono"),
            "yields Result[T, Panic]; allowed in handlers",
            "  and service methods only",
            "use resources release during the unwind",
        ]),
        ("Services and roots", VIOLET, [
            "service S(deps): immutable, construction is pure",
            "root: compile-time graph, by name then by type",
            "singleton inferred; scoped = one handler call",
            "resource service must define release()",
            ("use conn = Conn(cfg)", "mono"),
            "released at block end, in reverse order",
            "captive check: singleton → scoped is an error",
            "fn-typed dep returning a service is synthesised",
            "a .NET host container may override any leaf",
        ]),
        ("Runtime model", GREEN, [
            "Vector: 32-way trie, O(log32 n), tail append",
            "Map and Set: hash array mapped tries",
            "Option, Result, Instant, Unit, Never",
            "Cell[T]: the one mutable primitive",
            "  get is Nondet, set and update are Write",
            "sleep(ms): the reference Suspend builtin",
            "== is structural everywhere",
            ("no mutation, no for loop", "head"),
            "map · filter · fold · any · all · find · forEach",
        ]),
    ]
    k = len(sem)
    cw = (W - 2 * M - (k - 1) * gap) / k
    sb = 0
    for i, (t, col, lines) in enumerate(sem):
        sb = max(sb, panel(M + i * (cw + gap), sy, cw, t, lines, accent=col, title_size=19, line_gap=22))

    # ---- third row: syntax at a glance, samples, tooling
    ty = sb + 64 * s
    add(f'<text x="{M:.1f}" y="{ty - 14*s:.1f}" font-family="{SANS}" font-size="{px(13)}" fill="{INK3}" letter-spacing="{px(1.5)}">THE LANGUAGE AT A GLANCE, AND WHAT EXISTS</text>')
    samples = [
        "bookings: onion layers, roots dev / test / main",
        "  domain · application · infrastructure · api · host",
        "orders: the milestone-6 acceptance domain",
        "ffi: extern fn with csharp and cpp bindings",
        "async: sleep, the event loop, host-side spawn",
        "resources: use x = ..., reverse-order release",
        "factories: synthesised fn-typed dependencies",
        "trees: recursive unions and records",
        "supervision: supervise → Result[T, Panic]",
        "collections: Set builtins, structural ==",
        "generics: [T] on records, unions, functions",
    ]
    tooling = [
        ("treb check · effects · fmt --write", "mono"),
        ("treb emit dir --out gen --host", "mono"),
        ("treb emit dir --out gen --target cpp", "mono"),
        ("treb serve dir --root dev --port 5080", "mono"),
        "dotnet pack → Trebuchet.Cli (treb), Trebuchet.Runtime",
        "VS Code grammar in vscode-trebuchet/",
        "170 tests: lexer, parser, round-trip, checker,",
        "  interpreter, both emitters, both runtimes",
        "documents: brief, strategy (published), syntax sketch",
        "the interpreter is the oracle for every sample",
    ]
    quarter = (W - 2 * M - 2 * gap) / 4
    code_h = (H - 100 * s) - ty - 70 * s   # what the page has left above the legend and footer
    sb2 = panel(M + 2 * quarter + gap, ty, quarter, "Samples", samples, accent=INK3, title_size=19, line_gap=22, min_h=code_h)
    sb2 = max(sb2, panel(M + 3 * quarter + 2 * gap, ty, quarter, "Tooling", tooling, accent=INK3, title_size=19, line_gap=22, min_h=code_h))
    code_panel(M, ty, 2 * quarter, sb2 - ty, "Syntax at a glance", CODE)

    # ---- legend and tooling
    ly = sb2 + 46 * s
    x = M
    for flag, col, desc in [("! Pure", GREEN, "no effects"), ("Nondet", AMBER, "reads the world"), ("Write", PINK, "changes the world"), ("Suspend", BLUE, "may block")]:
        x = chip(x, ly, flag, col)
        add(f'<text x="{x + 10*s:.1f}" y="{ly:.1f}" font-family="{SANS}" font-size="{px(13)}" fill="{INK3}">{escape(desc)}</text>')
        x += (len(desc) * 7.2 + 44) * s
    add(f'<text x="{W - M:.1f}" y="{ly:.1f}" text-anchor="end" font-family="{MONO}" font-size="{px(13.5)}" fill="{INK3}">treb parse · fmt · tokens · check · effects · emit [--host] [--target cpp] · serve   |   170 tests, one oracle: the interpreter</text>')
    footer("Trebuchet is immutable-first. The interpreter is the executable specification; every sample must print the same line on the interpreter, on .NET, and on C++.")
    add("</svg>")

# ================================================================ RIGHT: targets and interop
def right_page():
    background("Targets", "the .NET and C++ backends, and the boundary with their hosts", "targets · right screen")
    M = 72 * s
    top = 176 * s
    gap = 28 * s
    cw = (W - 2 * M - gap) / 2
    lx, rx = M, M + cw + gap

    # ---- .NET column
    y = top
    net_panels = [
        ("C# emitter  ·  treb emit --host", [
            "record → C# record (validating ctor for init); union → abstract record + sealed variants",
            "shape → interface; a service implements every shape it satisfies",
            "Suspend → async ValueTask<T>;  ? → early return;  supervise → catch (TrebPanic)",
            "resource → IDisposable / IAsyncDisposable, use → using / await using",
            "#line directives: panics and diagnostics name the .treb file and line",
        ]),
        ("Trebuchet.Runtime  (nupkg)", [
            "Vector : IReadOnlyList,  Map : IReadOnlyDictionary,  Set,  Cell,  Option / Result",
            "Prelude with Async twins; sleep = Task.Delay",
            "TrebuchetJson: one converter factory for the boundary shape",
            "Boundary.To<T>: converts every extern result at the edge",
        ]),
        ("ASP.NET host  ·  AddTrebuchet_<root>()", [
            "each root entry: keyed factory + TryAdd under its type and shapes",
            "dependencies resolve from the container by declared type first,",
            "  so anything the host registered before the call wins",
            ("builder.Services.AddSingleton<Clock>(new HostClock());", "mono"),
            ("builder.Services.AddTrebuchet_dev();", "mono"),
            ("builder.Services.ConfigureHttpJsonOptions(o => TrebuchetJson.Configure(o.SerializerOptions));", "mono"),
        ]),
    ]
    for i, (t, lines) in enumerate(net_panels):
        if i > 0:
            arrow(lx + cw / 2, y, lx + cw / 2, y + 24 * s)
            y += 26 * s
        y = panel(lx, y, cw, t, lines, accent=BLUE, title_size=18, line_gap=21)
    net_bottom = y

    # ---- C++ column
    y = top
    cpp_panels = [
        ("C++ emitter  ·  treb emit --target cpp", [
            "record → struct with defaulted == and a generated std::hash",
            "union → forward-declared variants + std::variant alias; recursive field → Box<T>",
            "shape → abstract class; service → Rc<Service>; generics → templates",
            "Suspend → Task<T> coroutine, co_await / co_return;  supervise → catch (treb::Panic)",
            "resource → destructor calls release when the last Rc drops",
        ]),
        ("trebuchet.hpp  (header-only, standard library, g++ -std=c++20)", [
            "Rc<T> non-atomic refcount;  Vector trie;  Map and Set HAMT;  Box<T>",
            "Task<T>: lazy coroutine, symmetric transfer; get() drives the loop",
            "EventLoop per thread: ready queue, timer heap, thread-safe post()",
            "sleepFor, yield, spawn(task) → joinable, Completion<T> / Pending<T>",
        ]),
        ("Native host  ·  driver.cpp", [
            "declares host:: functions, then includes generated.hpp",
            "a callback API becomes a Task: move the Completion into the callback,",
            "  co_await pending(); a resolver dropped unresolved fails the waiter",
            ("auto root = Bookings_Host_Test::test();", "mono"),
            ("auto r = root.api->postBooking(req).get();", "mono"),
            ("auto a = spawn(tick(log, \"a\", 30)); a.get();   // timer order", "mono"),
        ]),
    ]
    for i, (t, lines) in enumerate(cpp_panels):
        if i > 0:
            arrow(rx + cw / 2, y, rx + cw / 2, y + 24 * s)
            y += 26 * s
        y = panel(rx, y, cw, t, lines, accent=PINK, title_size=18, line_gap=21)
    cpp_bottom = y

    # the dashed bands, now that the column heights are known
    col_bottom = max(net_bottom, cpp_bottom) + 14 * s
    for bx, col, label in [(lx, BLUE, ".NET path"), (rx, PINK, "C++ path")]:
        band(bx - 10 * s, top - 26 * s, cw + 20 * s, col_bottom - top + 26 * s, label, col)

    # ---- interop strip across both columns
    iy = col_bottom + 50 * s
    iw = W - 2 * M
    add(f'<text x="{M:.1f}" y="{iy - 12*s:.1f}" font-family="{SANS}" font-size="{px(13)}" fill="{AMBER}" letter-spacing="{px(1.5)}">THE INTEROP BOUNDARY, SAME RULES BOTH WAYS</text>')
    third = (iw - 2 * gap) / 3
    panel(M, iy, third, "Outbound: extern fn", [
        ("extern fn readLines(path: String)", "mono"),
        ("  -> Result[Vector[String], FileError] ! Nondet Suspend", "mono"),
        ("  csharp \"System.IO.File.ReadAllLinesAsync\"", "mono"),
        ("  cpp \"host::readLines\"", "mono"),
        ("  catch csharp \"System.IO.IOException\" -> Unreadable", "mono"),
        "effects are mandatory; an unmapped exception panics",
    ], accent=AMBER, title_size=17, line_gap=20)
    panel(M + third + gap, iy, third, "Values crossing", [
        "arrays and IEnumerable → Vector;  IDictionary → Map",
        "null → None when declared Option, otherwise a panic",
        "ints and dates widen; Trebuchet values pass out as they are",
        "JSON: a nested single-field record flattens to its value,",
        "  a nullary variant is its name, fields carry \"type\",",
        "  Option is null or value, Map is an object for scalar keys",
    ], accent=AMBER, title_size=17, line_gap=20)
    panel(M + 2 * (third + gap), iy, third, "Inbound: hosts call in", [
        "roots are the entry points; the host composes one",
        ".NET: the DI container supplies leaves and overrides defaults",
        "C++: a root struct of Rc<Service>, Task::get() at the edge",
        "panics: supervise inside, the host's catch-all outside",
        "packaging: Trebuchet.Cli tool (treb) carries the C++ header;",
        "  generated projects reference the Trebuchet.Runtime package",
    ], accent=AMBER, title_size=17, line_gap=20)
    footer("Same scenario on three targets: the interpreter is the oracle; bookings, ffi, async, resources, factories, trees, and supervision each print one identical line on .NET and on C++.")
    add("</svg>")

if PAGE == "left":
    left_page()
elif PAGE == "right":
    right_page()
else:
    raise SystemExit("page must be left or right")
open(OUT, "w").write("\n".join(parts))
print(f"wrote {OUT} ({PAGE}, {W}x{H})")
