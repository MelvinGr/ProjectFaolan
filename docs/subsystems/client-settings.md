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
| `UpdateCharacterPreview` | `preview.bin` — a binary appearance record (id/float triples plus texscript names such as `*texscript_1108153`), **not decoded** | 427 B |

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

`preview.bin`'s internals are the one thing still undecoded here — the server stores and echoes it, which is all
the client needs, but constructing one (so a character created on our server has a select-screen pose before it
has ever logged in) would need it. Open work tracked in Gitea.
