# Client settings and the character preview — the mid-session PlayerAgent socket (#611)

The retail client stores its UI state SERVER-side: hotkeys, chat-window layout, dock bars, per-character
preferences and the posed `preview.bin` the character-select screen draws. It uploads them over a **second,
short-lived PlayerAgent socket** it opens *while in world* — not after a disconnect — and gets them back at the
next login.

## The socket

Three sightings in the corpus, all the same shape:

| capture | stream | when | packets |
|---|---|---|---|
| `AssassinL23_rogue-assassin_login_randomActions-resurect` | 5 | ~80 s after the login stream closed | `AuthenticateLoggedIn` → `AuthenticateAck` → `UpdateSubscriptionSettings` |
| `Hoxxling_mage-heraldofxotli_create_modified_all_sliders-…` | 6 | mid-session | same |
| `Strie_rogue-assassin_login_randomActions` | 73 | mid-session | plus `UpdateCharacterSettings` and `UpdateCharacterPreview` |

`AuthenticateLoggedIn(accountId, cookie)` is the login `Authenticate` minus its trailing auth-status word, and
the two words it does carry are byte-identical to the ones the login connection sent — the same account id and
the same session cookie, in all three sightings. Everything after the ack arrives without a further handshake.

⚠️ Its opcode `0x20EC` is also `ReceivedCharacterSettings`'; `UpdateCharacterPreview`'s `0x20D6` is also
`VerifyLanguageSetting`'s. On this interface pair the 16-bit opcode is only the method varint's first group, so
dispatch keys on the reassembled registration constant (#608 / #610), never on the opcode.

## The IOBuffer

Every payload in the family is the client's `IOBuffer`:

```
[u32 byteLength][ "IOz1" ][u32 inflatedSize, LITTLE-endian][zlib stream]
```

`byteLength` counts everything after it, so **0 is a legitimately empty buffer** and no marker follows — that is
the 12-byte `SendCharacterSettings` retail sends x118 in the corpus for a character with nothing stored. Both
inner lengths are little-endian: this is a client-side file format shipped over a big-endian wire, not a wire
record.

Inflated, it is a **file archive** — the client's settings files concatenated:

```
repeated [u32 nameLength][name, LATIN1][u32 dataLength][data]      (both lengths little-endian)
```

| method | members | inflated |
|---|---|---|
| `UpdateSubscriptionSettings` (account-wide) | `hotkeys.xml`, `Prefs_2.xml` — 60 named values (audio/video/chat-bubble/camera) | 3.9 KB |
| `UpdateCharacterSettings` | `Chat\Windows.xml`, `DockAreas\SideBarLeft.xml`, `DockAreas\SideBarRight.xml`, `Prefs_2.xml` — 404 named values | 32 KB |
| `UpdateCharacterPreview` | `preview.bin` — a binary appearance record; **partially decoded** (#612), see below | 427 B |

Note the member names use BACKSLASHES.

## The checksum is over the COMPRESSED bytes

Both settings methods carry a trailing wire string: 32 lowercase hex chars. It is an **MD5 over the whole
IOBuffer payload — marker and inflated-size word included**, and it reproduces exactly on all three captured
uploads that carry one. `UpdateCharacterPreview` has no trailer, because no checksum map covers previews.

That fixes the storage model: the blob is kept **compressed and verbatim**. A deflate stream is not a function
of its contents (level, window and implementation all move it), so re-encoding it would change the checksum and
make every cached client copy read as stale. `ClientSettingsBufferCodec` therefore carries the payload as the
value and decodes the archive as a VIEW of it — `Build` writes the stored bytes back rather than recompressing.

## The round trip

| step | direction | message |
|---|---|---|
| reply to `GetStartupData` | s2c | one `SendCharacterPreview` per character that **has** one (characters without are skipped, never sent an empty buffer), then one `SendCharacterSettingsCheckSumMap` |
| the map | s2c | `[ListFrame count][ u32 characterId, string md5 ]*` — the md5 of that character's stored settings archive, **empty** for a character that has never uploaded any |
| after `LoginCharacter` | s2c | `SendCharacterSettings` — that character's stored archive, or the empty buffer |
| the client has applied them | c2s | `ReceivedCharacterSettings`, which is what drives the service handoff block |

Retail's maps show both cases in one message: a fresh character with an empty md5 alongside four that carry one,
and those four md5s are **stable across a year of captures** — which is what a server-side store looks like.

⚠️ The map's string used to be filled with the account's auth challenge — a 32-hex-char value of the right shape
and the wrong meaning. It advertised a checksum for settings the server did not hold, so no cached client copy
could ever match it.

## Where it lives

- Codecs: `Faolan.Core.Network/Codecs/PlayerAgent/` — `ClientSettingsBufferCodec` (the container and the archive),
  `AuthenticateLoggedInCodec`, `UpdateSubscriptionSettingsCodec`, `UpdateCharacterSettingsCodec`,
  `UpdateCharacterPreviewCodec`, `SendCharacterSettingsCodec`, `SendCharacterPreviewCodec`,
  `SendCharacterSettingsCheckSumMapCodec`.
- Handlers and senders: `Faolan/PlayerAgent/PlayerAgentListener.cs` and `Faolan/PlayerAgent/Packets.cs`.
- Storage: `CharacterClientSettings` (one row per character, `Settings` and `Preview`, either half nullable
  because they arrive as separate messages) and `Account.SubscriptionSettings`.
- Byte-exact gates: `ClientSettingsCodecTests` against the captured bodies, `ClientSettingsPersistenceTests`
  against a real SQLite database.

## Inside `preview.bin` (#612)

The server stores and echoes the record, which is all the client needs — it authored it. Constructing one, so a
character created here has a select-screen pose before it has ever logged in, needs the contents. Four distinct
payloads exist in the corpus (408 / 527 / 606 / 618 bytes, over three characters); they are reachable through
`ClientSettingsBufferCodec` alone, since the archive member is already parsed.

⭐ **The headline result: it is not a new vocabulary.** Every name in the record resolves against the
`MorphTargets` table we already ship, and every id resolves in the client RDB. So the preview is a character
appearance snapshot expressed in exactly the terms the create-editor and the spawn appearance use — which is what
makes constructing one from `CharacterBody` / `CharacterBodyResources` plausible rather than speculative.

### The grammar

Every byte of all four corpus payloads is accounted for by this, and the two lists are the only variable-length
parts:

```
[u8  version = 4]
[u32 modelId]                     the character's base MESH - an RDB 1010001 3D model
[u32 entryCount]                  2 in all four payloads
entryCount x ATTACHMENT (88 bytes):
    [u32 rdbType][u32 rdbId][u32 flags]
    [16 x f32]                    a row-major 4x4 affine; translation in the LAST ROW
    [u32 itemTemplateId][u32][u32]
N x SLOT (16 bytes):  [u32 slot][u32 resourceId][u32][u32]
[u32]                             ONE word between the two lists - see below
repeated NAMED:       [u32 index][u32 nameLength][name, LATIN1][f32 weight]   (nameLength 0 terminates)
```

⭐ **The header's three bytes after `04` are the low bytes of a MODEL ID**, not a count — `1091336` on the
408-byte record and `1091339` on the other three, both RDB **1010001** (3D model). That is the character's base
mesh, and it is why the byte differed between records: two body types, not two element counts.

**The attachment entries are `1010201` (MonsterData) and `1010030`, one of each, in either order.** Their tail's
first word is an **item template**: `3735601`, `3806202`, `3419923`, `3803265` across the four characters, every
one RDB type **1000020**, and `0` on the `1010201` entry. The last two words are constant per entry type —
`(0, 35)` on `1010201`, `(6, 58)` on `1010030`.

**The transform is real, not vestigial.** Most matrices are identity, but a `1010030` entry carries
`[0, 0, -0.12, 1]` in its last row — a small Z offset — which is what identifies the layout as row-major with
translation in the last row rather than an unused field.

### ⭐ The attachment `flags`: bit 2 says the transform is real

The eight attachment entries in the corpus carry three values — `5`, `11`, `33` — and **bit 2 (`0x04`) is the
only bit that separates the entries with a real matrix from the ones with an identity**:

| flags | bits | entries | matrix |
|---:|---|---:|---|
| `5` | 0,**2** | 3 | a translation (`-0.12`, `-0.06`, `-0.06` in the last row) |
| `11` | 0,1,3 | 1 | identity |
| `33` | 0,5 | 4 | identity |

Bit 0 is set on all three, bit 1 and bit 3 only on `11`, bit 5 only on `33`; bit 2 partitions the set exactly,
and "the transform slot is meaningful" is what a flags bit next to a matrix would carry. So the transform is
**opt-in**, which also answers why most matrices are identity: they are unset fields, not neutral transforms.

⚠️ Eight entries is a small sample and the bit is inferred from the partition, not from the client's reader. It
is the reading to build on and the first thing to re-check if a built record is rejected.

### What is not decoded

One word: **the single `u32` between the slot list and the named list** — `0`, `0`, `18`, `1018` across the four
payloads, so it carries a value rather than terminating anything, and the slot list's length has to be found by
locating the named list rather than read from a count.

⚠️ **That is the whole remaining obstacle to a builder, and it is not the binding one.** Acceptance is an
in-client round-trip — the client has to take the record and draw the pose — and a record it REJECTS is worse
than the current no-preview state, which is what retail itself produces for a freshly created character. So the
builder is gated on a client run rather than on more decode. Open work tracked in Gitea.
