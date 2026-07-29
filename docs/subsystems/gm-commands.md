# GM commands: `gm_level` is a BITMASK (#448/#449)

The retail client's GM surface is gated by one wire value — character stat `0x0D7`
(`CharacterStatId.GmLevel`), which rides along as protobuf `player_stats.gm_level`. It is **not a level**. It is a
**bitmask of privileges**, and a command is offered when

```
(gmLevel & command.requiredMask) != 0
```

with `requiredMask == 0` meaning unrestricted. Both halves come out of the client:

- **the bits** are registered by `FanaticGMLevelCommand_c`'s constructor (Ghidra `FUN_006336d0`) — the object behind
  the `/gm` command — as a literal name→bit table;
- **the AND** is the filter in the `/help` renderer (`FUN_006c86b0`), which reads `GetStat(0xD7, 0)` once and tests it
  against each registered command's mask word before listing it.

This retires the earlier "the command surface is tiered, `/kill` needs a higher level than 5" reading, which was an
artefact of reading a mask as a magnitude. At `gm_level = 10` (`PlayerResurrect | TeleportDynels`) `/kill` is refused
not because 10 is too low, but because 10 does not contain `NPCCommands`.

"Fanatic" is the client's own name for its GM/admin subsystem — it also names the `Fanatic` debug channel and the
tweak table's `TWK_karma_Fanatic*` entries. Nothing to do with player-facing content.

## The privileges

Modelled as `GmPrivileges` (`Faolan.Core/Enums/GmPrivileges.cs`).

| bit | name | | bit | name |
|---|---|---|---|---|
| `0x00000001` | Developer | | `0x00002000` | AssistSummon |
| `0x00000002` | PlayerResurrect | | `0x00004000` | ChangeName |
| `0x00000004` | TeleportSelf | | `0x00008000` | ChangeNick |
| `0x00000008` | TeleportDynels | | `0x00010000` | ShowGMTag |
| `0x00000010` | PlayerDamage | | `0x00020000` | EffectCommand |
| `0x00000020` | GetStats | | `0x00080000` | NPCCommands |
| `0x00000040` | SetStats | | `0x00100000` | CanCloak |
| `0x00000080` | InvisibleToMonsters | | `0x00400000` | CanKick |
| `0x00000100` | ImmuneToAOE | | `0x01000000` | GMChat |
| `0x00000200` | GodMode | | `0x02000000` | NoCriteriaCheck |
| `0x00000400` | Broadcast | | `0x04000000` | CityCheck |
| `0x00000800` | Sitecast | | `0x10000000` | HideFromCommunityServer |
| `0x00001000` | ACGspawn | | | |

Seven bits are used by the shipped roles below but never named by the client — `0x00040000`, `0x00200000`,
`0x00800000`, `0x08000000`, `0x20000000`, `0x40000000`, `0x80000000`. They are deliberately absent from the enum
rather than guessed at.

## The roles the client ships

Registered beside the bits, so `/gm support2` names one. Verbatim values — each set is Funcom's own policy, which is
why they are recorded as roles rather than rebuilt from flags. `GmRoles` in the same file.

| role | mask | |
|---|---|---|
| `all` | `0xFFFFFFFF` | every bit, including the seven unnamed |
| `dev` | `0x09011075` | |
| `internal` | `0x000BC0EF` | |
| `volunteers1` | `0x010123A4` | |
| `volunteers2` | `0x010123A4` | registered as a straight copy of `volunteers1` |
| `support1` | `0x0941F3A6` | |
| `support2` | `0x0951FFEE` | registered as `support1 \| 0x00100C48` |
| `operations` | `0x09410C20` | |

`AccountTypeExtensions.ToClientGmLevel` maps Administrator → `all` and GameMaster → `support2` — the widest of the
shipped non-developer roles, carrying the moderation set without the `Developer` bit that gates the ~85 debug
commands.

## The command catalog

133 commands, all registered by one function (`FUN_0064e2d0`) as 12-byte records — `[0] vftable, [4] 0,
[8] requiredMask` — pushed into the interpreter's list. Recovered with
`tools/ghidra/DumpCommandRegistry.java`; re-run it to regenerate this table. The raw dump, with each command's
implementing class and its registration address, is [`docs/re/gm-command-registry.tsv`](../re/gm-command-registry.tsv).

| privilege | bit | commands |
|---|---|---|
| `(unrestricted)` | `0x00000000` | `/system` |
| `Developer` | `0x00000001` | `/addbugcc` `/ai` `/andersdebug` `/banish` `/bccaction` `/bccself` `/brtest` `/cars` `/chaincast` `/char` `/clearcooldown` `/combo` `/commandqueue` `/consumememory` `/criteriatester` `/datamining` `/dataset` `/dbdebug` `/disablenpcautoreset` `/fakeevent` `/formation` `/fraglimit` `/gametime` `/gametweaks` `/getfaction` `/ghost` `/hatedbylist` `/hugomtest` `/jumpspeed` `/lazyreload` `/localcache` `/locktemplate` `/lua` `/luas` `/massive` `/MaterialInfo` `/memoryprofile` `/mentor` `/missiontimeout` `/mobs` `/morphvalues` `/mousetarget` `/move` `/net` `/normalset` `/npcsleep` `/observer` `/openurl` `/pet` `/pupil` `/quest` `/questmarkeron` `/RaidFinderRemove` `/reload` `/resetplayfield` `/resurrectionpoint` `/ruictest` `/save` `/scriptlist` `/setcontrolmode` `/setfaction` `/setlookspackage` `/setregionscript` `/setregionvar` `/settime` `/settimescale` `/showclassstatadjustments` `/showerrors` `/simpledynel` `/simpledyneldebug` `/simulatenpc` `/space` `/spawnacgoutdoorentrance` `/spawnpoints` `/substate` `/syncdisplay` `/tacticalmap` `/teleportcity` `/time` `/togglespellrecharge` `/tracescripts` `/tradeskill` `/update` |
| `TeleportSelf` | `0x00000004` | `/teleport` `/teleportself` `/tp` |
| `TeleportDynels` | `0x00000008` | `/grantress` `/playfieldinfo` `/playfieldinstancelock` `/reportcharoffline` `/resetacg` `/softreset` `/teleporttarget` `/unsummon` `/whereis` |
| `PlayerDamage` | `0x00000010` | `/areaknockback` `/falldamage` `/inflict` `/knockback` |
| `GetStats` | `0x00000020` | `/distance` `/feat` `/get` `/gethash` `/getscriptgroup` `/getskill` |
| `SetStats` | `0x00000040` | `/cash` `/feats` `/jumpheight` `/level` `/magic` `/pvplevel` `/set` `/setlocal` `/setscriptgroup` `/token` |
| `Broadcast` | `0x00000400` | `/broadcast` `/servcast` |
| `Sitecast` | `0x00000800` | `/sitecast` |
| `ACGspawn` | `0x00001000` | `/inventory` `/resurrect` `/spawn` `/spawnquest` |
| `AssistSummon` | `0x00002000` | `/assist` `/summon` |
| `ChangeName` | `0x00004000` | `/setname` |
| `ChangeNick` | `0x00008000` | `/setnick` |
| `NPCCommands` | `0x00080000` | `/kill` `/npc` `/respawn` |
| `CanCloak` | `0x00100000` | `/cloak` `/gasubscribe` |
| `CanKick` | `0x00400000` | `/kickban` |

`/gm` itself is `FanaticGMLevelCommand_c`, registered ahead of all of these and **without** a permission record — so
it is available to anyone. That is what makes it the way in: it is the command that reads and sets the mask.

## What the client sends

Executing a GM command emits `FanaticCommandIIR_t` (`0xEE4AA85C`) — `[u32 command][u32 targetA][u32 targetB][string
argument]`, decoded from its `ReadSubClass` (`FUN_00663860`) and parsed by `FanaticCommandCodec`. ⚠️ The server's
REPLY is still undecoded: no capture in the corpus contains one, because the retail traces are ordinary play rather
than GM sessions. The client's own `Fanatic` debug channel (level 2) logs its view of the exchange
(`--debug-channels Fanatic`).

## The two other `player_stats` GM fields

Both are in the protobuf schema, both unset by us, and both now have a meaning:

- **`gm_ldb_name`** (field 6) — the LDB string id of the tag rendered over a GM's head. Paired with the `ShowGMTag`
  privilege (`0x00010000`).
- **`gm_cloak_level`** (field 7) — the cloak degree. Paired with `CanCloak` (`0x00100000`) and the `/cloak` command,
  whose own help text is *"Turns on/off cloaking. off turns it off, everyting else turns it on"*.

Their VALUE semantics are not decoded — what LDB category `gm_ldb_name` indexes, and what the cloak level's magnitude
means, would be guesses. Open work: Gitea #449.

## Flying

`DebugToggleFlyingMode` is **not** a config key and not a chat command (the #448 backlog listed it as a candidate
UniversalConfig key — that is now settled). It is an input ACTION, bound in the client's own
`Data/Gui/Default/Hotkeys.xml` to **ALT + F7**, labelled "Toggle Character Flyingmode". Its whole group is GM-gated by
the same mask:

```xml
<HotKeyGroup label="GM/Debug" gmlevel="-1">
  ...
  <HotKey id="DebugToggleFlyingMode" key1="ALT + F7" label="Toggle Character Flyingmode"/>
```

`gmlevel="-1"` is `0xFFFFFFFF` — under the `&`-test that reads as "any GM bit at all", so a non-zero `gm_level`
enables the group. It is the only group in the file that carries the attribute.

### The chat command that actually flies: `/luke`

ALT+F7 is not the only way in, and it is not the reliable one — the whole GM/Debug group is a *debug* input path.
The client also ships a plain chat command for it, `SkywalkingCommand`, and it is **entirely client-local**: its
execute (`FUN_013f6130`) sends no packet at all, so **it works against our server today with no server support**.

The verb is **`/luke`** — Luke *Skywalker*. **Confirmed working in-client 2026-07-29.** It is an LDB string, which is why grepping the exe for "fly" or
"skywalk" never finds it: `SkywalkingCommand_Name` ELF-hashes to id 47110773 in category 10002, whose value is
`luke` (see [`../rdb/ldb-string-index.md`](../rdb/ldb-string-index.md) for the hash rule). Its description, id
97813470, reads *"This command enables skywalking."*, and it reports `Skywalking enabled` / `Skywalking disabled`.

It is a **toggle**, and what it toggles is client-side movement state, not anything on the wire:

```c
obj = <local player>;
toggle = (obj->[0x1BC] == 0);      // the flying flag
obj->[0x1BC] = toggle;
if (toggle) { SetAlignmentMode(obj->[0x100]); feedback("SkywalkingEnabled"); }
else        { if (mode == 5) SetAlignmentMode(obj->[0x100]); feedback("SkywalkingDisabled"); }
```

Flying is **alignment mode 5** in the client's movement state machine (`vehiclealignment.cpp`; the factory
`FUN_00a3cbc0` maps 0=Normal 1=Climbing 2=Dead 3=Diving 4=Falling **5=Flying** 6=Knockback 7=Swimming). Nothing
about it rides `CharDCMove`, whose MovingType is a layout bitfield with no movement-mode enum
([`../re/chardcmove-grammar.md`](../re/chardcmove-grammar.md)) — so there is no server-side "make them fly" field,
only the client's own state.

The one server-side lever the client exposes is the GameAction `e_Type_GC_GameAction_StartFlymode`
(`0xCF92`), which the `.fly` GM command sends; that path is **unverified** (its parameter list lives in a
runtime-filled descriptor table). `/luke` is the route that is decoded end to end and confirmed working.

⚠️ `.fly` first shipped with the id `0xD12D`, which is outside the range the client asserts
(`0xCF09..0xD026`, `SpellData.cpp:1577`) and **crashed the client**. See
[`../re/gameactionlist-grammar.md`](../re/gameactionlist-grammar.md) for the stride trap behind it.

### The rest of the GM/Debug group

All 18 entries, every one enabled by a non-zero `gm_level`:

| key | action | what it is |
|---|---|---|
| `CTRL + ALT + V` | `Debug_ToggleCameramode` | **Godcam** — detached free camera |
| `ALT + F7` | `DebugToggleFlyingMode` | the flying toggle (see `/luke` above) |
| `CTRL + ALT + X` | `Debug_MouseWorldPos` | world position under the cursor |
| `SHIFT + O` | `Debug_CopyMeshID` | copies the mesh id of what you're looking at |
| `SHIFT + CTRL + F7` | `Open_TweakGUIWindow` | the **tweak-table GUI** (RDB 1000516) |
| `CTRL + ALT + F2` | `Debug_LoadTweak` | reload the tweak file |
| `SHIFT + CTRL + F8` | `Open_ServerStatWindow` | server stat window |
| `CTRL + ALT + D` | `Toggle_DamageMeterWindow` | damage meter |
| `ALT + SHIFT + R` | `ToggleResManWindow` | resource-manager window |
| `ALT + SHIFT + S` | `ToggleSoundWindow` | sound window |
| `CTRL + ALT + P` | `Toggle_PerformanceLogger` | performance logger |
| `F10` | `Toggle_Options` | options |
| `ALT + ENTER` | `ToggleFullscreen` | fullscreen |
| `ALT+SHIFT+CTRL+1..5` | `Debug_CrashConan1..5` | ⚠️ deliberate crashes — do not press |

### Registered input actions with no default binding

The client registers **190** input actions but `Hotkeys.xml` binds only 163. The 28 unbound ones are reachable by
adding a `<HotKey>` entry for them, and several are real features rather than debug leftovers:
`Open_TradeSkillWindow`, `Toggle_PvPStatWindow`, `Toggle_QuestSharingWindow`, `Toggle_SP_Inventory`,
`ShowMagicCone`, `Use_Perception`, `LookAtTarget`, `CycleVicinityTagModes`, `SwitchLayout`, `TakePrintScreenShot`,
`Movement_ToggleSitStand`, `NextFriendlyTarget` / `PrevFriendlyTarget` / `NextHostileTarget` / `PrevHostileTarget`,
`Combat_TabWeapons` / `Combat_TabSpellFriendly` / `Combat_TabSpellHostile`, `SelectTeammember7..10`,
`Action_Reload`, `Debug_ToggleCPUAffinity`, `Use_Perception`, and the mouse-button pseudo-actions.
(`ToggleControlCenter__UNUSED` names itself.)

## Reproducing

```zsh
GH=<data-repo>/Ghidra
# the privilege bits + the shipped roles (FanaticGMLevelCommand_c's constructor)
CLASSRX='^FanaticGMLevelCommand.*::vftable$' /opt/ghidra/support/analyzeHeadless "$GH" aoc \
  -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpFanaticCommands.java
# the 133-command catalog with each command's required mask
/opt/ghidra/support/analyzeHeadless "$GH" aoc -process AgeOfConan.exe -noanalysis \
  -scriptPath tools/ghidra -postScript DumpCommandRegistry.java
# the /help filter that ANDs them
FNS=006c86b0 /opt/ghidra/support/analyzeHeadless "$GH" aoc -process AgeOfConan.exe -noanalysis \
  -scriptPath tools/ghidra -postScript DecompileFns.java
```
