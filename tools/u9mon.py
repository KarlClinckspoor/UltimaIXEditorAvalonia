"""u9mon: watch Ultima IX's mechanics live, and drive it, by hooking u9.exe's functions with Frida (no game files change).

    uv run --with frida python tools/u9mon.py                  # attach to a running u9.exe (or u9-mod.exe)
    uv run --with frida python tools/u9mon.py --wait           # wait for the game to start, attach, quit when it ends
    uv run --with frida python tools/u9mon.py --spawn          # start gamedata/u9.exe and attach from the first instruction
    uv run --with frida python tools/u9mon.py --log out.jsonl --only hp,spell,script --commands cmds.txt

What it reports (addresses from docs/exe-research.md, docs/mechanics/ and docs/trigger-scripts.md; the exe always
loads at 0x400000):
  hp      HP (and mana) loss: who, how much, before/after, which damage path called. Npc_SubtractHP 0x4F1CC0 is a
          clamped u16 subtract on a record field: +0x34 HP from the damage paths, +0x3A mana from 0x5F0ED0.
  spell   every cast: SpellManager_Cast 0x5ED950, the spell number (2nd argument), the caster's code
  ai      a creature fighting (0x4F97B0, once each), target changes (AI +0x104 after vf128 0x43CF30)
  team    the friend test 0x43E200 (AI_Base::vf08C), each pair's answer when it first appears or changes
  mana    mana spent (0x5F0ED0), raw arguments
  script  trigger scripts: each run (Trigger_RunScript 0x54F850: the trigger object, which of its four scripts, the
          slot, the entry label), every op as it runs (loop head 0x54F9C3: op, A, B, and the flag it left), each
          command sent to an object (0x5510A0), and the helpers behind the commands still marked ? in
          docs/trigger-scripts.md, while a script runs: 3E 0x5B7220, 05 0x4CD290, 10 0x50C2D0, 2B 0x576E50,
          30 0x4BDC60 (registers, stack arguments, return value)

Commands (typed on stdin, or appended as lines to the --commands file; `u9 script-uses` prints ready-made ones).
They run between two frames (at the end of Game_FrameUpdate 0x49F610), on the game's own thread:
  tp <map> <x>,<y>[,<z>] [yaw]    travel: Map_GetManager 0x4DF250, World_ChangeMap 0x49ED20 (object units, 128 per
                                   point; without z, the ground + 1)
  find <type> <x> <y> <z> <link>  look for a loaded object by its record (x, y, z in its chunk, as the map file
                                   holds them), print its address
  run <type> <x> <y> <z> <link> <which> [label]
                                   run one of that object's four scripts (0, 1 OO Trigger A/B; 2, 3 SF Trigger A/B)
                                   through Trigger_RunScript, from entry label `label` (0: the start)
  where                            the current map and the Avatar's position
Caller names come from analysis/u9exe/index/functions.tsv when that private index is present.
Ctrl+C stops and detaches; the game keeps running.
"""
import argparse, json, os, re, sys, threading, time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

AGENT = r"""
'use strict';
const only = new Set(ONLY);
const NPC_TABLE = ptr('0x98F31C');                  // NpcRecord* g_NpcTable (512 records of 316 bytes)
const REC = 316;
function table() { return NPC_TABLE.readPointer(); }
function recIndex(p) { const t = table(); const d = p.sub(t).toInt32(); return (d >= 0 && d < REC * 512) ? Math.floor(d / REC) : -1; }
function recName(i) {                              // name at +4, 32 bytes; spawned records can hold junk: ASCII only
    if (i < 0 || i >= 512) return '?';
    try {
        const b = new Uint8Array(table().add(i * REC + 4).readByteArray(32));
        let s = '';
        for (const c of b) { if (c === 0) break; s += (c >= 32 && c < 127) ? String.fromCharCode(c) : '?'; }
        return s;
    } catch (e) { return '?'; }
}
function aiName(ai) { const i = ai.add(0x20).readS32(); return i + ' ' + recName(i); }   // AI_Base +0x20 = NPC index
function caller(ctx) { return ctx.returnAddress.toString(); }
function hex(v) { return '0x' + (v >>> 0).toString(16); }

// A map object in memory is its map-file record: +4 x, +6 y, +8 z (in its chunk), +0x0A type, +0x14 flags,
// +0x1A link (when flag bit 0 is clear). Its 4 KB page's header holds the chunk origin (+0x0C x, +0x10 y).
function objInfo(p) {
    try {
        if (p.isNull()) return null;
        const page = p.and(ptr('0xFFFFF000'));
        return { addr: p.toString(), type: p.add(0x0A).readU16(), link: (p.add(0x14).readU8() & 1) ? 0 : p.add(0x1A).readU16(),
                 x: page.add(0x0C).readS32() + p.add(4).readU16(), y: page.add(0x10).readS32() + p.add(6).readU16(), z: p.add(8).readU16() };
    } catch (e) { return null; }
}

if (only.has('hp')) Interceptor.attach(ptr('0x4F1CC0'), {      // ushort __thiscall Npc_SubtractHP(ushort *field, ushort amount)
    onEnter(args) {
        this.hp = this.context.ecx;
        this.amount = args[0].toInt32() & 0xFFFF;
        this.before = this.hp.readU16();
        const d = this.hp.sub(table()).toInt32();
        this.rec = (d >= 0 && d < REC * 512) ? Math.floor(d / REC) : -1;
        this.field = this.rec < 0 ? -1 : d - this.rec * REC;
        this.from = caller(this);
    },
    onLeave(ret) {
        send({ ev: 'hp', npc: this.rec, name: recName(this.rec), amount: this.amount, before: this.before,
               after: this.hp.readU16(), field: this.field, from: this.from });
    }
});

if (only.has('spell')) Interceptor.attach(ptr('0x5ED950'), {
    onEnter(args) {
        const raw = [];
        for (let i = 0; i < 7; i++) raw.push(args[i].toInt32());
        send({ ev: 'spell', manager: this.context.ecx.toString(), raw: raw, from: caller(this) });
    }
});

if (only.has('mana')) Interceptor.attach(ptr('0x5F0ED0'), {
    onEnter(args) { send({ ev: 'mana', raw: [args[0].toInt32() & 0xFF, args[1].toInt32()], from: caller(this) }); }
});

if (only.has('ai')) {
    const fighting = {};
    Interceptor.attach(ptr('0x4F97B0'), {                       // 0x4F97B0(record, flag): called every frame while fighting
        onEnter(args) {
            const i = args[0].toInt32();
            if (fighting[i]) return;
            fighting[i] = true;
            send({ ev: 'fight', npc: i, name: recName(i), from: caller(this) });
        }
    });
    const lastTarget = {};
    Interceptor.attach(ptr('0x43CF30'), {                       // AI_Base::vf128 engage: choose a target
        onEnter(args) { this.ai = this.context.ecx; },
        onLeave(ret) {
            try {
                const me = aiName(this.ai);
                const t = this.ai.add(0x104).readS32();
                const key = this.ai.toString();
                if (lastTarget[key] !== t) {
                    lastTarget[key] = t;
                    send({ ev: 'target', who: me, target: t, targetName: t >= 0 && t < 512 ? recName(t) : '' });
                }
            } catch (e) { }
        }
    });
}

if (only.has('team')) {
    const seen = {};
    Interceptor.attach(ptr('0x43E200'), {                       // AI_Base::vf08C(other): is the other a friend?
        onEnter(args) { this.me = this.context.ecx; this.other = args[0]; },
        onLeave(ret) {
            try {
                const friend = ret.toInt32() & 0xFF;
                const a = aiName(this.me);
                const b = aiName(this.other);
                const key = a + '|' + b;
                if (seen[key] !== friend) {
                    seen[key] = friend;
                    send({ ev: 'team', looker: a, other: b, friend: friend, mode: this.me.add(0x139).readU8(),
                           team: this.me.add(0x13A).readU16() });
                }
            } catch (e) { }
        }
    });
}

// ---- trigger scripts ----
const FLAG = ptr('0xAE7C68');                       // Trigger_Flag: cleared before each op, set by tests
const STATE = ptr('0xAE7C6C');                      // the interpreter's state object
const getProp = new NativeFunction(ptr('0x4BF010'), 'uint32', ['pointer', 'uint8'], 'thiscall');
const scriptStack = [];                             // runs in progress (scripts can nest through 0x28 and others)
const seenRuns = {};                                // run signature -> times seen: repeats are not sent again
let forceRun = false;                               // the next run was asked for with `run`: always send it
function scriptDepth() { return scriptStack.length; }
function top() { return scriptStack[scriptStack.length - 1]; }
function flushOp(s) {
    // the op's event goes in when the op starts (so what it calls follows it); its flag is filled in at the next
    // loop head or the end, before the run is sent
    if (s && s.op) { s.op.flag = FLAG.readU32(); s.op = null; }
}
if (only.has('script')) {
    Interceptor.attach(ptr('0x54F850'), {           // void __thiscall Trigger_RunScript(state, object, which, label)
        onEnter(args) {
            const obj = args[0], which = args[1].toInt32(), label = args[2].toInt32();
            let slot = -1;
            try {
                if (!obj.isNull()) {
                    const pair = getProp(obj, which < 2 ? 0x3E : 0x3B);
                    slot = (which & 1) ? (pair >>> 16) : (pair & 0xFFFF);
                }
            } catch (e) { }
            this.entry = { depth: scriptStack.length, slot: slot, op: null, force: forceRun, events: [] };
            forceRun = false;
            scriptStack.push(this.entry);
            this.entry.events.push({ ev: 'script', depth: this.entry.depth, slot: slot, which: which, label: label, obj: objInfo(obj), from: caller(this) });
        },
        onLeave(ret) {
            const s = this.entry;
            flushOp(s);
            scriptStack.pop();
            s.events.push({ ev: 'script-end', depth: s.depth, slot: s.slot, flag: FLAG.readU32() });
            const key = JSON.stringify(s.events.map(e => [e.ev, e.op, e.a, e.b, e.flag, e.cmd, e.ret, e.obj && e.obj.addr]));
            const n = seenRuns[key] = (seenRuns[key] || 0) + 1;
            if (s.force || n === 1) send({ ev: 'batch', events: s.events });
            else if (n === 2) send({ ev: 'repeat', slot: s.slot, depth: s.depth });
        }
    });
    Interceptor.attach(ptr('0x54F9C3'), function () {     // the loop head: esi = this op (u16 op, A, B), ebx = state
        const s = top();
        if (!s) return;
        flushOp(s);                                       // the previous op, with the flag it left
        const p = this.context.esi, base = this.context.ebx.readPointer();
        s.op = { ev: 'op', depth: s.depth, slot: s.slot, i: Math.floor(p.sub(base).toInt32() / 6), op: p.readU16(), a: p.add(2).readU16(), b: p.add(4).readU16(), flag: null };
        s.events.push(s.op);
    });
    Interceptor.attach(ptr('0x5510A0'), {           // Trigger_SendToObject(state, command, op, object): once per match
        onEnter(args) {
            const s = top();
            if (s) s.events.push({ ev: 'send', depth: s.depth, cmd: args[0].toInt32() & 0xFF, obj: objInfo(args[2]) });
        }
    });
    const helpers = { '0x5B7220': '3E', '0x4CD290': '05', '0x50C2D0': '10', '0x576E50': '2B', '0x4BDC60': '30' };
    for (const a in helpers) Interceptor.attach(ptr(a), {
        onEnter(args) {
            this.on = scriptDepth() > 0;
            if (!this.on) return;
            const c = this.context;
            this.call = { ev: 'helper', cmd: helpers[a], fn: a, ecx: hex(c.ecx.toInt32()), edx: hex(c.edx.toInt32()),
                          stack: [0, 1, 2].map(i => hex(args[i].toInt32())), ecxObj: objInfo(c.ecx), from: caller(this) };
        },
        onLeave(ret) {
            if (!this.on) return;
            this.call.ret = hex(ret.toInt32());
            const s = top();
            if (s) s.events.push(this.call); else send(this.call);
        }
    });
}

// ---- commands, run between frames on the game thread ----
const WORLD = ptr('0x850228');
const getManager = new NativeFunction(ptr('0x4DF250'), 'pointer', ['int'], 'fastcall');
const changeMap = new NativeFunction(ptr('0x49ED20'), 'void', ['pointer', 'pointer', 'pointer', 'int', 'pointer', 'pointer', 'int'], 'thiscall');
const terrainHeight = new NativeFunction(ptr('0x4E02A0'), 'int', ['pointer', 'uint', 'uint'], 'thiscall');
const saveLoadEnable = new NativeFunction(ptr('0x522150'), 'void', ['pointer'], 'thiscall');
const runScript = new NativeFunction(ptr('0x54F850'), 'void', ['pointer', 'pointer', 'int', 'uint'], 'thiscall');
const queue = [];

function currentMap() {
    const w = WORLD.readPointer();
    if (w.isNull()) return -1;
    const mgr = w.add(0xCC).readPointer();
    return mgr.isNull() ? -1 : mgr.add(0x20).readS32();
}

function findObject(type, x, y, z, link) {
    // the record's x, y, z, type (8 bytes from +4) and its link at +0x1A
    const le = v => [v & 0xFF, (v >> 8) & 0xFF].map(b => ('0' + b.toString(16)).slice(-2)).join(' ');
    const pat = [le(x), le(y), le(z), le(type)].join(' ') + ' ' + Array(14).fill('??').join(' ') + ' ' + le(link);
    const hits = [];
    for (const r of Process.enumerateRanges('rw-')) {
        if (r.size > 256 * 1024 * 1024) continue;
        try { for (const m of Memory.scanSync(r.base, r.size, pat)) hits.push(m.address.sub(4)); } catch (e) { }
    }
    return hits;
}

const commands = {
    where() {
        const w = WORLD.readPointer();
        return { map: currentMap(), world: w.toString() };
    },
    tp(map, x, y, z, yaw) {
        const w = WORLD.readPointer();
        if (w.isNull()) return { error: 'no world loaded' };
        const mgr = getManager(map);
        if (mgr.isNull()) return { error: 'map ' + map + ' did not load' };
        if (z === null) z = terrainHeight(mgr, Math.floor(x / 128), Math.floor(y / 128)) + 1;
        const loc = Memory.alloc(12); loc.writeFloat(x); loc.add(4).writeFloat(y); loc.add(8).writeFloat(z);
        const t = (yaw || 0) * Math.PI / 360;                       // a turn about the vertical axis: (w, x, y, z)
        const rot = Memory.alloc(16); rot.writeFloat(Math.cos(t)); rot.add(4).writeFloat(0); rot.add(8).writeFloat(0); rot.add(12).writeFloat(Math.sin(t));
        changeMap(w, mgr, ptr(0), 0, loc, rot, 0);
        // as MapChanger::vf004 0x54A590 does after the change: saving back on, the busy flag off
        const sl = ptr('0xAD126C').readPointer(); if (!sl.isNull()) saveLoadEnable(sl);
        const busy = ptr('0x131F668').readPointer(); if (!busy.isNull()) busy.add(0x50).writeU32(0);
        return { map: map, x: x, y: y, z: z, now: currentMap() };
    },
    find(type, x, y, z, link) {
        const hits = findObject(type, x, y, z, link);
        return { hits: hits.map(objInfo) };
    },
    run(type, x, y, z, link, which, label) {
        const hits = findObject(type, x, y, z, link);
        if (hits.length !== 1) return { error: hits.length + ' objects match (need exactly 1; is its map loaded?)', hits: hits.map(objInfo) };
        const state = STATE.readPointer();
        if (state.isNull()) return { error: 'no interpreter state' };
        if (ptr('0x850220').readU32() !== 1) return { error: 'the game is not in play (0x850220 != 1)' };
        if (state.add(0x38).readU32() !== 0) return { error: 'the interpreter is busy (+0x38)' };
        forceRun = true;
        runScript(state, hits[0], which, label || 0);
        forceRun = false;
        return { ran: objInfo(hits[0]), which: which, label: label || 0 };
    },
};

// Game_FrameUpdate(world, 0), once a frame from the main loop: replaced by a version that runs the original and then
// the queued commands. A replacement (not an attach callback) so that the game functions the commands call still go
// through the hooks above: Frida skips hooks for calls made inside an attach callback.
const FRAME = ptr('0x49F610');
const frameOriginal = new NativeFunction(FRAME, 'void', ['pointer', 'int'], 'thiscall');   // from inside: the original
Interceptor.replace(FRAME, new NativeCallback(function (world, arg) {
    frameOriginal(world, arg);
    while (queue.length) {
        const c = queue.shift();
        let r;
        try { r = commands[c.name].apply(null, c.args); } catch (e) { r = { error: String(e) }; }
        send({ ev: 'result', cmd: c.name, args: c.args, result: r });
    }
}, 'void', ['pointer', 'int'], 'thiscall'));

rpc.exports = {
    queue(name, args) {
        if (!commands[name]) return 'unknown command ' + name;
        queue.push({ name: name, args: args });
        return 'queued';
    },
};
send({ ev: 'ready', hooks: Array.from(only) });
"""


def load_names():
    path = os.path.join(REPO, "analysis", "u9exe", "index", "functions.tsv")
    starts = []
    if not os.path.exists(path):
        return lambda a: hex(a)
    for line in open(path, encoding="utf-8", errors="replace"):
        p = line.split("\t")
        if len(p) > 3 and re.fullmatch(r"[0-9A-Fa-f]{8}", p[0]) and re.fullmatch(r"[0-9A-Fa-f]{8}", p[1]):
            starts.append((int(p[0], 16), int(p[1], 16), p[3]))
    starts.sort()
    keys = [s[0] for s in starts]
    import bisect

    def name(a):
        i = bisect.bisect_right(keys, a) - 1
        if i >= 0 and a < starts[i][1]:
            return f"{starts[i][2]}+0x{a - starts[i][0]:X}"
        return hex(a)
    return name


SPELLS = {10: "Lightning Bolt", 15: "Fireball", 21: "Summon Undead", 47: "big stone"}


def obj(o):
    if not o:
        return "-"
    return f"type {o['type']} link {o['link']} at {o['x']},{o['y']},{o['z']}"


def describe(m, name):
    ev = m.get("ev")
    if ev == "hp":
        what = {0x34: "HP  ", 0x3A: "MANA-"}.get(m.get("field"), f"+{m.get('field', 0):X}")
        return f"{what} {m['npc']:>3} {m['name']:<20} -{m['amount']:<4} {m['before']:>5} -> {m['after']:<5} from {name(int(m['from'], 16))}"
    if ev == "spell":
        raw = m["raw"]          # the spell number is the second argument (Liche 10/21, Hellhound 15 in the decompile)
        s = raw[1]
        return f"CAST spell {s} {SPELLS.get(s, '')}  raw {raw}  from {name(int(m['from'], 16))}"
    if ev == "mana":
        return f"MANA raw {m['raw']}  from {name(int(m['from'], 16))}"
    if ev == "fight":
        return f"FIGHT {m['npc']:>3} {m['name']} starts fighting"
    if ev == "target":
        return f"TARGET {m['who']} -> {m['target']} {m['targetName']}"
    if ev == "team":
        return f"TEAM {m['looker']} sees {m['other']} as {'FRIEND' if m['friend'] else 'ENEMY'} (mode {m['mode']}, team {m['team']})"
    ind = "  " * max(m.get("depth", 0), 0)
    if ev == "script":
        return f"{ind}SCRIPT {m['slot']} (script {m['which']} of {obj(m['obj'])}) label {m['label']} from {name(int(m['from'], 16))}"
    if ev == "script-end":
        return f"{ind}END {m['slot']} flag {m['flag']}"
    if ev == "op":
        return f"{ind}  op {m['i']:>3}: {m['op']:04x} A {m['a']:04x} B {m['b']:04x}  (cmd {m['op'] & 0xFF:02X}, flag after {m['flag']})"
    if ev == "send":
        return f"{ind}    -> cmd {m['cmd']:02X} to {obj(m['obj'])}"
    if ev == "helper":
        return (f"{'':4}HELPER {m['cmd']} {m['fn']} ecx {m['ecx']} edx {m['edx']} stack {m['stack']} -> {m['ret']}"
                f"{'  ecx = ' + obj(m['ecxObj']) if m.get('ecxObj') else ''}")
    if ev == "batch":
        return "\n".join(time.strftime("%H:%M:%S ") + describe(e, name) for e in m["events"])[9:]
    if ev == "repeat":
        return f"{ind}(script {m['slot']} ran again the same way: not shown again unless it changes)"
    if ev == "result":
        return f"RESULT {m['cmd']} {m['args']}: {json.dumps(m['result'])}"
    if ev == "ready":
        return f"hooks ready: {', '.join(m['hooks'])}"
    return json.dumps(m)


def parse_command(line):
    """'tp 9 63310,56600[,2420] [yaw]' -> ('tp', [9, 63310, 56600, z or None, yaw]); run/find/where likewise."""
    w = line.replace(",", " ").split()
    if not w:
        return None
    name, a = w[0], w[1:]
    if name == "tp":
        nums = [float(v) for v in a]
        if len(nums) < 3:
            raise ValueError("tp <map> <x>,<y>[,<z>] [yaw]")
        # a fifth number is the yaw; a fourth is z only when written after a comma
        has_z = len(line.split()[2].split(",")) == 3 if len(line.split()) > 2 else False
        z = nums[3] if has_z else None
        yaw = nums[4] if has_z and len(nums) > 4 else (nums[3] if not has_z and len(nums) > 3 else 0)
        return "tp", [int(nums[0]), nums[1], nums[2], z, yaw]
    if name == "find":
        return "find", [int(v) for v in a[:5]]
    if name == "run":
        v = [int(x) for x in a]
        return "run", v[:6] + [v[6] if len(v) > 6 else 0]
    if name == "where":
        return "where", []
    raise ValueError(f"unknown command {name}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--spawn", action="store_true", help="start the game (gamedata/u9.exe) instead of attaching")
    ap.add_argument("--wait", action="store_true", help="wait for the game to start, then attach")
    ap.add_argument("--exe", default=os.path.join(REPO, "gamedata", "u9.exe"))
    ap.add_argument("--only", default="hp,spell,ai,team,mana,script", help="comma list of: hp, spell, ai, team, mana, script")
    ap.add_argument("--log", help="also write every event as JSON lines here")
    ap.add_argument("--commands", help="a file to watch: each line appended to it is run as a command")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    import frida
    name = load_names()
    if a.spawn:
        pid = frida.spawn([a.exe], cwd=os.path.dirname(a.exe))
        session = frida.attach(pid)
    else:
        def find():
            return [p for p in frida.get_local_device().enumerate_processes() if p.name.lower() in ("u9.exe", "u9-mod.exe", "u9-laa.exe")]
        procs = find()
        if not procs and a.wait:
            print("waiting for u9.exe ...", flush=True)
            while not procs:
                time.sleep(1)
                procs = find()
            time.sleep(3)                       # let it load its data before hooking
        if not procs:
            sys.exit("No u9.exe running: start the game first, use --wait, or use --spawn.")
        pid = procs[0].pid
        session = frida.attach(pid)
    only = [s.strip() for s in a.only.split(",") if s.strip()]
    script = session.create_script(AGENT.replace("ONLY", json.dumps(only)))
    log = open(a.log, "a", encoding="utf-8") if a.log else None

    def on_message(msg, data):
        if msg["type"] == "send":
            m = msg["payload"]
            try:
                line = describe(m, name)
            except Exception as e:
                line = f"{json.dumps(m)}  ({e})"
            print(time.strftime("%H:%M:%S"), line, flush=True)
            if log:
                m["t"] = time.time()
                log.write(json.dumps(m) + "\n")
                log.flush()
        elif msg["type"] == "error":
            print("agent error:", msg.get("description"), file=sys.stderr, flush=True)
    script.on("message", on_message)
    gone = []
    session.on("detached", lambda reason, crash=None: gone.append(reason))
    script.load()
    if a.spawn:
        frida.resume(pid)
    print(f"watching pid {pid}; Ctrl+C to stop", flush=True)

    def submit(line):
        line = line.strip()
        if not line or line.startswith("#"):
            return
        try:
            c = parse_command(line)
            print(time.strftime("%H:%M:%S"), f"COMMAND {line}: {script.exports_sync.queue(c[0], c[1])}", flush=True)
        except Exception as e:
            print(time.strftime("%H:%M:%S"), f"COMMAND {line}: {e}", flush=True)

    if sys.stdin and sys.stdin.isatty():
        threading.Thread(target=lambda: [submit(l) for l in sys.stdin], daemon=True).start()
    seen = 0
    if a.commands:
        open(a.commands, "a").close()
        seen = os.path.getsize(a.commands)      # only lines added from now on
    try:
        while not gone:
            time.sleep(0.5)
            if a.commands and os.path.getsize(a.commands) > seen:
                with open(a.commands, encoding="utf-8") as f:
                    f.seek(seen)
                    chunk = f.read()
                complete = chunk[:chunk.rfind("\n") + 1]
                seen += len(complete.encode("utf-8"))
                for l in complete.splitlines():
                    submit(l)
        print(f"{time.strftime('%H:%M:%S')} game gone ({gone[0]}); monitor stopping", flush=True)
    except KeyboardInterrupt:
        pass
    finally:
        try:
            session.detach()
        except Exception:
            pass


if __name__ == "__main__":
    main()
