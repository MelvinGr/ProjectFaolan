# 04 — Encoding rules

The field-level rules shared by every message body. Get one of these wrong and the bytes look plausible but the
values are silently off — the failure mode this page exists to prevent.

---

## Endianness — the trap

There are **two** endiannesses in play, and which one applies depends on whether the field is protobuf-framed:

| Field kind | Endianness |
|---|---|
| Raw body integers (the IIR record fields) | **big-endian** |
| Raw body floats (positions, rotations) | **big-endian** |
| protobuf `fixed32` / `fixed64` (incl. protobuf floats) | **little-endian** |

So a position in a raw IIR body is BE, but the same conceptual float inside a protobuf sub-message is LE. The
stream has separate helpers on purpose: `WriteFloat` (BE) vs `WriteFloatLittleEndian`.

- Raw-body floats are big-endian, and round-trip with the reader.
  [V3: FaolanStreamFloatTests.WriteFloatBigEndian_IsBigEndian_AndRoundTripsWithReadFloat]
- A captured position decodes correctly as big-endian.
  [V3: FaolanStreamFloatTests.ReadFloat_DecodesCapturedBigEndianPosition]
- A `Vector3` is three consecutive big-endian floats.
  [V3: FaolanStreamFloatTests.WriteVector3BigEndian_WritesThreeBigEndianFloats]

The one place LE leaks into a raw header is the `ProtoEndpoint` class id (a protobuf `fixed32`), covered in
[`02-rpc.md`](02-rpc.md#protoendpoint).

## Varint and ZigZag

Inside protobuf sub-messages, integers are base-128 varints (LEB, unsigned). Signed protobuf fields use **ZigZag**:
`(n << 1) ^ (n >> 31)`. Reading a ZigZag field as a plain varint is wrong for negatives and **halves** positives.

This is not hypothetical: `ProtoScryScriptIIR.int_parameter` is ZigZag, and the character-editor slider values
decode correctly only when it is treated as signed.
[V3: CharacterMorphsTests.TryApply_StoresSignedSliderValue]

The generated schema surfaces the encoding of every protobuf field — the ZigZag and FixedSize ones especially —
so you never have to guess: [`gen/protobuf.md`](gen/protobuf.md).

## Length framing

Two length conventions appear, both prefixing their payload:

- `[u32 BE length][payload]` — the outer packet frame ([`00-transport.md`](00-transport.md)) and most IIR bodies.
- `[u16 BE length][payload]` — shorter inline arrays (e.g. the XML in a chat/feedback record).

A length prefix is always validated against the bytes actually remaining before it is used to size anything.
[V3: NetworkHardeningTests.ReadArrayUInt32Length_LengthBeyondRemaining_Throws]

## Strings

Strings are length-prefixed UTF-8, **no null terminator**, using whichever length width the surrounding grammar
specifies. remoteformat XML (localized token + parameters) is carried as such a string; it is how the server sends
a localized system line without shipping literal text.
[V3: ChatTextCodecTests.Build_FromArbitraryXml_LeadsWithTheSubopcode]

## See also

- [`gen/protobuf.md`](gen/protobuf.md) — per-field wire encoding for all 60 protobuf messages.
- [`02-rpc.md`](02-rpc.md) — the one big-endian header with a little-endian field in it.
