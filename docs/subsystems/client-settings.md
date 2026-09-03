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
| `UpdateCharacterPreview` | `preview.bin` — a binary appearance record; **decoded** (#612, #788), see below | 427 B |

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
- Handlers and senders: `Faolan/PlayerAgent/PlayerAgentListener.cs` and `Faolan/PlayerAgent/PlayerAgentListener.Outbound.cs`.
- Storage: `CharacterClientSettings` (one row per character, `Settings` and `Preview`, either half nullable
  because they arrive as separate messages) and `Account.SubscriptionSettings`.
- Byte-exact gates: `ClientSettingsCodecTests` against the captured bodies, `ClientSettingsPersistenceTests`
  against a real SQLite database.

## Inside `preview.bin` (#612, #788)

The server stores and echoes the record, which is all the client needs — it authored it. Constructing one, so a
character created here has a select-screen pose before it has ever logged in, needs the contents. Four distinct
payloads exist in the corpus (408 / 527 / 606 / 618 bytes, over three characters); they are reachable through
`ClientSettingsBufferCodec` alone, since the archive member is already parsed.

⭐ **The headline result: it is not a new vocabulary.** Every name in the record resolves against the
`MorphTargets` table we already ship, every id resolves in the client RDB, and the field NAMES come from the
client's own `monsterdata` XML (see below). So the preview is a character appearance snapshot expressed in
exactly the terms the create-editor and the spawn appearance use.

### The grammar

Every byte of all four corpus payloads is accounted for, and **every list is count-prefixed** — which is what
makes the record buildable rather than only readable. All integers and floats are LITTLE-endian, like the `IOz1`
archive around them.

```
[u8  version = 4]
[u32 monsterDataId]                                    RDB 1010201 — the body definition
[u32 resourceCount]   resourceCount x PREVIEW RESOURCE (88 bytes)
[u32 meshCount]       meshCount     x PREVIEW MESH     (16 bytes)
[u32 morphCount]      morphCount    x PREVIEW MORPH    (variable)
[u32 0]                                                a fourth list, EMPTY in all four payloads

PREVIEW RESOURCE  [u32 resourceType][u32 resourceId][u32 attractorType]
                  [16 x f32]                           a row-major 4x4; translation in the LAST row
                  [u32 itemTemplateId][u32][u32]
PREVIEW MESH      [u32 meshIndexFileId][u32][u32][u32 slot]
PREVIEW MORPH     [u32 nameLength][name, LATIN1][f32 weight][u32 index]
```

⭐ **The client's `monsterdata` XML names the fields.** RDB **1010201** record `1091339` is
`<monsterdata name="StygianMaleCharacter" …>` and carries

```xml
<previewset name="Guywithswordshield">
  <previewresource attractortype="33" name="head"   resourceid="18312"  resourcetype="1010201"/>
  <previewresource attractortype="6"  name="Shield" resourceid="108527" resourcetype="1010030"/>
  <previewresource attractortype="1"  name="Sword"  resourceid="113386" resourcetype="1010030"/>
</previewset>
```

— which is the record's head and its first list, field for field. So the header id is the **monsterdata** (not a
3D model: `1091336` = `CimmerianFemaleCharacter`, `1091339` = `StygianMaleCharacter`), and the third word of an
entry is an **`attractortype`** — an attachment POINT (1 = main hand, 2 = ranged, 6 = shield, 33 = Head, …), not
a flags word. The corpus carries `33` on the `1010201` head entry and `5`/`11` on the `1010030` weapon.

**The transform is real, not vestigial.** Most matrices are identity, but a `1010030` entry carries `-0.12` /
`-0.06` in its last row — a small Z offset — which is what identifies the layout as row-major with translation
in the last row rather than an unused field.

**The mesh list is the worn look.** Every id resolves as RDB **1010030**, a `MeshIndexFile` whose
`<MeshClusters>` names the body part outright (`ItemNeck`, `ItemBelt`, `ItemLegs`, `ItemWrist`), and its `slot`
is the ordinary [`CharacterEquipmentSlot`](../../Faolan.Core/Enums/CharacterEquipmentSlot.cs) — `1`, `2`, `5`,
`7`, `11`, `12`, `14`–`18` worn and `1002`/`1007`/`1011`/`1014`/`1018` vanity are all attested. ⚠️ Slot `0` is
the odd one out: it carries an RDB **1010001** 3D model rather than a mesh index — the hair/base model, which
has no equip slot.

**The morph list is the create editor's own stream:** a resource ref at weight exactly `1.0`
(`*texscript_…`, `*beardmesh_…`) or a `MorphTargets` category-1 body slider at a small signed weight (`arm`,
`arse`, `bosom`, `chest`, `leg`, `stomach`, `thigh`). Its trailing `index` is the editor arrival order, the same
ordering `CharacterBodyResource.Sequence` keeps.

### What is not named

Three words carry values nothing in the corpus explains, and none of them blocks a builder — every one is
reproducible from a record we hold:

* a **preview resource's last two words** — constant per `resourceType` across all four payloads, `(6, 58)` on
  the `1010030` weapon and `(0, 35)` on the `1010201` head;
* a **preview mesh's second word**, `0`–`3`. It is a property of the MESH, not of the slot: the same mesh id
  carries the same value at different slots and in different payloads, and the mesh's own `MeshIndexFile` XML
  does not distinguish them — which reads like a material or colour set index chosen by the item;
* a **preview mesh's third word**, zero in every captured entry.

Where each of the three comes from is now read off the client's own record WRITER, `FUN_00fe9f60` (#464,
2026-09-03; `[V1: tools/ghidra/DecompileWithCallees.java]`): it writes the version byte, the monsterdata id, then
each attachment as type / id / attractor / the 16 floats / attachment `+0x80` (the item template) / attachment
`+0x84` (the first unnamed word) / the attached VISUAL's `+0x4c` (the second — a property of the visual object,
which fits a per-class constant: 35 on the head's `VisualCATMesh_c`, 58 on the weapon's `VisualMesh_t`); then
each mesh entry as words 0, 1, 5 and 6 of a 28-byte struct. The preview LOADER hands every mesh entry to
`VisualCATMesh_c::AddSkinnedMesh(this, nIdxOrFmeId, word1, word5, slot, word2)` (`FUN_00fed5d0`, named by its
own log line), where the second word is consumed as a FLOAT and stored on the `MeshInstance` at `+0xd0` when it
lies in `[0, 6)` with bit `0x20` of `+0xe0` marking it set, and the third word is stored at instance `+0x1c`,
defaulting to the visual's own `+0x10` when 0 — an optional override, the same shape as the slot's default from
`+0x14`. Neither instance field has a reader the stride probes can find, so the two stay unnamed; what is settled
is that they are rendering parameters of the attached mesh, not identity, and that a builder may write 0 for both.

### Building one

`CharacterPreviewSetCodec` parses and re-encodes both committed payloads byte-exact, and
`SendCharacterPreviewCodec.Build(characterId, CharacterPreviewSet)` packs a constructed record into a fresh
`IOz1` archive (`ClientSettingsBufferCodec.Build(files)`). ⚠️ **Nothing in the server authors one.** Retail
itself sends no preview for a freshly created character, so a constructed preview is an improvement on retail
rather than a divergence being fixed — and a record the client REJECTS is worse than the current no-preview
state. Acceptance is an in-client run: the client has to take the record and draw the pose. Open work tracked in
Gitea.
