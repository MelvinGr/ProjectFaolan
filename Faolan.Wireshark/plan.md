# Wireshark C Plugin Plan - Decompression Only (GameServer)

## Goal
Build a native Wireshark **C plugin** that only does one thing for now:

- detect the GameServer compression transition
- run incremental RFC1950/zlib decompression on that stream
- show decompression status and decompressed bytes

No packet decoding is in scope for v1.

---

## Scope and non-scope

### In scope (v1)
- attach to GameServer TCP traffic (port preference + Decode As)
- track stream state per conversation (prefer per direction)
- detect compression marker `0x80 00 00 05`
- buffer and consume 9-byte compression start header
- initialize and maintain zlib inflate state
- incrementally inflate subsequent payload bytes
- display decompressed byte blobs and stream diagnostics
- handle inflate errors safely with expert info

### Out of scope (v1)
- Conan packet framing
- opcode parsing
- packet name mapping
- body field dissection
- protocol heuristics beyond manual binding

---

## Behavior to mirror from existing Faolan code
Reference behavior in:

- `Faolan.PacketAnalyzer/PacketCaptureReader.cs`
- `Faolan.Core/Network/Conan/ConanMessageFramer.cs`

Important rules:

- marker prefix: `0x80 00 00 05`
- start header length: 9 bytes
- bytes after header are compressed
- one inflater per stream after transition (no per-frame reinit)
- failures should degrade safely (no crashes, no infinite loops)

---

## Deliverable definition
A successful v1 plugin should:

1. Load in Wireshark and bind to selected GameServer TCP port(s)
2. Track compression state for each stream direction
3. Detect and consume the 9-byte compression start header
4. Initialize zlib once per compressed stream
5. Inflate across many TCP packets incrementally
6. Add a protocol subtree showing:
   - direction
   - compression state
   - header progress (0..9 before activation)
   - compressed input byte count (for this frame)
   - decompressed output byte count (for this frame)
7. Show decompressed output as raw bytes in the tree
8. Report zlib issues through expert info

---

## Minimal plugin structure

```text
Faolan.Wireshark/
  CMakeLists.txt
  packet-faolan-gameserver-zlib.c
  packet-faolan-gameserver-zlib.h
  README.md
```

Optional split later:

```text
packet-faolan-conversation.c
packet-faolan-zlib.c
packet-faolan-ui.c
```

---

## Conversation state model

Use direction-aware state so server stream inflate state is never mixed with client data.

```c
typedef struct {
    gboolean compression_enabled;
    gboolean compression_failed;

    guint8 pending_header[9];
    guint32 pending_header_count;

    z_stream zstrm;
    gboolean zstrm_initialized;
} faolan_stream_state_t;

typedef struct {
    faolan_stream_state_t c2s;
    faolan_stream_state_t s2c;
} faolan_conv_state_t;
```

Notes:
- keep state persistent across redissection-safe conversation storage
- only initialize zlib after verified header prefix
- if inflate fails, set `compression_failed` and stop trying for that stream

---

## Processing flow per TCP frame

1. Get conversation + pick `c2s` or `s2c` stream state
2. If compression not enabled:
   - append bytes into pending 9-byte header buffer
   - once 9 bytes reached:
     - if prefix matches `0x80 00 00 05`: enable compression + init zlib
     - else: treat as uncompressed/no-op for this plugin
3. If compression enabled and not failed:
   - feed payload bytes into `inflate()` incrementally
   - collect any produced output bytes
4. Add subtree items:
   - mode and counters
   - decompressed bytes (if any)
   - expert warning/error (if any)

Because this v1 is decompression-only, decompressed output remains raw bytes and is not further parsed.

---

## Error handling rules

- `inflate()` returns `Z_OK` with 0 output: allowed, not fatal
- `Z_STREAM_END`: mark stream finished; retain status in UI
- `Z_DATA_ERROR`, `Z_MEM_ERROR`, etc.: mark `compression_failed`, add expert item
- never loop indefinitely if zlib produces no progress
- never crash on malformed input

---

## Wireshark UI fields for v1

Show only decompression diagnostics:

- Stream direction (`Server -> Client` / `Client -> Server`)
- Compression state (`Disabled`, `HeaderPending`, `Enabled`, `Failed`, `Finished`)
- Header bytes collected (`n/9`)
- Input compressed bytes in this frame
- Output decompressed bytes in this frame
- Raw decompressed bytes (byte array)

No opcode/packet fields yet.

---

## Development phases

### Phase 0 - skeleton
- register protocol + fields
- bind to TCP port preference + Decode As
- create conversation state

Exit: dissector loads and displays a subtree on bound traffic.

### Phase 1 - compression transition
- implement 9-byte pending header buffering
- verify `0x80 00 00 05` prefix match logic
- initialize zlib stream at transition

Exit: transition point matches Faolan analyzer behavior.

### Phase 2 - incremental inflate
- feed payload across frames into persistent zlib state
- collect output bytes safely
- add expert info on failure states

Exit: decompressed output appears consistently across captures.

### Phase 3 - hardening
- validate retransmission/redissection behavior
- validate direction separation
- add defensive limits for per-frame output growth

Exit: stable output and safe failure behavior on malformed traffic.

---

## Validation plan (capture-backed)

Primary captures:

- `Captures/account-stricken997-character-Strie-login-randomActions.pcapng`
- `Captures/account-stricken997-character-Strie-create.pcapng`
- `Captures/faolanx-charlogin.pcapng`

Checks:

1. plugin attaches to expected GameServer stream
2. header pending increments correctly when split across TCP frames
3. compression activation frame aligns with analyzer expectations
4. decompressed byte output is produced after activation
5. no crashes or endless loops on malformed or truncated captures

Optional ground truth check:
- compare per-frame decompressed byte lengths against a debug dump from `PacketCaptureReader` transform stage

---

## Risks and mitigations

### Redissection state drift
Risk: state mutation causes inconsistent results when Wireshark re-dissects.
Mitigation: use conversation-scoped deterministic state and avoid frame-order assumptions beyond TCP sequence direction.

### Mixed stream directions
Risk: combining c2s/s2c bytes corrupts inflater state.
Mitigation: separate `faolan_stream_state_t` for each direction.

### Overgrowth of output buffers
Risk: memory spikes on malformed compressed input.
Mitigation: impose a per-frame decompressed output cap with expert warning when truncated.

---

## Definition of done (v1)

- plugin loads in Wireshark
- can be attached to GameServer TCP traffic
- detects `0x80 00 00 05` and consumes a 9-byte start header
- performs incremental zlib inflate on the selected stream
- displays decompressed raw bytes and state diagnostics
- handles bad data safely with expert info
- contains no packet decoding/framing logic

---

## First concrete coding task

Create `Faolan.Wireshark/packet-faolan-gameserver-zlib.c` with:

1. protocol registration and fields for decompression diagnostics
2. conversation state allocation (`c2s` + `s2c`)
3. 9-byte header buffering and marker detection
4. persistent zlib inflate flow and decompressed byte display
