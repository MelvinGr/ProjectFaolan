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
opcode is reproduced verbatim per call-site; its grammar is not decoded. [V0: RpcHeader trailing headerData]

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

PlayerAgent/PlayerInterface dispatches on a **32-bit hash**, not the `0x2000 + index` scheme. Login works because
the handful of observed values are hard-coded; the hash function is not recovered, so this interface cannot be
enumerated. [V0: PlayerAgent 32-bit hash dispatch]

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
