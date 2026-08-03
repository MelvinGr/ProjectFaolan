# 02 — RPC layer

The `RpcHeader` that [`00-transport.md`](00-transport.md) skipped, and how one TCP connection multiplexes several
logical RPC interfaces.

---

## RpcHeader

Immediately after the packet's `[length][crc32][hdrlen]` head, and inside `hdrlen` bytes:

```
[0x0A][byte len][sender ProtoEndpoint][0x12][byte len][receiver ProtoEndpoint][0x20][method: varint]
```

Every byte of it is protobuf. `0x0A` and `0x12` are field tags 1 and 2, length-delimited; `0x20` is field tag 4,
varint, and its value is the method's **32-bit registration constant, whole**
[V3: RpcHeaderTests.TheOpcodeIsTheMethodVarint].

The client's own schema (`Faolan.Core/Protobuf/aoc-client-protos.proto`, extracted from the client) names all five
fields, and `Rpc` is written field-for-field against it:

| # | tag | schema | `Rpc` |
|---|-----|--------|-------|
| 1 | `0x0A` | `required ProtoEndpoint caller_id` | `Sender` |
| 2 | `0x12` | `required ProtoEndpoint endpoint_id` | `Receiver` |
| 3 | `0x1A` | `repeated ProtoEndpoint full_routing_context` | `RoutingContext` |
| 4 | `0x20` | `required uint32 method_instance` | `Method` |
| 5 | `0x2A` | `repeated ProtoEndpoint routing_destination` | — (no captured frame carries it) |

### What used to be read as `[u16 opcode][headerData]`

The two fields above were read for a long time as a big-endian `u16` opcode followed by an optional trailing
`headerData` blob, because for most methods that split lands in exactly the same place:

| bytes on the wire | read as | actually |
|---|---|---|
| `20 09` | opcode `0x2009`, no headerData | field 4, varint `9` — method 9 |
| `20 85 01` | opcode `0x2085`, headerData `0x01` | field 4, varint `133` — method 133 |
| `20 83 96 CB E5 09` | opcode `0x2083`, headerData `96 CB E5 09` | field 4, varint `0x9CB2CB03` — `PlayerAgent.Authenticate` |

A varint's first group is `constant & 0x7F`, with the continuation bit set when more groups follow. So the "opcode"
is `0x2000 | (constant & 0x7F)` for a one-group value and `0x2080 | (constant & 0x7F)` for a longer one, and the
"headerData" is the remaining groups. That is the whole of it, and it explains every oddity the old reading had to
list as a special case:

- **`0x01` is not a marker, a flag or a latency bit.** It is the second group of a two-group varint, which is why it
  is *always* exactly `0x01` (12,056 sightings, never `0x00`, never `0x02`) and why it is on **100%** of the packets
  of any method that has it (5,917/5,917 pings, 5,689/5,689 pongs) — it belongs to the METHOD, not the packet.
- **`0x2080` is the boundary** because that is where a method constant needs a second varint group. Method 127 fits
  in one; method 128 does not.
- **The last byte of a 4-byte tail is always small (`0x02`–`0x0F`)** because it is the varint's *top* group, not the
  low byte of a hash.
- **`0x94A760` is not a constant.** It is `PlayerInterface.SendCharacterSettingsCheckSumMap`, three groups long
  because a varint is as long as its value needs.

Measured over the whole capture corpus (120 dumps, 419,300 frames): **404,208 of 404,210** field-4 frames carry a
well-formed varint whose first group is the opcode's low byte, and the presence of a tail agrees with the
continuation bit in **every** one.

The two misses are **not counter-examples, because neither is a packet.** Both sit inside a desynced decompressed
region of one capture (`Strie_rogue-assassin_login`) and fail an independent CRC recompute under *every* alignment,
while a normal frame from the same stream matches exactly; the surrounding buffer carries `DD DD DD` filler and a
header length of `0xDDDDDDF2`. Their `0x20A8`/`0x20A9` opcodes are coincidence
[V3: RpcHeaderTests.TheContinuationGroupAppearsAtMethod128].

### Field 3: `full_routing_context`

472 frames carry an extra field *before* the method — tag `0x1A`, field 3, length-delimited, holding one more
`ProtoEndpoint`:

```
[0x0A sender][0x12 receiver][0x1A][len 7][0x0D 0x7D155738 0x10 0x01][0x20][method: varint]
```

These are the four login handoffs, and the old reading called all four "opcode `0x1A07` = `OverrideGameTweaks`
with an opaque 13-byte session blob" — `0x1A07` being the field-3 tag plus its length byte, and the "blob" being
the endpoint plus the method varint. Decoded, the four are `ChatServerConnectReady`, `CSServerConnectReady`,
`LoginConnectReady` and `OverrideGameTweaks` (118 sightings each) — exactly the four the login handoff sends, in
the order it sends them [V3: RpcHeaderTests.TheLoginHandoffs_ReproduceTheCapturedHeaders]. They address the
ServiceConnection, so the routing context restates the `PlayerInterface` object the method fires on — instance 1
in every captured frame.

### The implementation

`Rpc` (`Faolan.Core.Network/Protocol/Rpc.cs`) carries `Method` and writes it whole; there is no `Opcode` field and
no header tail, so nothing can supply one and nothing has to remember to
[V3: RpcHeaderTests.ASendCarriesTheWholeMethodVarint]. `Rpc.Opcode` survives as a derived **view** — the tag byte
plus the varint's first group, which is what you read off a hex dump — because the opcode enums and the dissector
are written that way, and because on the index-registered classes it is the method index exactly, so `0x2000 + index`
is a faithful rendering of the wire rather than a fiction.

⚠️ **On the hash-registered pair there is no opcode, and asking for one throws** rather than returning 7 of the
constant's 32 bits [V3: RpcHeaderTests.AHashRegisteredMethod_HasNoOpcode]. There is no `PlayerAgentOpcodes` /
`PlayerInterfaceOpcodes` enum either — a value that names two methods cannot be a catalog key, which is exactly how
an opcode-keyed dispatch ran the wrong handler (#608). Dispatch, the packet trace, the timeline report and the
generated docs all read `Method` there; `PlayerAgentMethodIds.ObservedOpcode` computes the dump reading when a
human needs to find one of these in a capture (#613). Callers that can see either kind gate on `Rpc.HasOpcode`.

The header is read **strictly**: an unknown tag, a repeated singular field or a missing required one throws
`InvalidDataException` at the frame boundary, which the receive loop logs against the peer and disconnects. That is
affordable only because `Method` holds the whole identity — re-serializing a parsed frame reproduces it byte for
byte, which is what the old opaque tail was buying
[V3: RpcHeaderTests.EveryCapturedFrame_ParsesStrictlyAndReSerializes]. `Rpc.cs` records why the header is written
by hand rather than through the generated `RpcHeader` contract.

⚠️ The asymmetry is worth knowing when reading pre-#610 sends: an **extra** tail byte was harmless (the varint ends
at the first group without a continuation bit, and the stray byte is inside the header's own length, so the body is
unaffected), while a **missing** one truncates a varint mid-continuation. That is why a spurious `0x01` on the
trade replies went unnoticed for so long, and why omitting one on a ping would not have. Both are now unrepresentable
— and both are rejected on the way in.

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

PlayerAgent/PlayerInterface registers its methods with a 32-bit HASH rather than a small index, so the same varint
field carries a 4–5 group value and the `0x2000 + index` reading has nothing to hold on to. Nothing else about the
header differs.

⚠️ **The 16-bit "opcode" is not a method key on this interface** — but not because it is per-connection, which is
what the wire looks like until the varint is reassembled. It is `0x2080 | (constant & 0x7F)`, so two methods whose
constants share their low 7 bits share an opcode: `0x20EC` is `ReceivedCharacterSettings` (`0x3C7C926C`) *and*
`AuthenticateLoggedIn` (`0x9D991E6C`); `0x20D6` is `VerifyLanguageSetting` (`0x9847AED6`) *and*
`UpdateCharacterPreview` (`0x4D661656`). Both pairs appear in the corpus. A dispatcher keyed on the opcode alone
therefore cannot tell them apart — it has to read the whole varint (Gitea #610).

**The constants are DERIVED, not tabulated.** None of the wire values appears anywhere in `AgeOfConan.exe`, which is
what made "dump the table" a dead end: the wire carries the registration constant varint-encoded, and the varint's
groups are not a substring of the constant. Reassembling the varint reproduces **all 28** observed methods, 14 c2s
and 14 s2c, with no exception. Both registration tables (25 c2s + 26 s2c methods, the complete interface) are in
`PlayerAgentMethodIds` [V1: Faolan.Core.Network/Constants/PlayerAgentMethodIds.cs].

Recovering the constants also gave the interface its real NAMES, replacing nine invented ones — `InitAuth` is
`AuthenticateAck`, `SendRealmList` is `SetDimensionList`, `SmallCharList` is `SendCharacterSettingsCheckSumMap`, and
the opcode we called `CreateCharacter` is `CreateCharacterWithData`. See
[`../re/aoc-rpc-method-opcodes.md`](../re/aoc-rpc-method-opcodes.md).

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
| `GameCharInterface.DispatchPong(i_nSequence, i_nServerTime)` | `[u32 sequence echoed][u32 serverTime HIGH][u32 serverTime LOW]` — the last two words are ONE **64-bit microsecond** counter, high word first, and its value is the server's **uptime**: `(hi << 32 | lo)` advances at exactly wall-clock rate across every captured pong (182/186 and 23/23 in the two NecroL80 sessions), giving 2.60 and 4.73 days. The client's own signature has two args, not three, which is why. ⚠️ The high word therefore looks like a per-session constant inside any one capture (it only ticks once per 71.6 min) and takes 83 different values across the corpus — reading it as a constant is the trap. The client sends `LatencyCheck` (`0x2085`, a bare `[u32 counter]`) every ~5s and retail answers in ~117ms [V2: NecroL80_mage-necromancer_login_actions-described-in-chat.tshark.json] |

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
