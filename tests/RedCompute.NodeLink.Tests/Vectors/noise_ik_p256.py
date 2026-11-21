# Provenance of NoiseTests' fixed vectors (P0.5a crypto review, 2026-11-21).
# The reviewer's independent Noise_IK_P256_AESGCM_SHA256 (Noise rev 34) on pyca/cryptography (OpenSSL),
# unchanged above the marked lines at the end, which print each message for NoiseTests to store as hex.
# Run: python -I noise_ik_p256.py   (needs the cryptography package).
# Prints: the transcript digest (PinnedVector), the RFC 5903 8.1 shared x, then M1, M2 and the record.

# Independent Noise_IK_P256_AESGCM_SHA256 per Noise rev 34, on pyca/cryptography (OpenSSL).
import hashlib, hmac, struct
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives import serialization

def key(seed):
    d = int.from_bytes(hashlib.sha256(bytes([seed])).digest(), 'big')
    return ec.derive_private_key(d, ec.SECP256R1())
def pub(k):
    return k.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
def dh(k, p):
    return k.exchange(ec.ECDH(), ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), p))
def hkdf(ck, ikm):
    t = hmac.new(ck, ikm, hashlib.sha256).digest()
    o1 = hmac.new(t, b'\x01', hashlib.sha256).digest()
    o2 = hmac.new(t, o1 + b'\x02', hashlib.sha256).digest()
    return o1, o2
def nonce(n): return b'\x00'*4 + struct.pack('>Q', n)

class SS:
    def __init__(s, name):
        name = name.encode()
        s.h = name + b'\x00'*(32-len(name)) if len(name) <= 32 else hashlib.sha256(name).digest()
        s.ck = s.h; s.k = None; s.n = 0
    def mh(s, d): s.h = hashlib.sha256(s.h + d).digest()
    def mk(s, ikm): s.ck, s.k = hkdf(s.ck, ikm); s.n = 0
    def eah(s, pt):
        c = AESGCM(s.k).encrypt(nonce(s.n), pt, s.h); s.n += 1; s.mh(c); return c

NODE = "node_01JCK4Z8X9T5V6W7Y8Z9A0B1C2"
rs, s_, e, re_ = key(1), key(2), key(3), key(4)
st = SS("Noise_IK_P256_AESGCM_SHA256")
st.mh(("leaf-node-link.v1\n" + NODE).encode())
st.mh(pub(rs))
st.mh(pub(e)); st.mk(dh(e, pub(rs)))
cs = st.eah(pub(s_))
st.mk(dh(s_, pub(rs)))
cp = st.eah(b"binding")
m1 = b'\x10' + pub(e) + cs + cp
st.mh(pub(re_)); st.mk(dh(e, pub(re_))); st.mk(dh(s_, pub(re_)))
c2 = st.eah(b"")
m2 = b'\x11' + pub(re_) + c2
k1, k2 = hkdf(st.ck, b"")
rec = b'\x12' + AESGCM(k1).encrypt(nonce(0), b"frame", b'\x12')
print(hashlib.sha256(m1 + m2 + rec).hexdigest().upper())
# RFC 5903 8.1 sanity of the DH encoding
i = ec.derive_private_key(int("C88F01F510D9AC3F70A292DAA2316DE544E9AAB8AFE84049C62A9C57862D1433",16), ec.SECP256R1())
gr = bytes.fromhex("04D12DFB5289C8D4F81208B70270398C342296970A0BCCB74C736FC7554494BF6356FBF3CA366CC23E8157854C13C58D6AAC23F046ADA30F8353E74F33039872AB")
print(dh(i, gr).hex().upper())

# ---- added for the stored vectors
print("M1", m1.hex().upper())
print("M2", m2.hex().upper())
print("REC", rec.hex().upper())
