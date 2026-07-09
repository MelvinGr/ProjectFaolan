# 00 — Transport

How bytes become packets. Everything above this layer (RPC, IIR, message bodies) assumes a framed, decompressed
packet; this page is how you get one.

Every fact is tagged. See [`PLAN.md`](PLAN.md#1-the-verification-ladder) for what the tags mean.

---

## Framing

One TCP connection carries a stream of length-prefixed packets. The frame is:

```
[u32 BE length][payload]        length = byte count of payload (it does NOT count itself)
```

The reader (`ConanMessageFramer.PushBytes`) is a pure incremental parser: it accumulates the 4-byte header, then
the payload, and yields whole packets. It must be, because TCP splits and coalesces arbitrarily — a packet, a
compression marker, and the next packet routinely arrive in one `recv`.
[V3: NetworkHardeningTests.Framer_CompressionMarkerCoalescedWithPacket_KeepsThePacket]

A hostile or corrupt length prefix is rejected rather than used to size an allocation.
[V3: NetworkHardeningTests.ReadArrayUInt32Length_HostileLengthPrefix_ThrowsInsteadOfAllocating]

## Packet layout

Inside the frame, every Conan service packet has the same head:

```
[u32 BE length][u32 crc32][u32 BE hdrlen][RpcHeader][body]
```

- `length` is written as `Length - 4` and `crc32` is computed over the whole stream, both back-patched once the
  body is complete (`PacketStream.WriteLengthHash`). This is why builders write two zero words first.
- `hdrlen` counts the `RpcHeader` bytes only, not the body.
- `RpcHeader` is decoded in [`02-rpc.md`](02-rpc.md).

The AgentServer (chat) service is the exception: its packets carry a raw u16 opcode and **no RpcHeader**.
[V1: Captures/ghidra/DumpRpcTables.java]

## Compression

Compression is a property of the **direction**, not of a packet. At a packet boundary either side may emit a
fixed 9-byte control frame:

```
[80 00 00 05][4 bytes][flag]
```

`flag == 0` means everything after it in that direction is a single zlib deflate stream; non-zero means the
stream stays plaintext. Retail only ever sends this server→client, to open the GameServer download stream.
[V2: Barbarian-login.tshark.json]

Two consequences that have each caused a real bug:

1. **Our server never compresses.** It emits no marker. Our client emits a standalone marker with no compressed
   payload behind it, so the framer *skips the marker and keeps parsing*. The marker can straddle a `recv`
   boundary, so the skip count is carried across pushes.
   [V3: NetworkHardeningTests.Framer_CompressionMarkerSplitAcrossPushes_IsSkipped]

   An earlier reader treated the tag as "decompress from here" and dropped the rest of the receive buffer. When
   TCP coalesced the marker with the packets behind it, those packets were silently eaten — which is why the
   post-ready `0x205f` ability grant intermittently arrived with an empty spellbar.

2. **Inflating attacker-controlled input is bounded.** A deflate stream that expands without limit is dropped
   rather than inflated. [V3: NetworkHardeningTests.Framer_DecompressionBomb_IsDroppedNotInflatedToOom]

The four bytes between the tag and the flag are not decoded. [V0: compression marker bytes 4..7]

## Reading captures

`Captures/scripts/tshark_dump.sh` produces the canonical `*.tshark.json`: TCP-reassembled, one record per packet,
**including the compressed download packets** (the Wireshark plugin follows the deflate stream). Without
reassembly roughly two thirds of the GameServer download traffic is missing — do not dissect raw.

On lossy captures pass `-o tcp.analyze_sequence_numbers:FALSE` (the script does).

## See also

- [`01-crypto.md`](01-crypto.md) — the login block is encrypted *inside* this framing, not around it.
- [`02-rpc.md`](02-rpc.md) — the `RpcHeader` this page skips over.
- [`gaps.md`](gaps.md) — what we don't know here.
