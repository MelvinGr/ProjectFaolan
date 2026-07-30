#!/usr/bin/env python3
"""Find inbound packet bodies the server does NOT read to the end.

An unread tail is an UNDECODED FIELD, not a harmless remainder: it is the client telling us something we
throw away. This cross-references two sources that already exist and never had to agree:

  * the CAPTURES - the actual c2s body length per (opcode, method), from `Captures/*.tshark.json`
  * the LISTENERS - how many bytes the matching `case <X>Opcodes.<Method>:` block consumes

and reports every method whose handler provably reads fewer bytes than the shortest captured body.

⚠️ THIS IS A LEAD GENERATOR, NOT A VERDICT. It is a static byte-count over C# source, so:

  * it follows ONE level of delegation (`await _shop.HandleBuyItem(client, packet, body)`), by name, and
    misses deeper chains and codec helpers it cannot resolve - those show up as FALSE POSITIVES
  * any handler reaching a variable-length read (`ReadString`, `ReadRemainingArray`, `body.ToArray()`) is
    SKIPPED rather than guessed at - those are FALSE NEGATIVES
  * a method with no capture is invisible, and a method with no `case` block is not reported

So: read every hit, then confirm it by opening the handler. The first pass (2026-07-27) reported 16 candidates,
of which 9 were real - TeamInvite's message + criminal-status flag, the three search criteria,
SetTutorialStats' step, SetHideInspectOption, CloseShop's vendor id, CinematicTransition's bEntering, and
TryLootItem's i_nDesiredPosition (the slot the player dropped the loot on, ignored to this day: Gitea #462).
All are fixed, so a clean run reports ZERO candidates - which is the point: a new one means a new handler
walked away from bytes the client sent.

Self-check: delete a read from any handler and re-run; the method must reappear.

Once a hit is confirmed, the fix is usually NOT to guess: look the method up in
`docs/re/aoc-client-rpc-methods.json` for the client's own typed signature (see
`docs/protocol/02-rpc.md`, "Reading a c2s body from the client's own signature") and read the real fields.

Usage:  tools/capture/audit_unread_bodies.py
        FAOLAN_CAPTURES=<data-repo>/Captures tools/capture/audit_unread_bodies.py
"""
import collections
import glob
import json
import os
import re
import sys
import pathlib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from captures import captures_dir  # corpus lives in the ProjectFaolan-Data repo

REPO = pathlib.Path(__file__).resolve().parent.parent.parent

# Fixed-width reads, by consumed bytes. Anything else is variable and disqualifies the handler from the
# arithmetic entirely (better to report nothing than a wrong number).
SIZES = {
    "ReadUInt32": 4, "ReadInt32": 4, "ReadFloat": 4, "ReadSingle": 4,
    "ReadUInt16": 2, "ReadInt16": 2,
    "ReadByte": 1, "ReadBool": 1,
    "ReadUInt64": 8, "ReadInt64": 8,
}

# The listeners whose `case` blocks are the dispatch surface.
LISTENERS = [
    "Faolan/UniverseAgent/UniverseAgentListener.cs",
    "Faolan/PlayerAgent/PlayerAgentListener.cs",
    "Faolan/CSPlayerAgent/CSPlayerAgentListener.cs",
    "Faolan/ChatServer/ChatServerListener.cs",
    "Faolan/GameServer/GameServerListener.Inbound.cs",
    "Faolan/GameServer/GameServerListener.Tradepost.cs",
]

# Where a delegated handler body might live.
SOURCE_ROOTS = ["Faolan", "Faolan.Core", "Faolan.Core.Network"]

ENUM_DECL = re.compile(r"\benum\s+([A-Za-z0-9_]*Opcodes)\b")
ENUM_MEMBER = re.compile(r"^\s*([A-Za-z0-9_]+)\s*=\s*0x([0-9A-Fa-f]+)")
ENDPOINT_CONST = re.compile(r"public const uint ([A-Za-z0-9_]+)\s*=\s*0x([0-9A-Fa-f]+)")
CATALOG_ENTRY = re.compile(r"\[Endpoints\.([A-Za-z0-9_]+)\]\s*=\s*typeof\(([A-Za-z0-9_]+)\)")

METHOD_DEF = re.compile(
    r"^\s*(?:public|private|internal|protected|static|async|sealed|override|\s)*[\w<>,\[\]\?\. ]+\s+([A-Za-z0-9_]+)\s*\(([^)]*)\)\s*$")
CASE = re.compile(r"case\s+([A-Za-z0-9_]+Opcodes)\.([A-Za-z0-9_]+)\s*:")
READ = re.compile(r"\b(?:body|reader|stream|s)\.(Read[A-Za-z0-9]*)\s*[(<]")
WHOLE_BODY = re.compile(r"\bbody\.(ToArray|GetBuffer|Remaining)\b")
# Every `name(` on a line that mentions `body`. Deliberately not one-regex-per-call: calls NEST
# (`_trade.Start(id, EntityIdCodec.Parse(...).Id)`) and a single non-overlapping match would
# swallow the inner one - which is exactly the codec helper that does the reading.
CALL = re.compile(r"\b([A-Za-z0-9_]+)\s*(?:<[^>]*>)?\s*\(")
MENTIONS_BODY = re.compile(r"\bbody\b")
NOT_DELEGATES = {"BodyReader", "LogPacketBodyDebug", "WriteArray"}


def load_sources():
    out = {}
    for root in SOURCE_ROOTS:
        for dirpath, _, filenames in os.walk(REPO / root):
            if "/obj" in dirpath or "/bin" in dirpath:
                continue
            for name in filenames:
                if name.endswith(".cs"):
                    path = os.path.join(dirpath, name)
                    out[os.path.relpath(path, REPO)] = open(path, encoding="utf-8").read()
    return out


def index_methods(sources):
    """name -> [body text]. Brace-matched, so a delegated handler can be costed like a case block."""
    methods = collections.defaultdict(list)
    for text in sources.values():
        lines = text.split("\n")
        for i, line in enumerate(lines):
            m = METHOD_DEF.match(line)
            if not m or i + 1 >= len(lines) or lines[i + 1].strip() != "{":
                continue
            depth = 0
            body = []
            for j in range(i + 1, min(i + 400, len(lines))):
                depth += lines[j].count("{") - lines[j].count("}")
                body.append(lines[j])
                if depth == 0 and j > i + 1:
                    break
            methods[m.group(1)].append("\n".join(body))
    return methods


def strip_comments(text):
    """Drop `//` comments - this codebase documents wire layouts IN them, so scanning them finds phantom reads."""
    return "\n".join(re.sub(r"//.*$", "", line) for line in text.split("\n"))


def consumed(text, methods, depth=0, seen=None):
    """(fixed bytes, [variable reads]) for a block, following delegation by name up to 3 deep."""
    text = strip_comments(text)
    seen = seen if seen is not None else set()
    fixed, variable = 0, []
    for m in READ.finditer(text):
        op = m.group(1)
        if op in SIZES:
            fixed += SIZES[op]
        else:
            variable.append(op)
    if WHOLE_BODY.search(text):
        variable.append("ToArray")
    if depth < 3:
        for line in text.split("\n"):
            if not MENTIONS_BODY.search(line):
                continue
            for m in CALL.finditer(line):
                name = m.group(1)
                if name in NOT_DELEGATES or name in seen or name not in methods:
                    continue
                seen.add(name)
                for callee in methods[name]:
                    sub_fixed, sub_var = consumed(callee, methods, depth + 1, seen)
                    fixed += sub_fixed
                    variable += sub_var
    return fixed, variable


def opcode_index(sources):
    """(receiverClass, opcode) -> enum member name, resolved exactly the way the server routes.

    Matching on the METHOD NAME in the dump is wrong twice over: it is whatever `Opcodes.cs` was called when
    the dump was written (several have been renamed since - `EnterWorld` -> `LoginCharacter`,
    `RequestSmallCharList` -> `GetStartupData`), and the same 16-bit opcode means a DIFFERENT method on every
    interface. So resolve it the way `ProtocolCatalog` does: receiver class hash -> opcode enum -> member.
    """
    types = {name: int(value, 16) for name, value in ENDPOINT_CONST.findall(sources["Faolan.Core.Network/Constants/Endpoints.cs"])}
    enum_by_class = {}
    for endpoint, enum_name in CATALOG_ENTRY.findall(sources["Faolan.Core.Network/Protocol/ProtocolCatalog.cs"]):
        if endpoint in types:
            enum_by_class[types[endpoint]] = enum_name

    members = collections.defaultdict(dict)  # enum name -> {value: member}
    current = None
    for line in sources["Faolan.Core/Enums/Opcodes.cs"].split("\n"):
        declared = ENUM_DECL.search(line)
        if declared:
            current = declared.group(1)
            continue
        member = ENUM_MEMBER.match(line)
        if member and current:
            members[current].setdefault(int(member.group(2), 16), member.group(1))

    return {(cls, value): (name, enum_name)
            for cls, enum_name in enum_by_class.items()
            for value, name in members.get(enum_name, {}).items()}


def case_blocks(sources):
    blocks = collections.defaultdict(list)
    for path in LISTENERS:
        lines = sources[path].split("\n")
        current, start, buf = None, 0, []
        for i, line in enumerate(lines):
            m = CASE.search(line)
            if m:
                if current:
                    blocks[current].append((path, start, "\n".join(buf)))
                current, start, buf = (m.group(1), m.group(2)), i + 1, []
            elif current:
                buf.append(line)
        if current:
            blocks[current].append((path, start, "\n".join(buf)))
    return blocks


def captured_lengths():
    lengths = collections.defaultdict(collections.Counter)
    for path in sorted(glob.glob(str(captures_dir() / "*.tshark.json"))):
        for packet in json.load(open(path))["Packets"]:
            if packet.get("Direction") != "c2s" or not packet.get("Opcode") or not packet.get("ReceiverClass"):
                continue
            key = (int(packet["ReceiverClass"], 16), int(packet["Opcode"], 16), packet.get("Method") or packet["Opcode"])
            lengths[key][len(bytes.fromhex(packet.get("BodyHex") or ""))] += 1
    return lengths


def main():
    sources = load_sources()
    methods = index_methods(sources)
    blocks = case_blocks(sources)
    by_class = opcode_index(sources)
    lengths = captured_lengths()

    findings, skipped, unhandled = [], 0, []
    for receiver, opcode, method in sorted(lengths, key=lambda k: k[2]):
        counts = lengths[(receiver, opcode, method)]
        shortest, longest, seen = min(counts), max(counts), sum(counts.values())
        name, enum_name = by_class.get((receiver, opcode), (method, None))
        hits = blocks.get((enum_name, name), [])
        if not hits:
            unhandled.append((name, receiver, opcode, shortest, longest, seen))
            continue
        costed = [(*consumed(text, methods), path, line) for path, line, text in hits]
        # ANY variable-length read anywhere disqualifies the arithmetic - a wrong number is worse than silence.
        if any(variable for _, variable, _, _ in costed):
            skipped += 1
            continue
        fixed, _, path, line = max(costed, key=lambda c: c[0])
        if fixed < shortest:
            findings.append((shortest - fixed, method, shortest, longest, fixed, seen, path, line))

    print(f"{len(lengths)} c2s methods in the corpus, {skipped} skipped (variable-length reads), "
          f"{len(unhandled)} with no matching case block.\n")
    for gap, method, shortest, longest, fixed, seen, path, line in sorted(findings, reverse=True):
        print(f"{gap:5d}B unread  {method:32s} body {shortest}..{longest}B, reads {fixed}B  "
              f"n={seen}  [{path}:{line}]")
    print(f"\n{len(findings)} candidates - CONFIRM EACH by opening the handler (see the module docstring).")
    if unhandled:
        print("\nNo case block found (routed elsewhere, or genuinely unhandled):")
        for name, receiver, opcode, shortest, longest, seen in unhandled:
            print(f"        {name:32s} 0x{opcode:04x} on class 0x{receiver:08x}  body {shortest}..{longest}B  n={seen}")


if __name__ == "__main__":
    main()
