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
enables the group. It is the only group in the file that carries the attribute. Also in it: `Debug_ToggleCameramode`
(CTRL+ALT+V, "Toggle Godcam"), `Debug_MouseWorldPos` (CTRL+ALT+X) and `Debug_LoadTweak` (CTRL+ALT+F2).

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
