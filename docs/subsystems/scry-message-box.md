# The scry message box (`DispatchScryShowMessageBox` / `ScryMessageBoxAnswer`)

The modal confirm the client raises on behalf of a server-side **scry script** — in every capture, a
zone-transition prompt (entering and leaving King Conan's castle, and a changemap). Both directions are
decoded from the corpus as a matched pair: six shows, six answers, and every answer echoes its show exactly.

## s2c — `GameCharInterface.DispatchScryShowMessageBox` (`0x205E`)

```
[string cText]              a <localized …/> RemoteFormat record (see on-screen-feedback.md)
[u32 nText1][u32 nText2][u32 nText3]
[u32 nTip1][u32 nTip2][u32 nTip3]
[u32 nScriptThreadCtxId]    the server-side script thread waiting on the answer
[string cCustomName]        empty in every capture
[u64 nCtxCookie]            the thread's one-shot cookie
```

Field names are the client's own (`docs/re/aoc-client-rpc-methods.json`). All six captured shows are the
same 113-byte body apart from the last twelve bytes: `cText` is always
`<localized id="2010" category="520" flags="" key="_eJgq#Y4?(P_laXxWMD)" >`, `nText1..3` are always
`0x6F/0x70/0x71`, `nTip1..3` are always `0x0250B94C / 0x0A31C0D3 / 0`, and `cCustomName` is always empty —
only `nScriptThreadCtxId` and `nCtxCookie` change per prompt.
[V2: NecroL80_mage-necromancer_login_enter_king_conan_castle2.tshark.json frame 1957]

## c2s — `GameCharAgent.ScryMessageBoxAnswer` (`0x203B`)

```
[u32 nAnswer][u32 nScriptThreadCtxId][u64 nCtxCookie]
```

16 bytes, and the last two fields are a **verbatim echo** of the show's — that pairing holds in all six
captures, which is what pins the grammar:

| capture | show `nScriptThreadCtxId` / `nCtxCookie` | answer |
|---|---|---|
| `..._enter_king_conan_castle2` f1957 → f1968 | `0034BCBB` / `0000000059407280` | `1`, same two |
| `..._leave_king_conan_castle-Dismount` f837 → f848 | `00097F96` / `00000000350713F0` | `1`, same two |
| `..._conans-castle-exit` f1630 → f1636 | `001FAA9D` / `0000000062872390` | `1`, same two |

`nAnswer` is `1` in every capture — the player always confirmed, so the value for the other button is
unobserved. [V2: NecroL80_mage-necromancer_login_leave_king_conan_castle-Dismount.tshark.json frame 848]

## What the server does

Nothing, deliberately. We never raise a box (the script VM was removed — the scripted-content path is
`ConversationService` plus the GM/quest triggers), so there is no thread to resume and no cookie to match.
`GameServerListener.Inbound.cs` reads all three fields and logs them, so an answer arriving out of nowhere is
visible rather than reported as an unknown opcode.

Wiring it up means the reverse: a server-side prompt would mint a `(nScriptThreadCtxId, nCtxCookie)` pair,
send the show, and resume whatever it parked when the matching answer comes back. The zone-transition doors
are the obvious first user — see [`teleport-changemap.md`](teleport-changemap.md).
