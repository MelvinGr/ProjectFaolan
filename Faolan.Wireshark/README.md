# Faolan.Wireshark

A native Wireshark/tshark dissector plugin for the Age of Conan TCP services (UniverseAgent,
PlayerAgent, CSPlayerAgent, GameServer). Ground-truth captures are readable directly in
Wireshark without the JSON dump + Python dissector pipeline.

## What it does

- **Conan packet framing** on every connection: `[u32 BE length][u32 crc32][u32 BE hdrlen]
[RpcHeader][body]`, reassembled across TCP segments per direction.
- **In-band compression transitions**: at a packet boundary a peer may emit a 9-byte header
  `[80 00 00 05][4 bytes][flag]`. Flag `00` switches the rest of that direction to one
  continuous zlib deflate stream (framing continues on the inflated bytes); flag `01` leaves
  it plaintext. Observed: GameServer s2c transitions at offset 0 with flag 00; the c2s side
  transitions mid-stream with flag 01.
  _(Note: the C# `ConanMessageFramer` only detects the marker at receive-chunk starts; this
  plugin handles it at any packet boundary.)_
- **RpcHeader dissection**: sender/receiver `ProtoEndpoint`s (class id resolved to the 14
  recovered client class names - BKDR-31 hashes, see `docs/re/aoc-opcode-ids.md` - plus the
  instance), the 16-bit method opcode, trailing header bytes, body. The two
  endpoints are dispatched by their protobuf tag (sender = field 1 / `0x0A`, receiver =
  field 2 / `0x12`) in **either order** - server->client replies swap them (receiver-first),
  so a fixed sender-then-receiver assumption misframed those (the old CSPlayerAgent
  `op=0x0a0a` bug, #108).
- **Method names**: the (receiver class, opcode) pair resolves to its recovered method name
  (`faolan.pkt.method`, e.g. `GameCharAgent TryLootItem`) via tables **generated at build
  time from the server's curated C# catalogs** (`Faolan.Core.Network/Constants/Opcodes.cs`,
  `scripts/gen_opcode_names.py`) - the plugin never drifts from what the server calls a
  method. PlayerAgent/PlayerInterface (32-bit hash dispatch, not `0x2000 + index`) only
  resolve their behavior-verified subset; everything else falls back to raw hex.
- **IIR type resolution**: GameAgent/GameInterface `0x2000` (PassBlob) bodies are the IIR
  carrier `[u32 len][u32 IIR type id][record]`; the id (BKDR-31 hash of the client IIR
  class name) is dissected as `faolan.pkt.iir` and resolved against the catalog in
  `Opcodes.cs` (`GameServer0X2000Opcodes`), so COL_INFO reads e.g.
  `GameInterface DispatchPassBlob:MoveTaskIIR`.
- **Direction**: `faolan.pkt.direction` = `c2s`/`s2c` from the receiver class suffix
  (*Agent classes receive c2s calls, *Interface classes s2c dispatches); chat packets use
  the conversation initiator instead (`faolan.chat.direction`).
- **CRC verification**: the packet crc is a plain CRC-32 (zlib `crc32()`) over everything
  after the 8-byte length+crc prefix; mismatches get expert warnings.
- **Auto-follow**: service handoffs advertise the next service's address (ASCII `host:port`
  or the binary `[u32 ip][u16 port]` form in the 0x1A07 handoffs). Advertised ports are
  registered automatically, so a whole session dissects from just the fixed UniverseAgent
  port (7010, the default binding).
- **AgentServer chat**: connections that fail Conan validation (first-packet CRC) but walk
  cleanly as `[u16 opcode][u16 bodyLen][body]` framing are re-framed from their first byte
  and dissected as chat packets (`faolan.chat.opcode` / `len` / `body`, raw values).
- **Robust delivery**: per-direction TCP sequence tracking deduplicates retransmissions and
  reorders out-of-order segments. If a gap never fills (bytes missing from the capture), a
  plaintext direction resyncs at the next segment; a compressed direction keeps inflating
  across the hole - downstream packets usually survive and damaged ones are flagged by CRC.

## Build and install

```zsh
cmake -S Faolan.Wireshark -B Faolan.Wireshark/bin
cmake --build Faolan.Wireshark/bin -j
cmake --build Faolan.Wireshark/bin --target install-faolan-plugin   # -> ~/.local/lib/wireshark/plugins/<ver>/epan
tshark -G plugins | grep -i faolan    # verify
```

Standalone stream/framer tests (no Wireshark needed):

```zsh
./Faolan.Wireshark/bin/faolan_stream_tests
```

## Usage

Open any AoC capture - the UniverseAgent port (7010) is bound by default and the rest of the
session is followed automatically. Filter with `faolan`.

**Recommended for captures with packet loss/retransmissions** (most real-internet captures):

```zsh
tshark -r capture.pcapng -o tcp.analyze_sequence_numbers:FALSE ...
```

Without it, Wireshark suppresses subdissector calls on frames it classifies as
retransmissions, so a late retransmission that fills a gap never reaches the plugin and the
compressed stream stalls at the hole. (Do NOT use `tcp.reassemble_out_of_order` - it withholds
all data after a genuinely lost segment forever.) In the Wireshark UI set the same option under
Edit > Preferences > Protocols > TCP, or bind extra ports via Decode As / the `faolan.tcp_ports`
preference.

Useful fields:

| field                                                             | meaning                                                          |
| ----------------------------------------------------------------- | ---------------------------------------------------------------- |
| `faolan.pkt.opcode`                                               | 16-bit method opcode (method index 0x2000+position)              |
| `faolan.pkt.method`                                               | method name for (receiver class, opcode), generated from Opcodes.cs |
| `faolan.pkt.iir`                                                  | IIR type id in a PassBlob 0x2000 body, resolved to the IIR class name |
| `faolan.pkt.direction`                                            | `c2s`/`s2c` (receiver class Agent/Interface suffix)              |
| `faolan.pkt.sender_class` / `receiver_class`                      | ProtoEndpoint class id, resolved to class name                   |
| `faolan.pkt.sender_instance` / `receiver_instance`                | character/zone instance                                          |
| `faolan.pkt.len` / `crc` / `header_len` / `header_extra` / `body` | packet layout fields                                             |
| `faolan.chat.opcode` / `method` / `direction` / `len` / `body`    | AgentServer chat packets (names per direction)                   |
| `faolan.compression_state`                                        | Plain / TransitionHeader / Compressed / Failed / Chat / Skipped  |
| `faolan.stream_note`                                              | retransmission / out-of-order / capture-gap diagnostics          |
| `faolan.decompressed`                                             | inflated bytes produced in this frame (own data source)          |

Packets completed in a frame appear under the "Faolan Conan packets" data source; the COL_INFO
column shows `ReceiverClass Method[:IirType]` per packet (raw `0xOPCODE` when unresolved), e.g.
`GameInterface DispatchPassBlob:MoveTaskIIR, GameCharAgent TryLootItem`.

Example - everything the client asked the GameCharAgent:

```zsh
tshark -r capture.pcapng -o tcp.analyze_sequence_numbers:FALSE \
  -Y 'faolan.pkt.receiver_class == 0xa9ecb95d' \
  -T fields -e frame.number -e faolan.pkt.opcode -e faolan.pkt.method
```

Or every MoveTask the server streamed: `-Y 'faolan.pkt.iir == 0x96c46740'`.

## JSON export (analyzer-style dumps)

`Captures/tshark_dump.sh` converts a capture into an analyzer-style dump - one record per
Faolan/chat packet with frame/time/endpoints/direction/opcode/classes/body:

```zsh
Captures/tshark_dump.sh <capture.pcapng> [output.json]   # default output: <capture>.tshark.json
```

Under the hood: `tshark -T json` emits the full dissection tree (one object per frame; each
"Conan packet N" subtree is self-contained) and `Captures/scripts/tshark_to_dump.py` re-groups it:

```zsh
tshark -r capture.pcapng -o tcp.analyze_sequence_numbers:FALSE -T json \
  | Captures/scripts/tshark_to_dump.py > capture.tshark.json
```

The reshape pass is python3 (was a jq filter; #106) - same dump, but ~10x faster on large
captures (131 MB tshark JSON: ~11.7 s jq -> ~1.1 s) and no jq dependency. Not schema-identical
to the `Faolan.PacketAnalyzer` dumps, but carries the same essentials and additionally includes
the compressed-stream packets the analyzer drops. Regenerated dumps also carry the plugin's
resolved `Method` / `Iir` / `Direction` as additive keys (absent in older committed dumps;
all pre-existing keys stay byte-identical).

## Validation

Byte-exact parity with the `Faolan.PacketAnalyzer` JSON dumps (per-opcode packet counts) on:

- `Strie_rogue-assassin_login.pcapng` (305/305, + post-capture-hole recovery
  the analyzer can't do)
- `Hoxxling_mage-heraldofxotli_login-guild_invite_accepted-...pcapng` (6520/6520,
  incl. both GameServer connections of the zone change; chat opcode counts 94/94 + one
  body-less `[op][len 0]` packet per session the C# stack drops as invalid)
- `account-faolan-character-necrol80-login-sell_items_to_trader-...pcapng` (3765/3765)
- `Strie_rogue-assassin_create.pcapng` (673/673)

## Layout

```text
src/faolan_conan_framer.{c,h}   packet framer ([u32 len] frames, transition-marker scan)
src/faolan_conan_stream.{c,h}   per-direction stream: phases, transition header, zlib inflate
src/faolan_stream_tests.c       standalone test harness for the two modules
scripts/gen_opcode_names.py     Opcodes.cs -> value_string tables (build-time, via CMake)
wireshark/packet-faolan-gameserver-zlib.c   the dissector (fields, seq tracking, auto-follow)
wireshark/plugin.c              plugin registration
```

## Known gaps / future work

- Per-opcode body grammars are deliberately NOT dissected (beyond the IIR carrier prefix):
  decoding belongs to the proven pipeline (`Captures/decode_*.py` -> golden test -> C# codec);
  the plugin's job is ground-truth framing plus enough metadata to find and filter packets.
- A capture hole inside a compressed stream cannot be fully recovered (the deflate stream has
  no flush points); the plugin decompresses across the hole on a best-effort basis.

