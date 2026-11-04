"""Headless replacement for the UXM + WitchyBND steps in the README.

Reads the game's own encrypted archives directly and produces the `er-files`
layout the rest of tools/ expects, without any of the Windows GUI tools:

  <ER_FILES>/chr/c0000.anibnd.dcx           player animations (TAE + HKX)
  <ER_FILES>/chr/c0000_a00_hi.anibnd.dcx    ...
  <ER_FILES>/regulation-bin/*.param         weapons, stamina, motion values
  <ER_FILES>/sd/{enus/,}cs_*.bnk            the Wwise sound banks (optional)

The BHD5 archives are RSA-encrypted (per-archive public key) and each file may
be AES-encrypted in ranges; regulation.bin is AES-256-CBC over a DCX(ZSTD) BND4.
This is a narrowed port of Ekey's ER.BDT.Tool (ER.BDT.Tool) plus the regulation
key from SoulsFormats' RegulationDecryptor.

Needs pycryptodome and zstandard:  python -m pip install pycryptodome zstandard
The Oodle DLL (oo2core_6_win64.dll) is still read from ER_GAME_DIR by erfmt.py
when the other tools decompress these .dcx files.

Usage: python tools/unpack_game_files.py
"""
import base64
import os
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import paths

HASH_MASK = (1 << 64) - 1

# RSA public keys, one per BHD5 archive (Ekey's BinderKeys / UXM's ArchiveKeys).
KEYS = {
    "Data0": """-----BEGIN RSA PUBLIC KEY-----
MIIBCwKCAQEA9Rju2whruXDVQZpfylVEPeNxm7XgMHcDyaaRUIpXQE0qEo+6Y36L
P0xpFvL0H0kKxHwpuISsdgrnMHJ/yj4S61MWzhO8y4BQbw/zJehhDSRCecFJmFBz
3I2JC5FCjoK+82xd9xM5XXdfsdBzRiSghuIHL4qk2WZ/0f/nK5VygeWXn/oLeYBL
jX1S8wSSASza64JXjt0bP/i6mpV2SLZqKRxo7x2bIQrR1yHNekSF2jBhZIgcbtMB
xjCywn+7p954wjcfjxB5VWaZ4hGbKhi1bhYPccht4XnGhcUTWO3NmJWslwccjQ4k
sutLq3uRjLMM0IeTkQO6Pv8/R7UNFtdCWwIERzH8IQ==
-----END RSA PUBLIC KEY-----""",
    "Data1": """-----BEGIN RSA PUBLIC KEY-----
MIIBCwKCAQEAxaBCHQJrtLJiJNdG9nq3deA9sY4YCZ4dbTOHO+v+YgWRMcE6iK6o
ZIJq+nBMUNBbGPmbRrEjkkH9M7LAypAFOPKC6wMHzqIMBsUMuYffulBuOqtEBD11
CAwfx37rjwJ+/1tnEqtJjYkrK9yyrIN6Y+jy4ftymQtjk83+L89pvMMmkNeZaPON
4O9q5M9PnFoKvK8eY45ZV/Jyk+Pe+xc6+e4h4cx8ML5U2kMM3VDAJush4z/05hS3
/bC4B6K9+7dPwgqZgKx1J7DBtLdHSAgwRPpijPeOjKcAa2BDaNp9Cfon70oC+ZCB
+HkQ7FjJcF7KaHsH5oHvuI7EZAl2XTsLEQIENa/2JQ==
-----END RSA PUBLIC KEY-----""",
    "Data2": """-----BEGIN RSA PUBLIC KEY-----
MIIBDAKCAQEA0iDVVQ230RgrkIHJNDgxE7I/2AaH6Li1Eu9mtpfrrfhfoK2e7y4O
WU+lj7AGI4GIgkWpPw8JHaV970Cr6+sTG4Tr5eMQPxrCIH7BJAPCloypxcs2BNfT
GXzm6veUfrGzLIDp7wy24lIA8r9ZwUvpKlN28kxBDGeCbGCkYeSVNuF+R9rN4OAM
RYh0r1Q950xc2qSNloNsjpDoSKoYN0T7u5rnMn/4mtclnWPVRWU940zr1rymv4Jc
3umNf6cT1XqrS1gSaK1JWZfsSeD6Dwk3uvquvfY6YlGRygIlVEMAvKrDRMHylsLt
qqhYkZNXMdy0NXopf1rEHKy9poaHEmJldwIFAP////8=
-----END RSA PUBLIC KEY-----""",
    "Data3": """-----BEGIN RSA PUBLIC KEY-----
MIIBCwKCAQEAvRRNBnVq3WknCNHrJRelcEA2v/OzKlQkxZw1yKll0Y2Kn6G9ts94
SfgZYbdFCnIXy5NEuyHRKrxXz5vurjhrcuoYAI2ZUhXPXZJdgHywac/i3S/IY0V/
eDbqepyJWHpP6I565ySqlol1p/BScVjbEsVyvZGtWIXLPDbx4EYFKA5B52uK6Gdz
4qcyVFtVEhNoMvg+EoWnyLD7EUzuB2Khl46CuNictyWrLlIHgpKJr1QD8a0ld0PD
PHDZn03q6QDvZd23UW2d9J+/HeBt52j08+qoBXPwhndZsmPMWngQDaik6FM7EVRQ
etKPi6h5uprVmMAS5wR/jQIVTMpTj/zJdwIEXszeQw==
-----END RSA PUBLIC KEY-----""",
    # sd/sd and sd/sd_dlc02 (the sound banks) share a key.
    "sd": """-----BEGIN RSA PUBLIC KEY-----
MIIBCwKCAQEAmYJ/5GJU4boJSvZ81BFOHYTGdBWPHnWYly3yWo01BYjGRnz8NTkz
DHUxsbjIgtG5XqsQfZstZILQ97hgSI5AaAoCGrT8sn0PeXg2i0mKwL21gRjRUdvP
Dp1Y+7hgrGwuTkjycqqsQ/qILm4NvJHvGRd7xLOJ9rs2zwYhceRVrq9XU2AXbdY4
pdCQ3+HuoaFiJ0dW0ly5qdEXjbSv2QEYe36nWCtsd6hEY9LjbBX8D1fK3D2c6C0g
NdHJGH2iEONUN6DMK9t0v2JBnwCOZQ7W+Gt7SpNNrkx8xKEM8gH9na10g9ne11Mi
O1FnLm8i4zOxVdPHQBKICkKcGS1o3C2dfwIEXw/f3w==
-----END RSA PUBLIC KEY-----""",
}

# The nonstandard RSA exponent (from the game's own PEM keys) is read straight
# out of each key; nothing else here is key-specific.

# Dictionary line (as in UXM's EldenRingDictionary) -> path under ER_FILES.
BINDERS = {
    "/chr/c0000.anibnd.dcx": "chr/c0000.anibnd.dcx",
    "/chr/c0000_a00_hi.anibnd.dcx": "chr/c0000_a00_hi.anibnd.dcx",
    "/chr/c0000_a00_lo.anibnd.dcx": "chr/c0000_a00_lo.anibnd.dcx",
    "/chr/c0000_a00_md.anibnd.dcx": "chr/c0000_a00_md.anibnd.dcx",
    "/chr/c0000_a0x.anibnd.dcx": "chr/c0000_a0x.anibnd.dcx",
    "/chr/c0000_a1x.anibnd.dcx": "chr/c0000_a1x.anibnd.dcx",
    "/chr/c0000_a2x.anibnd.dcx": "chr/c0000_a2x.anibnd.dcx",
    "/chr/c0000_a3x.anibnd.dcx": "chr/c0000_a3x.anibnd.dcx",
    "/chr/c0000_a4x.anibnd.dcx": "chr/c0000_a4x.anibnd.dcx",
    "/chr/c0000_a5x.anibnd.dcx": "chr/c0000_a5x.anibnd.dcx",
    # Shadow of the Erdtree's player styles (patched into Data3, not DLC.bdt).
    "/chr/c0000_dlc01.anibnd.dcx": "chr/c0000_dlc01.anibnd.dcx",
    "/chr/c0000_dlc02.anibnd.dcx": "chr/c0000_dlc02.anibnd.dcx",
}

# Optional: the Wwise banks the sound baker reads (see README). Without them
# the sandbox runs silent.
SOUND_BANKS = {
    "/cs_smain.bnk": "sd/cs_smain.bnk",
    "/enus/cs_main.bnk": "sd/enus/cs_main.bnk",
}

# AES-256 key for Elden Ring's regulation.bin (SoulsFormats RegulationDecryptor).
REGULATION_KEY = bytes([
    0x99, 0xBF, 0xFC, 0x36, 0x6A, 0x6B, 0xC8, 0xC6, 0xF5, 0x82, 0x7D, 0x09, 0x36, 0x02, 0xD6, 0x76,
    0xC4, 0x28, 0x92, 0xA0, 0x1C, 0x20, 0x7F, 0xB0, 0x24, 0xD3, 0xAF, 0x4E, 0x49, 0x3F, 0xEF, 0x99,
])


def stream_hash(s: str) -> int:
    h = 0
    for ch in s.lower():
        h = (ord(ch) + 133 * h) & HASH_MASK
    return h


def parse_rsa_public_key(pem: str):
    b64 = "".join(line.strip() for line in pem.splitlines() if "KEY" not in line)
    der = base64.b64decode(b64)

    def tlv(d, i):
        i += 1  # tag
        length = d[i]
        i += 1
        if length & 0x80:
            count = length & 0x7F
            length = int.from_bytes(d[i:i + count], "big")
            i += count
        return d[i:i + length], i + length

    seq, _ = tlv(der, 0)
    n_bytes, i = tlv(seq, 0)
    e_bytes, _ = tlv(seq, i)
    return int.from_bytes(n_bytes, "big"), int.from_bytes(e_bytes, "big")


def rsa_decrypt(data: bytes, n: int, e: int) -> bytes:
    # Ciphertext blocks are the key size; the recovered blocks are one byte
    # shorter (BouncyCastle's decrypt output block size is keySize - 1).
    size = (n.bit_length() + 7) // 8
    out = bytearray()
    for i in range(0, len(data), size):
        block = data[i:i + size].ljust(size, b"\0")
        out += pow(int.from_bytes(block, "big"), e, n).to_bytes(size - 1, "big")
    return bytes(out)


def aes_ecb_decrypt(buf: bytearray, key: bytes, ranges) -> None:
    from Crypto.Cipher import AES

    cipher = AES.new(key, AES.MODE_ECB)
    for start, end in ranges:
        if start != -1 and end != -1 and start != end:
            buf[start:end] = cipher.decrypt(bytes(buf[start:end]))


def bhd_entries(bhd: bytes):
    magic, version, _flags, _size, bucket_count, _dir = struct.unpack_from("<IiIIii", bhd, 0)
    if magic != 0x35444842:
        raise ValueError("not a BHD5 archive (bad magic)")
    if version != 511:
        raise ValueError(f"unexpected BHD5 version {version}")
    pos = 24
    salt_len = struct.unpack_from("<i", bhd, pos)[0]
    pos += 4 + salt_len

    entries = []
    for _ in range(bucket_count):
        entry_count, entry_offset = struct.unpack_from("<ii", bhd, pos)
        pos += 8
        for j in range(entry_count):
            p = entry_offset + j * 40
            name_hash, padded_size, size, offset, _sha, aes_off = struct.unpack_from("<Qiiqqq", bhd, p)
            key, ranges = None, []
            if aes_off != 0:
                key = bhd[aes_off:aes_off + 16]
                (total,) = struct.unpack_from("<i", bhd, aes_off + 16)
                ranges = [struct.unpack_from("<qq", bhd, aes_off + 20 + k * 16) for k in range(total)]
            entries.append((name_hash, padded_size, size, offset, key, ranges))
    return entries


def extract(targets: dict, bhd_path: Path, bdt_path: Path, key_pem: str, out: Path, truncate: bool) -> set:
    """Pull the files in `targets` (dictionary line -> path under `out`) out of
    one BHD5/BDT pair. `truncate` drops the block padding real files do not have."""
    wanted = {stream_hash(line): rel for line, rel in targets.items()}
    n, e = parse_rsa_public_key(key_pem)
    entries = bhd_entries(rsa_decrypt(bhd_path.read_bytes(), n, e))
    found = set()
    with open(bdt_path, "rb") as bdt:
        for name_hash, padded_size, size, offset, key, ranges in entries:
            rel = wanted.get(name_hash)
            if rel is None:
                continue
            bdt.seek(offset)
            buf = bytearray(bdt.read(padded_size))
            if key is not None:
                aes_ecb_decrypt(buf, key, ranges)
            if truncate:
                del buf[size:]
            dest = out / rel
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(bytes(buf))
            found.add(rel)
            print(f"[{bhd_path.stem}] {rel}  ({len(buf)} bytes)")
    return found


def unpack_binders(game: Path, out: Path) -> None:
    found = set()
    for archive in ("Data0", "Data1", "Data2", "Data3"):
        bhd_path, bdt_path = game / f"{archive}.bhd", game / f"{archive}.bdt"
        if not bhd_path.exists() or not bdt_path.exists():
            continue
        try:
            found |= extract(BINDERS, bhd_path, bdt_path, KEYS[archive], out, truncate=False)
        except Exception as exc:
            print(f"[{archive}] skipped: {exc}")
    missing = [name for name in BINDERS.values() if name not in found]
    if missing:
        sys.exit("not found in any archive: " + ", ".join(missing))


def unpack_sounds(game: Path, out: Path) -> None:
    bhd_path = game / "sd" / "sd.bhd"
    if not bhd_path.exists():
        print("  no sd/sd.bhd: skipping sounds (the sandbox will run silent)")
        return
    try:
        found = extract(SOUND_BANKS, bhd_path, game / "sd" / "sd.bdt", KEYS["sd"], out, truncate=True)
    except Exception as exc:
        print(f"  [sd] skipped: {exc}")
        return
    missing = [name for name in SOUND_BANKS.values() if name not in found]
    if missing:
        print("  missing from the sd archive: " + ", ".join(missing))


def unpack_regulation(game: Path, out: Path) -> None:
    from Crypto.Cipher import AES
    import zstandard

    raw = (game / "regulation.bin").read_bytes()
    iv, ciphertext = raw[:16], raw[16:]
    if len(ciphertext) % 16:
        ciphertext += b"\0" * (16 - len(ciphertext) % 16)
    data = AES.new(REGULATION_KEY, AES.MODE_CBC, iv).decrypt(ciphertext)
    if data[:4] == b"DCX\0":
        raw_size, comp_size = struct.unpack_from(">II", data, 0x1C)
        data = zstandard.ZstdDecompressor().decompress(data[0x4C:0x4C + comp_size], max_output_size=raw_size)
    if data[:4] != b"BND4":
        sys.exit("regulation.bin did not decrypt to a BND4")
    for _id, name, content in bnd4(data):
        dest = out / "regulation-bin" / Path(name).name
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(content)
    print(f"wrote {len(list((out / 'regulation-bin').glob('*.param')))} param files")


def bnd4(data: bytes):
    """Minimal BND4 reader (same layout erfmt.bnd4 uses)."""
    count, = struct.unpack_from("<i", data, 0x0C)
    entry_size, = struct.unpack_from("<q", data, 0x20)
    unicode = data[0x30]
    fmt = int(f"{data[0x31]:08b}"[::-1], 2)
    for i in range(count):
        p = 0x40 + i * entry_size + 8
        comp_size, = struct.unpack_from("<q", data, p)
        p += 8
        if fmt & 0x20:
            p += 8
        if fmt & 0x10:
            offset, = struct.unpack_from("<q", data, p)
            p += 8
        else:
            offset, = struct.unpack_from("<I", data, p)
            p += 4
        if fmt & 0x02:
            p += 4
        name = ""
        if fmt & 0x0C:
            name_off, = struct.unpack_from("<I", data, p)
            if unicode:
                end = name_off
                while data[end:end + 2] != b"\0\0":
                    end += 2
                name = data[name_off:end].decode("utf-16le")
            else:
                name = data[name_off:data.index(b"\0", name_off)].decode("shift-jis")
        yield i, name, data[offset:offset + comp_size]


def main():
    game = paths.game_dir()
    # paths.er_files() wants chr/ to exist; on a first run it doesn't yet.
    (Path(os.environ.get("ER_FILES", "er-files")) / "chr").mkdir(parents=True, exist_ok=True)
    out = paths.er_files()
    print(f"game: {game}\n out: {out}\n")
    print("1/3  Extracting player animation binders...")
    unpack_binders(game, out)
    print("2/3  Decrypting regulation.bin...")
    unpack_regulation(game, out)
    print("3/3  Extracting the sound banks (optional)...")
    unpack_sounds(game, out)
    print("\nDone. Now run:  python tools/setup.py")


if __name__ == "__main__":
    main()
