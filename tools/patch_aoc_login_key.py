#!/usr/bin/env python3
"""Live-patch the AoC client's embedded DH public key in memory (Funcom's -> ours), for LOCAL login testing.

Companion to docs/login-decryption-plan.md (work item 1, the no-file-touch variant). The retail client encrypts
the login block (TEA-CBC) with a key derived from a DH shared secret against Funcom's public key, which we can't
reproduce (we don't have Funcom's private exponent). Swapping the embedded Funcom pubkey for OUR pubkey (CELLAO_PUB)
makes the client derive a secret our server CAN reproduce with CELLAO_PRIV -> real password validation works.

Both keys are 256-char ASCII hex (same length), so this is an in-place overwrite. The client maps its PE
MAP_PRIVATE, so a /proc/<pid>/mem write copies-on-write and changes ONLY the running process - the game file on
disk is untouched (no Steam "verify integrity" fallout, nothing to back up). Run it AFTER launching the client and
BEFORE clicking Play/Login (the key string is read fresh per handshake, so any time before login works).

Usage (needs root to write another process's memory):
    sudo ./patch_aoc_login_key.py            # find AoC, replace Funcom key -> CELLAO key
    sudo ./patch_aoc_login_key.py --dry-run  # just locate the key, write nothing
    sudo ./patch_aoc_login_key.py --restore  # put Funcom's key back (CELLAO -> Funcom)
    sudo ./patch_aoc_login_key.py --pid 1234 # target a specific pid

This patches the user's own client for interop with their own emulator (same basis as the rest of the RE work).
"""
import os
import re
import sys
import glob
import argparse

# From docs/login-decryption-plan.md. Both are exactly 256 ASCII hex chars (g^priv mod N, the 1024-bit DH public).
FUNCOM = (b"9c32cc23d559ca90fc31be72df817d0e124769e809f936bc14360ff4bed758f2"
          b"60a0d596584eacbbc2b88bdd410416163e11dbf62173393fbc0c6fefb2d855f1"
          b"a03dec8e9f105bbad91b3437d8eb73fe2f44159597aa4053cf788d2f9d7012fb8"
          b"d7c4ce3876f7d6cd5d0c31754f4cd96166708641958de54a6def5657b9f2e92")
CELLAO = (b"26b5a3b4ac1177f24a2d9de44bafef477ff23ef1cb5f646919b1be2651605303"
          b"0b65d5afb60cef6f49de539958ba0b7922a099319b8016a8673cb27a696ae4b6"
          b"0fdece25ddcdad42e7f0056b87fc35687fe033b242e17e960d79806fd46c4a79"
          b"cbc64f558660a50cabc1c242dace70de6af452e3433f97e30e202567f187de70")
assert len(FUNCOM) == len(CELLAO) == 256, (len(FUNCOM), len(CELLAO))

CHUNK = 8 << 20  # 8 MiB scan window


def find_pids():
    pids = []
    for d in glob.glob("/proc/[0-9]*"):
        try:
            cmd = open(os.path.join(d, "cmdline"), "rb").read()
        except OSError:
            continue
        if b"AgeOfConan" in cmd and b".exe" in cmd:
            pids.append(int(os.path.basename(d)))
    return pids


def iter_readable_regions(pid):
    for line in open(f"/proc/{pid}/maps"):
        m = re.match(r"([0-9a-f]+)-([0-9a-f]+) (\S{4})\s+\S+\s+\S+\s+\S+\s*(.*)", line)
        if not m:
            continue
        perms = m.group(3)
        if perms[0] != "r":            # need read to scan
            continue
        path = m.group(4)
        if path.startswith("[v") or path in ("[vsyscall]",):   # vvar/vdso/vsyscall: skip
            continue
        yield int(m.group(1), 16), int(m.group(2), 16), perms, path


def scan_and_patch(pid, needle, repl, dry_run):
    hits = 0
    try:
        mem = open(f"/proc/{pid}/mem", "r+b", buffering=0)
    except OSError as e:
        print(f"  pid {pid}: cannot open /proc/{pid}/mem ({e}); are you root?")
        return 0
    overlap = len(needle) - 1
    with mem:
        for start, end, perms, path in iter_readable_regions(pid):
            addr = start
            carry = b""
            carry_addr = start
            while addr < end:
                size = min(CHUNK, end - addr)
                try:
                    mem.seek(addr)
                    buf = mem.read(size)
                except (OSError, ValueError, OverflowError):
                    break               # unreadable region (e.g. special mapping)
                if not buf:
                    break
                window = carry + buf
                base = carry_addr
                off = window.find(needle)
                while off != -1:
                    hit_addr = base + off
                    label = f"0x{hit_addr:x} ({perms} {path or 'anon'})"
                    if dry_run:
                        print(f"  FOUND  {label}")
                        hits += 1
                    else:
                        try:
                            mem.seek(hit_addr)
                            mem.write(repl)
                            mem.seek(hit_addr)
                            ok = mem.read(len(repl)) == repl
                            print(f"  {'PATCHED' if ok else 'WROTE?? (verify failed)'} {label}")
                            hits += 1 if ok else 0
                        except OSError as e:
                            print(f"  FOUND but write FAILED {label}: {e}")
                    off = window.find(needle, off + 1)
                carry = window[-overlap:] if overlap else b""
                carry_addr = base + len(window) - len(carry)
                addr += size
    return hits


def main():
    ap = argparse.ArgumentParser(description="Live-patch the AoC client DH pubkey (Funcom -> CELLAO) for login testing.")
    ap.add_argument("--dry-run", action="store_true", help="locate the key, write nothing")
    ap.add_argument("--restore", action="store_true", help="reverse the patch (CELLAO -> Funcom)")
    ap.add_argument("--pid", type=int, help="target a specific pid instead of auto-detecting")
    args = ap.parse_args()

    if not args.dry_run and os.geteuid() != 0:
        print("Writing another process's memory needs root. Re-run with sudo (or use --dry-run).")
        return 1

    needle, repl = (CELLAO, FUNCOM) if args.restore else (FUNCOM, CELLAO)
    action = "restore (CELLAO -> Funcom)" if args.restore else "patch (Funcom -> CELLAO)"

    pids = [args.pid] if args.pid else find_pids()
    if not pids:
        print("No running AgeOfConan*.exe process found. Launch the client first.")
        return 1

    total = 0
    for pid in pids:
        print(f"pid {pid}: {action} ...")
        total += scan_and_patch(pid, needle, repl, args.dry_run)

    if total == 0:
        print("\nNo occurrences found/patched. If the client is at the login screen already, the key may have been\n"
              "read; relaunch and patch before logging in. (Or it was already patched/restored.)")
        return 1
    print(f"\nDone: {total} occurrence(s) {'located' if args.dry_run else action}.")
    print("Now log in. The server derives the shared secret with CELLAO_PRIV and can validate the real password\n"
          "(once UniverseAgentListener.AnswerChallenge is wired to LoginEncryption - work item 2).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
