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

### What is decoded

| region | shape | what it is |
| --- | --- | --- |
| header | `04 <u8> a7 10 00` | byte 0 constant; byte 1 is `08` on the 408-byte record and `0b` on the other three |
| attachment entries | `[u32 slot][u32 rdbType][u32 rdbId][u32 flags][4×4 identity matrix, 16 × f32 LE]` | a resource attached at a slot, with a transform. `1010201` / `1010030` appear as the type word, and the id resolves: `1139362`, `215943`, `323710`, `700815` are all CDN-hosted client resources |
| quadruples | `[u32 slot][u32 id][u32][u32]` | ids resolve to real records — `3735601` is **type 1000020, an ITEM TEMPLATE** (so this is worn gear) and `3394702` **type 1010001, a 3D model** |
| named tail | repeated `[u32 index][u32 nameLength][name, LATIN1][f32 weight]`, terminated by a zero length | the appearance proper — see below |

### The named tail is the character's own look

Two vocabularies share it, and the weight tells them apart:

- **resource assignments at weight `1.0`** — `*texscript_213937`, `*texscript_3113027`, `*beardmesh_3181884`;
- **body morph sliders at small signed weights** — `arm`, `arse`, `bosom`, `chest`, `leg`, `stomach`, `thigh`
  at `0.1` / `-0.038` / `0.011` / `0.016` / `0.042` …

**All of them are rows in `MorphTargets`.** The seven sliders are exactly that table's `Category` 1, and the
`*texscript_*` / `*beardmesh_*` names resolve there too. The float is the slider position, and the same character
carries a consistent set across its uploads while the three characters differ — which is what says these are
per-character appearance rather than a constant preamble.

### What is not decoded

The three header bytes after `04`, the `flags` word on an attachment entry, the meaning of the transform (every
observed matrix is identity, so nothing distinguishes "no transform" from "not used"), and the last two words of
a quadruple. Those are what stand between reading a preview and BUILDING one — and a record the client rejects
would be worse than the current no-preview state, which retail itself produces for a freshly created character.
Open work tracked in Gitea.
