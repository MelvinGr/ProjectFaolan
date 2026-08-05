# The scry message box (`DispatchScryShowMessageBox` / `ScryMessageBoxAnswer`)

The modal confirm the client raises on behalf of a server-side **scry script**. Both directions are decoded
from the corpus as a matched pair: **15 shows, 15 answers**, and every answer echoes its show exactly.

Fourteen of the fifteen are the **zone-line travel prompt** — *"Do you want to travel to Ymir's Pass?"* —
raised when the player uses a door. The fifteenth is category `10035` with a different context and is not
about travel; **the category is what separates them**, and anything reading these off the wire has to gate on
it (see the extractor note below).

## s2c — `GameCharInterface.DispatchScryShowMessageBox` (`0x205E`)

```
[string cText]              a <localized …/> RemoteFormat record (see on-screen-feedback.md)
[u32 nText1][u32 nText2][u32 nText3]
[u32 nTip1][u32 nTip2][u32 nTip3]
[u32 nScriptThreadCtxId]    the server-side script thread waiting on the answer
[string cCustomName]        empty in every capture
[u64 nCtxCookie]            the thread's one-shot cookie
```

Field names are the client's own (`docs/re/aoc-client-rpc-methods.json`). Across the 14 travel shows only
`cText`, `nScriptThreadCtxId` and `nCtxCookie` vary: `nText1..3` are `0x6F/0x70/0x71` on twelve of them (a
pointer-looking pair on the two Amphitheatre ones), `nTip1..3` are always `0x0250B94C / 0x0A31C0D3 / 0`, and
`cCustomName` is always empty.
[V2: NecroL80_mage-necromancer_login_enter_king_conan_castle2.tshark.json frame 1957]

### `cText` names the DESTINATION MAP

```
<localized id="1090" category="520" flags="" key="%z&#36;n_zw5ComNiHcqoW4j" >
```

`id` is the map the trip goes to and `category` is `520`, the client's map-name category — every travel box is
followed by a redirect and a `MapChange` to exactly that map. The seven destinations the corpus confirms into
are 1004 Spider Caverns, 1020 Field of the Dead, 1090 Ymir's Pass, 1092 the Amphitheatre of Karutonia, 2010
Tarantia Noble District, 2090 Thunder River and 2094 Slaughterhouse Cellar.

`key` is a per-string LDB key ([`ldb-string-index.md`](../rdb/ldb-string-index.md)), and it is **inert here**:
category 520 is plaintext, so the client never decrypts with it. It is replayed to reproduce retail's bytes.
Two different doors into Tarantia Noble District carry the *same* key, which is what shows the key belongs to
the destination's name string rather than to the door.

## c2s — `GameCharAgent.ScryMessageBoxAnswer` (`0x203B`)

```
[u32 nAnswer][u32 nScriptThreadCtxId][u64 nCtxCookie]
```

16 bytes, and the last two fields are a **verbatim echo** of the show's — that pairing holds in all fifteen
shows, which is what pins the grammar:

| capture | show `nScriptThreadCtxId` / `nCtxCookie` | answer |
|---|---|---|
| `..._enter_king_conan_castle2` f1957 → f1968 | `0034BCBB` / `0000000059407280` | `1`, same two |
| `..._leave_king_conan_castle-Dismount` f837 → f848 | `00097F96` / `00000000350713F0` | `1`, same two |
| `..._conans-castle-exit` f1630 → f1636 | `001FAA9D` / `0000000062872390` | `1`, same two |

`nAnswer` is `1` in every capture — the player always confirmed, so the value for the other button is
unobserved. [V2: NecroL80_mage-necromancer_login_leave_king_conan_castle-Dismount.tshark.json frame 848]

## What the server does

Raises the travel box on a zone-line door whose destination confirms, and resumes the trip on the answer.

Because the answer is a **pure echo**, no script thread is involved on either side — retail's
`nScriptThreadCtxId`/`nCtxCookie` are its own script-context handles, and ours are just a correlation token
(the character id plus a per-send counter, held in `INetworkClientSession.PendingTravelConfirm`).
`SendTravelConfirmation` mints the pair and parks the trip; `ScryMessageBoxAnswer` teleports if the echo
matches an outstanding box and the answer is `1`, and drops the trip otherwise.

**Which destinations confirm is harvested, not inferred.** Half the captured door walk-throughs raise no box
at all, so "the door crosses a map" is not the rule. The corpus splits cleanly by DESTINATION — doors to the
same map agree, and carry the same key — so `world-teleports` writes `Map.TravelConfirmKey` from the
`UseObject → DispatchScryShowMessageBox → N3Teleport` sequence, gated on `category == 520` **and** on the
box's `id` matching the map the same sequence then teleports to. A map no capture raised a box for teleports
straight through, exactly as before. See [`teleport-changemap.md`](teleport-changemap.md).

⚠️ Two things the corpus does not settle, so neither is modelled: whether a map could confirm from one door
but not another (none observed), and what a **decline** looks like — every captured answer is `1`, so the
value for the other button is unobserved and we treat "not `1`" as a decline.
