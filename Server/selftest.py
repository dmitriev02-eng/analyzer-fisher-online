import base64, hashlib, json, os, ssl, time, urllib.request
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils
B = "https://127.0.0.1:8443"
ctx = ssl._create_unverified_context()
def req(path, body=None, headers=None):
    r = urllib.request.Request(B + path, data=body, headers=headers or {}, method="POST" if body is not None else "GET")
    try:
        with urllib.request.urlopen(r, context=ctx, timeout=10) as f: return f.status, json.loads(f.read())
    except urllib.error.HTTPError as e: return e.code, json.loads(e.read() or b"{}")
key = ec.generate_private_key(ec.SECP256R1())
pub = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
s, r = req("/api/v1/register", json.dumps({"pubkey": base64.b64encode(pub).decode()}).encode(), {"Content-Type": "application/json"})
print("register", s, r.get("status")); iid = r["install_id"]
def submit(events, nonce=None, tamper=False, other_key=None):
    body = json.dumps({"events": events}).encode(); ts = str(int(time.time() * 1000)); nonce = nonce or os.urandom(12).hex()
    msg = ("POST\n/api/v1/submit\n%s\n%s\n%s" % (ts, nonce, hashlib.sha256(body).hexdigest())).encode()
    der = (other_key or key).sign(msg, ec.ECDSA(hashes.SHA256())); r_, s_ = utils.decode_dss_signature(der)
    sig = base64.b64encode(r_.to_bytes(32, "big") + s_.to_bytes(32, "big")).decode()
    if tamper: body = body.replace(b'"rod": 1', b'"rod": 2')
    return req("/api/v1/submit", body, {"X-AFO-Id": iid, "X-AFO-Ts": ts, "X-AFO-Nonce": nonce, "X-AFO-Sig": sig, "Content-Type": "application/json"}), nonce
now = int(time.time() * 1000)
ok = [{"uid": "t%08d" % i, "kind": "catch", "ts": now - 60000 * i, "waterbody": 23, "cell": [210, 290], "fish": 71, "weight": 400, "windex": 1, "rod": 1, "game_time": "06:10"} for i in range(3)]
(res, n) = submit(ok); print("valid batch", res)
print("replay same nonce", submit(ok, nonce=n)[0])
print("tampered body", submit([dict(ok[0], uid="x1")], tamper=True)[0])
print("signed by other key", submit([dict(ok[0], uid="x2")], other_key=ec.generate_private_key(ec.SECP256R1()))[0])
fake = [{"uid": "fake0001", "kind": "catch", "ts": now, "waterbody": 23, "fish": 71, "weight": 150000, "rod": 1},
        {"uid": "fake0002", "kind": "catch", "ts": now + 3600_000, "waterbody": 23, "fish": 71, "weight": 400, "rod": 1},
        {"uid": "fake0003", "kind": "catch", "ts": now, "waterbody": 999, "fish": 71, "weight": 400, "rod": 1},
        {"uid": "fake0004", "kind": "catch", "ts": now, "waterbody": 23, "fish": 71, "weight": 400, "rod": 9}]
print("implausible", submit(fake)[0])
burst = [{"uid": "burst%04d" % i, "kind": "bite", "ts": now - i * 1000, "waterbody": 23, "fish": 71, "weight": 400, "rod": 0} for i in range(5)]
print("too frequent", submit(burst)[0])
print("spots (quarantine install not counted)", req("/api/v1/spots?waterbody=23")[1])
