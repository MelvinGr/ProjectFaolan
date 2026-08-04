# `CharacterStats.flags` — the client's "Flags" stat (Gitea #648)

Raw RE artifact. What the client does with `ProtoCoreCharacterIIR.CharacterStats.flags` (protobuf field 16),
recovered statically from `AgeOfConan.exe`.

## What the wire says

`CharacterStats.flags` is a real bitfield, and **bit 28 is the "this is an NPC" bit** — measured over the whole
corpus slice, 17,400 NPC records all carry it and all 317 player records do not, while bit 0 is set on both.
That is gated by `NpcStatCoreColumnsTests.CharacterStatsFlags_Bit28_IsTheNpcBit`.

Two further bits appear only on players and only transiently: **3 (`0x8`)** and **5 (`0x20`)**. Seven characters
each appear with two different values, so they are STATE rather than identity, and the two never co-occur.

## The client's name for the word: stat `0x000` "Flags"

`CoreCharacter::SetFlags(uint)` = `FUN_006f12f0` is four lines and settles the whole question:

```c
uVar1 = in_EAX & 0xefffffff;                                  // clear bit 28
if (*(char *)(in_ECX + 0x1e8) != '\0') uVar1 |= 0x10000000;   // re-set it from the client's own NPC bool
*(uint *)(in_ECX + 0x374) = uVar1;                            // store on the CoreCharacter
(**(code **)(*(int *)(in_ECX + 0x10c) + 0xc))(0, uVar1);      // SetStat(0, flags)
```

So the wire word is pushed into the character under **stat id 0**, which LDB 615 names **"Flags"**
(`docs/re/ldb-stat-names.md`; `CharacterStatId.Flags`). It is stored at `CoreCharacter + 0x374`, and
**bit 28 is always recomputed locally** — the client clears whatever the server sent and re-derives it from its
own `CoreCharacter + 0x1e8` boolean. An independent confirmation of the bit's meaning, and a warning that the
server's bit 28 is advisory: the client does not trust it.

`CoreCharacterFullUpdateIIR::Activate` (`FUN_01458d40`) calls this twice — once inlined at `0x1458e8b`, once as a
real call at `0x1459c95` — both times with `stats().flags()`:

```asm
01458e8b  MOV EAX,dword ptr [EBX + 0x58]     ; this->m_msg.stats_
01458e8e  TEST EAX,EAX / JNZ
01458e92  MOV EAX,[0x0298088c]               ; ProtoCoreCharacterIIR::default_instance_
01458e97  MOV EAX,dword ptr [EAX + 0x14]     ;   ->stats_
01458e9a  MOV EAX,dword ptr [EAX + 0x44]     ; CharacterStats::flags_   <-- field 16
01458e9d  AND EAX,0xefffffff
01458ea2  CMP byte ptr [EDI + 0x1e8],0x0
01458eab  OR  EAX,0x10000000
01458eb3  MOV dword ptr [EDI + 0x374],EAX
```

### How the `+ 0x44` was pinned — the protobuf member layout

The generated messages in this client lay out as **`[vptr @ +0x00][one word @ +0x04][field N @ 0x04 + 4·N]
[… `_has_bits_[0]` after the last field]`**, and every accessor compiles to the
`ptr != NULL ? ptr : default_instance_->ptr` idiom, so a *pair* of offsets (`[this + X]` / `[default + Y]`) reads
straight off the disassembly and `X − Y` is the message's base offset inside its owner.

For `CoreCharacterFullUpdateIIR` that delta is **`0x44` on every pairing** (`0x58`/`0x14`, `0x5c`/`0x18`,
`0x78`/`0x34`, `0x94`/`0x50`), so `m_CoreCharacterMessage` sits at IIR + 0x44 and the message-relative offsets are
`0x14`, `0x18`, `0x34`, `0x50`. The `0x04 + 4·N` scheme is then forced, three ways:

- `_has_bits_[0]` is at message + 0xa0, and bit index = field number − 1: bit 4 guards `+0x18`, bit 11 guards
  `+0x34`, bit 13 guards `+0x3c`. Under `0x04 + 4·N` those are fields 5/12/14 = `npc_stats` / `offensive_target` /
  `team_id` — all sub-messages, which is what the pointer idiom requires. Under any one-slot shift, `+0x34` would
  be field 11 `character_kdtree`, a `uint32`, which cannot have a pointer accessor at all.
- `locked_to` (field 19, message + 0x50) is a `ProtoID32`, and the code reads *its* fields at `+0x08` / `+0x0c` —
  fields 1 and 2 under the same scheme. A shift would make those the header words.
- `firstname` / `lastname` (fields 2 and 3, message + 0x0c / + 0x10) are read with the MSVC `std::string` SSO
  idiom (`CMP [p+0x14],0x10 / JC / MOV EAX,[p]`), and they are the only adjacent string pair in the message.

`CharacterStats.flags` is field 16 → `stats() + 0x44`. ✔

⚠️ This also **retracts** the earlier reading of `this + 0x40` as the flags word (commit `14450786`). IIR + 0x40 is
the word *immediately before* the embedded message, i.e. a member of the IIR itself; the bits read there
(`>> 1` → stat `0xC2` "In Play", `>> 3` → stat `0xDF` "Can Change Clothes", `>> 0x1b` → stat `0x300` "Has Knubot
Data") are **not** `CharacterStats.flags` bits and say nothing about the wire word. Those three names are
withdrawn. The anomaly that flagged the reading as suspect — no captured NPC sets bit 27 although dialogue NPCs
plainly have knubot data — was the correct instinct.

## The bits, named from their consumers

Every access to `CoreCharacter + 0x374` in the binary (73 instructions touching `+ 0x374`, filtered to this class
by the setter/getter pair below):

| bit | mask | client code | reading |
|---|---|---|---|
| 3 | `0x8` | cleared by `FUN_0081dfa0` (TradeHolder reset — nulls the holder's item slots) and `FUN_0081f0e0` (trade finish — builds `KnubotFinishTradeIIR_c`) | **a trade session is open** |
| 5 | `0x20` | cleared by `FUN_006f21f0`, which logs `"OnTeleportFailed %u:%u [%s] wanted to go to: %s\n"` | **a teleport / zone transfer is in flight** |
| 6 | `0x40` | cleared by `FUN_007ae3e0` (state `== 3`), which then calls vtable slot `0xc8` with `0x40` | not named |
| 22 | `0x400000` | `TEST dword ptr [EDI + 0x374],0x400000` at `0x1459cdd` gates copying `firstname` (IIR + 0x50) and `lastname` (IIR + 0x54) onto `CoreCharacter + 0x23c` / `+ 0x258` | **the first/last name fields are populated** |
| 28 | `0x10000000` | recomputed locally in `SetFlags` from `CoreCharacter + 0x1e8` | **NPC** (already gated) |
| 29 | `0x20000000` | cleared in `FUN_011086a0`, the player-character construction (`ClientHolder` / `PlayerTagHolder` / `QuestHolder` / …) | not named |

Bits 3 and 5 fit their corpus evidence exactly: transient, players only, mutually exclusive (you cannot be
mid-teleport and mid-trade), and NPCs have neither. Almost every non-`0x1` sighting is in one busy-city capture,
which is where trades and zone-ins happen.

**No bit of this word is ever SET by the client** — the only `OR` into `+ 0x374` is the generic
`CoreCharacter::SetFlagBits(mask)` (`FUN_006ea720`, `flags |= mask; SetStat(0, flags)`), whose companion is
`CoreCharacter::HasFlag(mask)` (`FUN_006ea750`, `(flags & mask) != 0`); both are virtual and have no direct
callers. Every literal site clears. So the word is **server-owned state pushed down**, which the client only ever
turns off locally when it observes the state ending.

## Consequences for the server

None that change bytes. We do not compose this word — an NPC spawn carries the harvested `NpcStatCores.Flags`,
and bit 28 is the only bit we assert anything about. Bits 3 and 5 would only ever be emitted by a server that
models trade sessions and teleports, and retail is authoritative about when that happens, so nothing is invented
here.

## Also recovered along the way

`NpcStats.community` (field 6, `npc_stats() + 0x1c`) is pushed under stat `0x1C7` (455) — LDB "Monster Type".

## Method notes

- Do not const-scan for a bit MASK: the client compiles these as C++ bitfields (`>> n & 1`), and the 249 hits for
  `0x10000000` are all the statically-linked MSVC CRT's `printf` format flags.
- Ghidra's decompiler mis-attributed the base register at several of these sites, rendering both `[EBX + 0x40]`
  and `[EDI + 0x374]` as `*(uint *)(in_ECX + 0x40)`. **Check the disassembly before naming a bit** — the whole
  retracted reading above came from trusting the decompiled form.
