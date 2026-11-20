#!/usr/bin/env python3
"""Analyzer Fisher Online — data collection server (SERVER-01, v1).

Only standard library + python3-cryptography (Ubuntu package). Listens on 127.0.0.1:8090 behind nginx (TLS, rate limits).
Stores NO nicknames, chat, player ids or IP addresses. An installation is identified only by its public key (ECDSA P-256).

Endpoints:
  GET  /api/v1/health
  POST /api/v1/register   {"pubkey": base64(SubjectPublicKeyInfo DER)}            -> {"install_id", "status", "server_time"}
  POST /api/v1/submit     signed batch {"events":[...]}                             -> {"accepted", "rejected", "reasons"}
  GET  /api/v1/spots?waterbody=<id>[&fish=<id>]                                      -> cells confirmed by >= CONSENSUS installs
Signature (submit): headers X-AFO-Id, X-AFO-Ts (unix ms), X-AFO-Nonce (16..64 chars),
  X-AFO-Sig = base64(ECDSA-P256-SHA256 over "POST\n/api/v1/submit\n{ts}\n{nonce}\n{sha256hex(body)}"), raw r||s (64 bytes) or DER.
"""
import base64, hashlib, json, os, sqlite3, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils
from cryptography.exceptions import InvalidSignature

DB = os.environ.get("AFO_DB", "/var/lib/afo/afo.db")
REF = os.environ.get("AFO_REF", "/opt/afo/ref")           # fish_limits.json, waterbodies.json
HOST, PORT = "127.0.0.1", int(os.environ.get("AFO_PORT", "8090"))
MAX_BODY = 256 * 1024
MAX_EVENTS = 500
CLOCK_SKEW_MS = 5 * 60 * 1000
QUARANTINE_S = 24 * 3600          # new installation does not affect shared spots for 24 h ...
QUARANTINE_EVENTS = 50            # ... and until it has sent this many valid events
CONSENSUS = 3                     # a spot is shown only when confirmed by >= 3 different trusted installations
MAX_REQ_PER_HOUR = 120
MAX_EVENTS_PER_DAY = 20000
MAX_REG_PER_HOUR = 30             # whole server: new installations per hour
KINDS = {"bite", "empty_bite", "catch"}
_lock = threading.Lock()
_reg_times = []


def load_ref():
    fish, wb = {}, set()
    try:
        for k, v in json.load(open(os.path.join(REF, "fish_limits.json"), encoding="utf-8")).items():
            fish[int(k)] = (int(v[0]), int(v[1]))
    except Exception as e:
        print("no fish limits:", e, file=sys.stderr)
    try:
        wb = {int(x) for x in json.load(open(os.path.join(REF, "waterbodies.json"), encoding="utf-8"))}
    except Exception as e:
        print("no waterbodies:", e, file=sys.stderr)
    return fish, wb


FISH, WATERBODIES = load_ref()


def db():
    c = sqlite3.connect(DB, timeout=10)
    c.execute("PRAGMA journal_mode=WAL"); c.execute("PRAGMA foreign_keys=ON")
    return c


def init_db():
    os.makedirs(os.path.dirname(DB), exist_ok=True)
    c = db()
    c.executescript("""
    CREATE TABLE IF NOT EXISTS installs(install_id TEXT PRIMARY KEY, pubkey BLOB NOT NULL UNIQUE, created INTEGER NOT NULL,
        status TEXT NOT NULL DEFAULT 'quarantine', valid INTEGER NOT NULL DEFAULT 0, invalid INTEGER NOT NULL DEFAULT 0, last_seen INTEGER);
    CREATE TABLE IF NOT EXISTS nonces(install_id TEXT NOT NULL, nonce TEXT NOT NULL, ts INTEGER NOT NULL, PRIMARY KEY(install_id, nonce));
    CREATE TABLE IF NOT EXISTS requests(install_id TEXT NOT NULL, ts INTEGER NOT NULL, events INTEGER NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_req ON requests(install_id, ts);
    CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY, install_id TEXT NOT NULL, uid TEXT NOT NULL, kind TEXT NOT NULL, ts INTEGER NOT NULL,
        game_time TEXT, waterbody INTEGER NOT NULL, cx INTEGER, cz INTEGER, bait INTEGER, hook INTEGER, chance REAL,
        fish INTEGER, weight INTEGER, windex INTEGER, rod INTEGER, received INTEGER NOT NULL, UNIQUE(install_id, uid));
    CREATE INDEX IF NOT EXISTS ix_ev_spot ON events(waterbody, cx, cz);
    CREATE TABLE IF NOT EXISTS revoked(install_id TEXT PRIMARY KEY, at INTEGER NOT NULL, reason TEXT);
    """)
    c.commit(); c.close()


def now_ms():
    return int(time.time() * 1000)


def verify(pub_der, msg, sig):
    key = serialization.load_der_public_key(pub_der)
    if not isinstance(key, ec.EllipticCurvePublicKey) or key.curve.name != "secp256r1":
        return False
    if len(sig) == 64:   # IEEE P1363 r||s (.NET default) -> DER
        sig = utils.encode_dss_signature(int.from_bytes(sig[:32], "big"), int.from_bytes(sig[32:], "big"))
    try:
        key.verify(sig, msg, ec.ECDSA(hashes.SHA256()))
        return True
    except InvalidSignature:
        return False


def check_event(e, now):
    """Plausibility. Returns (row tuple, None) or (None, reason). Nothing is invented: missing optional fields stay NULL."""
    if not isinstance(e, dict): return None, "not_object"
    kind = e.get("kind")
    if kind not in KINDS: return None, "kind"
    uid = str(e.get("uid", ""))[:64]
    if len(uid) < 8: return None, "uid"
    ts = e.get("ts")
    if not isinstance(ts, int) or ts > now + CLOCK_SKEW_MS or ts < now - 7 * 24 * 3600 * 1000: return None, "time"
    wb = e.get("waterbody")
    if not isinstance(wb, int) or (WATERBODIES and wb not in WATERBODIES): return None, "waterbody"
    rod = e.get("rod")
    if rod is not None and (not isinstance(rod, int) or rod < 0 or rod > 4): return None, "rod"
    cell = e.get("cell")
    cx = cz = None
    if cell is not None:
        if not (isinstance(cell, list) and len(cell) == 2 and all(isinstance(v, int) and 0 <= v <= 2000 for v in cell)): return None, "cell"
        cx, cz = cell
    fish, w, wi = e.get("fish"), e.get("weight"), e.get("windex")
    if fish is not None and not isinstance(fish, int): return None, "fish"
    if w is not None:
        if not isinstance(w, int) or w <= 0 or w > 200000: return None, "weight"
        if fish in FISH:
            lo, hi = FISH[fish]
            if w < lo * 0.5 or w > hi * 1.05: return None, "weight_range"
    if wi is not None and (not isinstance(wi, int) or wi < 0 or wi > 3): return None, "windex"
    if kind in ("bite", "catch") and fish is None: return None, "no_fish"
    gt = e.get("game_time")
    if gt is not None and not (isinstance(gt, str) and len(gt) == 5 and gt[2] == ":"): return None, "game_time"
    ch = e.get("chance")
    if ch is not None and (not isinstance(ch, (int, float)) or ch < 0 or ch > 1000): return None, "chance"
    bait, hook = e.get("bait"), e.get("hook")
    if bait is not None and not isinstance(bait, int): return None, "bait"
    if hook is not None and not isinstance(hook, int): return None, "hook"
    return (uid, kind, ts, gt, wb, cx, cz, bait, hook, ch, fish, w, wi, rod), None


def burst_ok(rows):
    """Per rod at most one bite/catch every 6 s (the game checks a rod about every 8 s)."""
    last = {}
    for r in sorted(rows, key=lambda r: r[2]):
        if r[1] == "empty_bite": continue
        k = r[13]
        if k in last and r[2] - last[k] < 6000: return False
        last[k] = r[2]
    return True


class H(BaseHTTPRequestHandler):
    server_version = "AFO/1"
    sys_version = ""

    def log_message(self, *a):   # no IP logging
        pass

    def send(self, code, obj):
        b = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers(); self.wfile.write(b)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n <= 0 or n > MAX_BODY: return None
        return self.rfile.read(n)

    def do_GET(self):
        u = urlparse(self.path)
        if u.path == "/api/v1/health":
            return self.send(200, {"ok": True, "v": 1, "server_time": now_ms()})
        if u.path == "/api/v1/spots":
            q = parse_qs(u.query)
            try: wb = int(q.get("waterbody", [""])[0])
            except ValueError: return self.send(400, {"error": "waterbody"})
            fish = q.get("fish", [None])[0]
            c = db()
            sql = ("SELECT e.cx, e.cz, e.fish, count(*) n, count(DISTINCT e.install_id) k, max(e.windex) FROM events e JOIN installs i ON i.install_id=e.install_id "
                   "WHERE e.waterbody=? AND e.cx IS NOT NULL AND e.kind IN ('bite','catch') AND i.status='trusted' AND e.ts>?")
            args = [wb, now_ms() - 30 * 24 * 3600 * 1000]
            if fish:
                sql += " AND e.fish=?"; args.append(int(fish))
            sql += " GROUP BY e.cx, e.cz, e.fish HAVING k>=? ORDER BY n DESC LIMIT 2000"; args.append(CONSENSUS)
            rows = c.execute(sql, args).fetchall(); c.close()
            return self.send(200, {"waterbody": wb, "consensus": CONSENSUS, "spots": [{"cell": [r[0], r[1]], "fish": r[2], "bites": r[3], "installs": r[4], "max_windex": r[5]} for r in rows]})
        self.send(404, {"error": "not_found"})

    def do_POST(self):
        u = urlparse(self.path)
        raw = self.body()
        if raw is None: return self.send(413, {"error": "body_size"})
        if u.path == "/api/v1/register": return self.register(raw)
        if u.path == "/api/v1/submit": return self.submit(raw)
        self.send(404, {"error": "not_found"})

    def register(self, raw):
        try:
            pub = base64.b64decode(json.loads(raw)["pubkey"], validate=True)
            key = serialization.load_der_public_key(pub)
            if not isinstance(key, ec.EllipticCurvePublicKey) or key.curve.name != "secp256r1": raise ValueError
        except Exception:
            return self.send(400, {"error": "pubkey"})
        iid = hashlib.sha256(pub).hexdigest()[:32]
        t = int(time.time())
        with _lock:
            c = db()
            row = c.execute("SELECT status FROM installs WHERE install_id=?", (iid,)).fetchone()
            if row is None:
                _reg_times[:] = [x for x in _reg_times if x > t - 3600]
                if len(_reg_times) >= MAX_REG_PER_HOUR:
                    c.close(); return self.send(429, {"error": "too_many_registrations"})
                _reg_times.append(t)
                c.execute("INSERT INTO installs(install_id,pubkey,created,last_seen) VALUES(?,?,?,?)", (iid, pub, t, t)); c.commit()
                status = "quarantine"
            else:
                status = row[0]
            c.close()
        self.send(200, {"install_id": iid, "status": status, "server_time": now_ms()})

    def submit(self, raw):
        h = self.headers
        iid, ts, nonce, sig = h.get("X-AFO-Id", ""), h.get("X-AFO-Ts", ""), h.get("X-AFO-Nonce", ""), h.get("X-AFO-Sig", "")
        try:
            ts_i = int(ts); sig_b = base64.b64decode(sig, validate=True)
        except Exception:
            return self.send(401, {"error": "auth_headers"})
        now = now_ms()
        if abs(now - ts_i) > CLOCK_SKEW_MS: return self.send(401, {"error": "clock"})
        if not (16 <= len(nonce) <= 64): return self.send(401, {"error": "nonce"})
        c = db()
        try:
            if c.execute("SELECT 1 FROM revoked WHERE install_id=?", (iid,)).fetchone(): return self.send(403, {"error": "revoked"})
            row = c.execute("SELECT pubkey, status, created, valid FROM installs WHERE install_id=?", (iid,)).fetchone()
            if row is None: return self.send(401, {"error": "unknown_install"})
            msg = ("POST\n/api/v1/submit\n%s\n%s\n%s" % (ts, nonce, hashlib.sha256(raw).hexdigest())).encode()
            if not verify(row[0], msg, sig_b): return self.send(401, {"error": "signature"})
            try:
                c.execute("INSERT INTO nonces VALUES(?,?,?)", (iid, nonce, now))
            except sqlite3.IntegrityError:
                return self.send(409, {"error": "replay"})
            hour = c.execute("SELECT count(*), ifnull(sum(events),0) FROM requests WHERE install_id=? AND ts>?", (iid, now - 3600 * 1000)).fetchone()
            day = c.execute("SELECT ifnull(sum(events),0) FROM requests WHERE install_id=? AND ts>?", (iid, now - 86400 * 1000)).fetchone()[0]
            if hour[0] >= MAX_REQ_PER_HOUR: c.commit(); return self.send(429, {"error": "rate"})
            try:
                events = json.loads(raw)["events"]
                if not isinstance(events, list) or len(events) > MAX_EVENTS: raise ValueError
            except Exception:
                c.commit(); return self.send(400, {"error": "payload"})
            if day + len(events) > MAX_EVENTS_PER_DAY: c.commit(); return self.send(429, {"error": "daily_limit"})
            ok, reasons = [], {}
            for e in events:
                r, why = check_event(e, now)
                if r: ok.append(r)
                else: reasons[why] = reasons.get(why, 0) + 1
            if ok and not burst_ok(ok):
                reasons["too_frequent"] = len(ok); ok = []
            acc = 0
            for r in ok:
                cur = c.execute("INSERT OR IGNORE INTO events(install_id,uid,kind,ts,game_time,waterbody,cx,cz,bait,hook,chance,fish,weight,windex,rod,received) "
                                "VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", (iid,) + r + (now,))
                acc += cur.rowcount
            bad = sum(reasons.values())
            c.execute("INSERT INTO requests VALUES(?,?,?)", (iid, now, len(events)))
            c.execute("UPDATE installs SET valid=valid+?, invalid=invalid+?, last_seen=? WHERE install_id=?", (acc, bad, int(time.time()), iid))
            # quarantine -> trusted: old enough, enough valid data, few rejects
            v = row[3] + acc
            if row[1] == "quarantine" and time.time() - row[2] >= QUARANTINE_S and v >= QUARANTINE_EVENTS:
                inv = c.execute("SELECT invalid FROM installs WHERE install_id=?", (iid,)).fetchone()[0]
                if inv <= v * 0.05: c.execute("UPDATE installs SET status='trusted' WHERE install_id=?", (iid,))
            c.execute("DELETE FROM nonces WHERE ts<?", (now - 86400 * 1000,))
            c.execute("DELETE FROM requests WHERE ts<?", (now - 2 * 86400 * 1000,))
            c.commit()
            self.send(200, {"accepted": acc, "rejected": bad, "reasons": reasons})
        finally:
            c.close()


def admin(argv):
    """afo_server.py revoke <install_id> [reason] | trust <id> | stats"""
    init_db(); c = db()
    if argv[0] == "revoke":
        c.execute("INSERT OR REPLACE INTO revoked VALUES(?,?,?)", (argv[1], int(time.time()), " ".join(argv[2:])[:200]))
        c.execute("UPDATE installs SET status='revoked' WHERE install_id=?", (argv[1],))
    elif argv[0] == "trust":
        c.execute("UPDATE installs SET status='trusted' WHERE install_id=?", (argv[1],))
    elif argv[0] == "stats":
        print(dict(c.execute("SELECT status, count(*) FROM installs GROUP BY 1").fetchall()), c.execute("SELECT count(*) FROM events").fetchone()[0], "events")
    c.commit(); c.close()


if __name__ == "__main__":
    if len(sys.argv) > 1:
        admin(sys.argv[1:]); sys.exit(0)
    init_db()
    srv = ThreadingHTTPServer((HOST, PORT), H)
    srv.daemon_threads = True
    print("AFO server on %s:%d" % (HOST, PORT), flush=True)
    srv.serve_forever()
