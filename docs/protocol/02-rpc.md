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
(`Captures/scripts/decode_rpc_headerdata.py` tallies them: 7366 / 1425 / 340 / 89 packets):

| bytes | what | notes |
|---|---|---|
| 1 (`0x01`) | latency marker | constant; rides the ping/pong (`0x2085` c2s / `0x2082` `DispatchPong` s2c) and a few CSPlayerInterface dispatches — the vast majority of packets. |
| 4 | PlayerAgent/PlayerInterface **32-bit method id** | present ONLY here (empty for all 16 other interfaces); keyed by (opcode, direction), not opcode alone — two 16-bit indices are reused by a c2s method and its s2c twin with different ids. See #320 / [`../re/aoc-rpc-method-opcodes.md`](../re/aoc-rpc-method-opcodes.md). |
| 3 (`0x94A760`) | PlayerInterface `SmallCharList` (`0x20A5`) | constant. |
| 13 | PlayerInterface `OverrideGameTweaks` (`0x1A07`) | a **protobuf endpoint reference**, not an opaque block: `0x0D <fixed32 class id 0x7D155738 = PlayerInterface> 0x10 <varint 1> 0x20 <varint per-session instance id>` — consumes all 13 bytes. |

So it is the PlayerInterface method id (decoded) plus a constant `0x01` marker plus two PlayerInterface specials —
not a mystery appended to arbitrary packets. [V1: Captures/scripts/decode_rpc_headerdata.py]

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
indices. [V1: Captures/ghidra/DumpRpcTables.java]

The full per-interface catalog — 819 entries across 16 enums, with direction and class id — is **generated** from
`Opcodes.cs`, not written here: [`gen/opcodes.md`](gen/opcodes.md).

> A named opcode is **not** a decoded message. `gen/opcodes.md` enumerates every opcode (V1); whether its body is
> understood is a separate question answered by [`gen/coverage.md`](gen/coverage.md).

### The one exception

PlayerAgent/PlayerInterface additionally carries a **32-bit method hash** on the wire (alongside the 16-bit
opcode), not the `0x2000 + index` scheme alone. It is a dump task, not a hash to reverse: the login-critical
method set is recovered and verified end-to-end (a Frida-under-Wine hook on the client send path correlated with
a loopback capture) and carried as real associated ids (`PlayerAgentMethodIds`, appended by `WriteHeader`). Only
the methods the server does not implement (character create/delete/rename, Facebook token, hardcore, …) are still
un-enumerated, harvestable by the same technique. [V0: PlayerAgent 32-bit hash dispatch]

## Multiplexing

One GameServer TCP connection carries roughly four RPC class-pairs at once (e.g. GameCharAgent ↔
GameCharInterface, plus project/tradepost variants). A packet is routed by the pair `(receiver class id, opcode)`,
never by the connection. This is why the same opcode number means different things on different class ids, and why
cross-listener actions go through dispatcher seams rather than direct references. [V1: Captures/ghidra/DumpDispatch.java]

Direction convention: `*Agent` classes receive client→server calls; `*Interface` classes receive server→client
dispatches.

## See also

- [`03-iir-carrier.md`](03-iir-carrier.md) — opcode `0x2000` is itself a carrier for a second, 32-bit-hashed
  opcode space.
- [`gen/opcodes.md`](gen/opcodes.md) — the generated catalog.
- [`gen/coverage.md`](gen/coverage.md) — which opcodes have a decoded body.
