# 02 — RPC layer

The `RpcHeader` that [`00-transport.md`](00-transport.md) skipped, and how one TCP connection multiplexes several
logical RPC interfaces.

---

## RpcHeader

Immediately after the packet's `[length][crc32][hdrlen]` head, and inside `hdrlen` bytes:

```
[0x0A][byte len][sender ProtoEndpoint][0x12][byte len][receiver ProtoEndpoint][u16 BE opcode][optional headerData]
```

`0x0A` and `0x12` are protobuf field tags (fields 1 and 2, length-delimited). The optional `headerData` after the
opcode is **not a generic grammar** — across every capture it is exactly four per-opcode / constant cases
(`tools/capture/decode_rpc_headerdata.py` tallies them: 7366 / 1425 / 340 / 89 packets):

| bytes | what | notes |
|---|---|---|
| 1 (`0x01`) | latency marker | constant; rides the ping/pong (`0x2085` c2s / `0x2082` `DispatchPong` s2c) and a few CSPlayerInterface dispatches — the vast majority of packets. |
| 4 | PlayerAgent/PlayerInterface **32-bit method id** | present ONLY here (empty for all 16 other interfaces); keyed by (opcode, direction), not opcode alone — two 16-bit indices are reused by a c2s method and its s2c twin with different ids. See #320 / [`../re/aoc-rpc-method-opcodes.md`](../re/aoc-rpc-method-opcodes.md). |
| 3 (`0x94A760`) | PlayerInterface `SmallCharList` (`0x20A5`) | constant. |
| 13 | PlayerInterface `OverrideGameTweaks` (`0x1A07`) | a **protobuf endpoint reference**, not an opaque block: `0x0D <fixed32 class id 0x7D155738 = PlayerInterface> 0x10 <varint 1> 0x20 <varint per-session instance id>` — consumes all 13 bytes. |

So it is the PlayerInterface method id (decoded) plus a constant `0x01` marker plus two PlayerInterface specials —
not a mystery appended to arbitrary packets. [V1: tools/capture/decode_rpc_headerdata.py]

(The `0x01` marker is always exactly `0x01` in every capture; only its *semantic* — a flag? a version? — is unproven,
the sole micro-residual on #319. The 13-byte endpoint above is decoded.)

## ProtoEndpoint

Both sender and receiver are a `ProtoEndpoint` — the addressing primitive of the whole protocol:

```
[0x0D][class id: fixed32 LE][0x10][instance: varint]
```

- **class id** identifies the RPC interface (which set of methods). It is the BKDR-31 hash of the client's
  agent/interface class name — the same hash family as the IIR sub-opcodes ([`03-iir-carrier.md`](03-iir-carrier.md)).
  [V3: ProtocolDocsTests.RpcClassIds_AreBkdr31OfTheClassName]
- **instance** identifies *which* object of that class — a character id, a zone id, etc.

Encoding note: `0x0D`/`0x10` are protobuf fixed32 and varint tags, so the class id is **little-endian** here even
though the rest of the protocol is big-endian (see [`04-encoding.md`](04-encoding.md)). The typed
`WriteHeader(ProtoEndpoint, …)` overload serializes byte-identically to the hand-rolled arrays it replaced.
[V3: ProtoEndpointGoldenTests.Endpoint_SerializesToHandRolledHeaderBytes]

## Opcodes are method indices

For every interface except one, the 16-bit opcode is a **method index**:

```
wire opcode = 0x2000 + registration index
```

The index is the method's position in the client's per-class RPC registration. Both directions were recovered
from the client: c2s order is the registration order; s2c "Dispatch" opcodes are the per-class dispatcher switch
indices. [V1: tools/ghidra/DumpRpcTables.java]

The full per-interface catalog — 819 entries across 16 enums, with direction and class id — is **generated** from
`Opcodes.cs`, not written here: [`gen/opcodes.md`](gen/opcodes.md).

> A named opcode is **not** a decoded message. `gen/opcodes.md` enumerates every opcode (V1); whether its body is
> understood is a separate question answered by [`gen/coverage.md`](gen/coverage.md).

### The one exception

PlayerAgent/PlayerInterface additionally carries a **32-bit method hash** on the wire (alongside the 16-bit
opcode), not the `0x2000 + index` scheme alone. It is a dump task, not a hash to reverse: the login-critical
method set is recovered and verified end-to-end (a Frida-under-Wine hook on the client send path correlated with
a loopback capture) and carried as real associated ids (`PlayerAgentMethodIds` — 9 c2s methods incl. character
create/delete, appended by `WriteHeader`). Only the methods never yet observed on the wire (character rename,
Facebook-token save, hardcore, offline-levels, …) still lack their (opcode, hash) pair, harvestable by the same
technique. [V0: PlayerAgent 32-bit hash dispatch]

## Multiplexing

One GameServer TCP connection carries roughly four RPC class-pairs at once (e.g. GameCharAgent ↔
GameCharInterface, plus project/tradepost variants). A packet is routed by the pair `(receiver class id, opcode)`,
never by the connection. This is why the same opcode number means different things on different class ids, and why
cross-listener actions go through dispatcher seams rather than direct references. [V1: tools/ghidra/DumpDispatch.java]

Direction convention: `*Agent` classes receive client→server calls; `*Interface` classes receive server→client
dispatches.

## Reading a c2s body from the client's own signature

The client logs its proxy method **signatures** — name, parameter names and C++ types — and those strings are
recovered in [`../re/aoc-client-rpc-methods.json`](../re/aoc-client-rpc-methods.json) (376 client→server methods
across 14 interfaces). Combined with the `wire = 0x2000 + index` mapping above, that turns a client→server body
from "some u32s" into a typed record: look the opcode's index up in the interface's method list, and the params
are the fields in wire order. The C++ types map onto the encodings in [`04-encoding.md`](04-encoding.md):

| C++ in the signature | on the wire |
|---|---|
| `uint32` / `int` / `unsigned int` / `ID32::InstanceType` | 4 bytes BE |
| `uint16` | 2 bytes BE |
| `bool` | 1 byte |
| `std::string` / `String` | `[u16 length][LATIN1 bytes]` (§04) |
| `const ID32&` | **two** words: `[u32 class][u32 instance]` (§05) |
| a map / vector (`StrStrMap`, `CharacterCheckSumMap`, …) | a `ListFrame` count word, then that many entries |
| `BinaryStream&` | `[u32 byte length][bytes]` |

The whole login chain reads out of it directly, and each body below is byte-identical across every capture that
contains it:

| method | body |
|---|---|
| `UniverseAgent.InitiateAuthentication(username, nGameID)` | `[string username][u32 nGameID]` — `nGameID` is **1** in all 116 captured logins (3 accounts) [V2: Barbarian-login.tshark.json frame 4] |
| `PlayerAgent.GetStartupData(md5)` | one `[u16][LATIN1]` string, the md5 of the client's CACHED startup data (empty on a first login, a 32-char hex digest once a cache exists) — not the bare u16 it looks like [V2: Barbarian-login.tshark.json frame 22] |
| `PlayerAgent.FinishGetStartupData(characterMap)` | `[ListFrame count]` then per entry `[u32 characterId][string md5]` — the same checksum, per character [V2: Barbarian-login.tshark.json frame 24] |
| `PlayerAgent.LoginCharacter(nCharInstance, dataMap, cClientLanguage, regionCode, ageCode, genderCode, igrID, userType, serviceType)` | `[u32 charId][ListFrame 0][string "en"][u16 0][u16 0][u16 0][string "_"][u32 0][u32 0]` — everything after the id is a constant the retail client always sends: empty dataMap, UI language, zeroed age-gate fields, the `"_"` placeholder IGR (internet-gaming-room) id, `userType`/`serviceType` 0 [V2: Barbarian-login.tshark.json frame 33] |
| `CSPlayerAgent.Authenticate(nCharacterInstance, nCookie, communityVersion)` | `[u32 charInstance][u32 cookie][u32 1]` — `cookie` is the `uCookie` from `UniverseInterface.DispatchAckAuthenticate` [V2: Barbarian-login.tshark.json frame 95] |
| ChatServer `Hello` | `[u32 1][u32 charInstance][u32 cookie][string "English"]` — the same instance+cookie pair as the CSPlayerAgent `Authenticate`, byte for byte [V2: Barbarian-login.tshark.json frame 94] |

And the in-world ones that had unnamed reads:

| method | body |
|---|---|
| `GameAgent.ChatVicinity(cSenderID, nMode, cMsg)` | `[ID32 sender][u32 mode][u32 msgLen][string text][u8 0]` — the sender ID32 is usually `{0,0}` (the client leaves it for the server), the `BinaryStream` length counts the string's own u16 plus its bytes plus the trailing zero [V2: Barbarian-login.tshark.json frame 138] |
| `GameCharAgent.CinematicTransition(i_cClientId, i_bEntering)` | `[ID32 {0xC350, handle}][u8 entering]` — 9 bytes in all 402 captured packets [V2: Barbarian-login.tshark.json frame 71] |
| `GameCharAgent.SplitInventoryItem(i_cInventory, i_nSrcPos, i_nSplitSize, i_nDstPos)` | `[ID32 {container kind, owner handle}][u32 src][u32 size][u32 dst]` — the leading ID32 is the CONTAINER, `0x68` = the backpack, i.e. the same value as `CharacterInventory.Container` [V2: DemoL80_mage-demonologist_login_randomActions.tshark.json frame 704] |
| `GameCharAgent.AddShortcut(i_eType, i_nPosition, i_nData, i_nInventoryType)` | `[u32 type][u32 position][u32 data][u32 inventoryType]` — the trailing word is 0 in all 192 captured AddShortcuts [V2: Barbarian-create.tshark.json frame 235] |

The same reading applies to `0x2000` IIR bodies once the carrier (§03) is stripped: the client→server `QuestIIR`
(`0x97E02314`, the client's `DeleteQuest`) is byte-for-byte the record the server sends back down —
`[u32 class][u32 handle][u32 0][u8 0x01][u32 state][u32 questId][u32 0]` [V3: QuestTests.BuildQuestIir_MatchesCapture].

### Auditing it: bodies we do not read to the end

An unread tail is an undecoded field, not a harmless remainder. `tools/capture/audit_unread_bodies.py`
cross-references two things that never had to agree — the captured c2s body length per (receiver class,
opcode), and how many bytes the matching `case` block consumes — and reports every handler that provably
reads fewer bytes than the shortest captured body. It resolves the opcode the way `ProtocolCatalog` does
(receiver-class hash → opcode enum → member) rather than by method name, because the `Method` in a tshark
dump is whatever `Opcodes.cs` was called when the dump was written.

It is a lead generator, not a verdict: it follows one level of delegation by name, and skips any handler that
reaches a variable-length read rather than guessing. Confirm each hit by opening the handler, then look the
method up in the signature catalog above rather than inventing a grammar. The first pass found nine real
cases; a clean run reports zero. [V1: tools/capture/audit_unread_bodies.py]

## GameServerStats: what retail's server frame rate actually was

`Ping` (c2s `0x2005`, empty body) is answered with `GameServerStats` (`0x2009`). The client signature is
`DispatchGameServerStats(nGSFrameRate, uGSFrameCount, nGSCpuUsage)` → body = `[f32 BE][u32][u32]`, and the client
renders the first field as **"Server Framerate"** in its developer overlay.

Across the **4243** s2c `GameServerStats` in the corpus (107 captures):

| | value |
|---|---|
| ceiling | **99.34039 Hz** = 10.0664 ms per frame [V2: DemoL80_mage-demonologist_login_randomActions-very_long_loading_screen_after_field_change.tshark.json frame 2920] |
| median | 98.94 Hz |
| share above 99.0 Hz | 45.8% |
| floor | **0.1315 Hz** [V2: DemoL80_mage-demonologist_login_randomActions-very_long_loading_screen_after_field_change.tshark.json frame 5043] |

So retail's game server ran a **~100 Hz / 10 ms frame loop**. The value never once exceeds 99.34, and sits pinned
just under it for most of every session — that is a nominal 100 Hz achieving ~10.07 ms in practice, not a
free-running rate. Its interest is the *degradation*: the ceiling and the floor above come from the SAME capture,
whose name records what happened in between (a very long loading screen after a field change) — the stall is
visible in the metric, three orders of magnitude down.

**`uGSFrameCount` and `nGSCpuUsage` are zero in all 4243 packets.** Retail declared them and never populated them,
so `0` is the proven-safe value and we send `0` for both.

Our own reply reports the **measured** rate of our simulation loop (`TickMetrics` → `KeepAliveService`), not a
constant: a healthy reading is ~10.0 against our nominal 10 Hz base tick, and a stalled or dead world loop reads
0 rather than a plausible-looking number. The reading that matters is against its own nominal — ours is an order
of magnitude under retail's because our base tick is a coarse gameplay tick, not a server frame.
[V3: TickMetricsTests.AfterAFullWindow_ReportsTheAchievedRateAndBusyFraction]
[V3: KeepAliveRpcPilotTests.HandlePing_ReportsTheMeasuredTickRate]
[V3: KeepAliveRpcPilotTests.HandlePing_ReportsZeroWhenTheWorldLoopIsNotTicking]

## See also

- [`03-iir-carrier.md`](03-iir-carrier.md) — opcode `0x2000` is itself a carrier for a second, 32-bit-hashed
  opcode space.
- [`gen/opcodes.md`](gen/opcodes.md) — the generated catalog.
- [`gen/coverage.md`](gen/coverage.md) — which opcodes have a decoded body.
