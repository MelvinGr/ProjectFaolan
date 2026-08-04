# Mounts

Riding is two halves that come from two different sources, and keeping them apart is the whole design:

- **Which mounts exist** is the client's own data. RDB type 1040005 (the spell catalog) field **186** is
  `Mount` — the client's name for stat `0x0BA`, the same id the ride buff pushes as the mounted-state stat.
  It occurs on **155** spell records, and every one of them is a mount summon. Those 155 rows are the
  `Mounts` table. [V1: Faolan.Core.Database/Tables/Mount.cs]
- **What riding looks like on the wire** is capture-only. Nothing in the RDB states the summon channel, the
  ride buff, its payload or the dismount; those come from the NecroL80 mount captures and live in
  `MountCatalog`. [V2: NecroL80_mage-necromancer_login_mount_move_dismount.tshark.json]

## The catalog

A mount's identity is its **summon spell**: the `from` word of the client's `SummonMount` behavior action IS
the spell record's id. `0x3E34F1` is spell **4076785**, which the RDB independently names *Fading Black Horse*
and whose field 186 is **3226779** — exactly the `0x00313C9B` retail put in the mounted-state `StatUpd`. The
capture and the client's data arrive at the same number from two directions, which is what makes the import a
mirror of retail rather than a guess. [V3: MountCatalogTests.FadingBlackHorse_IsInTheShippedCatalog_WithItsCapturedCreatureId]

So `Mount.Id` is an FK to `Spells` (the name comes from the spell row) and `Mount.CreatureId` is field 186.
The creature id is **not** unique and must never be the key: two *Killer Rhino* summons name one creature, and
three *Ridden Death* summons name three different ones.
[V3: MountCatalogTests.TheCatalog_IsTheWholeClientList_KeyedByTheSummonSpell]

Four rows are the game's own dev markers (`XX Test Bear Mount 3`, `XX Putharjath Red`, …). They are kept
rather than filtered: their records are named and well-formed, so dropping them would be our rule, not the
client's, and no client ever sends their id.

## There is no mount SPEED field

`MountSpeed` (stat `0x0BB`) is a flat **100** because the client's mount record has nowhere else for it to
come from. Comparing the three variants of one horse settles it — every field of *Fading Black Horse*,
*Swift Fading Black Horse* and *Azure-barded Fading Black Horse* is identical bar the icon, the creature id
and one field:

| field | Fading Black | Swift Fading Black | Azure-barded Fading Black |
|---|---|---|---|
| 54 `Level` | 1 | 1 | 1 |
| 79 `Icon` | 1136630 | 4062994 | 4062993 |
| 128 (line) | 44224 | 44224 | 44224 |
| **186 `Mount`** | **3226779** | **3226783** | **1313207** |
| 188 | 4 | 4 | 4 |
| 203 / 204 | 1 / 1 | 1 / 1 | 1 / 1 |
| 294 `CastingTimeSeconds` | 700 | 700 | 700 |
| **499** | — | **2** | — |

Field 499 is the **quality/rarity tier** — the same field, on the same 0-indexed `ItemQuality` ladder, that
an item template carries and `RdbItemImporter` already reads. Across the whole set it reads as rarity and not
as speed: nothing at all for the level-40 starter horses, `2 Enchanted` for the Swift horses / Killer Rhino /
War Mammoth / Ice Bear, `3 Rare` for the war wolves and Hyrkanian mounts, `4 Epic` for the Yothian War-Mares
and Vaaghasan Slaughter Steeds, `5 Legendary` for Blood of Zath and Abomination of Selkhet. `1 Superior`
never occurs — the same sparse use of the ladder gear shows.
[V3: MountCatalogTests.Quality_IsTheOnlyVariantDiscriminator_AndIsARarityLadder]

Deriving a speed from a rarity tier would be an invention, and there is not even a baseline to scale from:
the one mount a capture pins is the *tierless* Fading Black Horse, and it is already at 100.

Reproduce the comparison with:

```zsh
dotnet run --project Faolan.Importer -- --report spell-fields --report-arg 4076785 --RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:186x499 --RdbDir <rdb-dir>
```

## The wire sequence

Mounting is a **server-timed channel**; dismounting is **client-driven**. Both directions start with the same
`SummonMount` press — it toggles.

| | mount | dismount |
|---|---|---|
| begin | behavior echo-ack (cast bar + horse runs up) | behavior echo-ack |
| then | after **545 ms**: complete echo-ack | immediately: complete echo-ack |
| buff | ride buff `0x31EB1500`, 24 h | end twin `0x31EB1501`, empty payload |
| stats | `Mount` = the creature id, `MountSpeed` = 100 | `Mount` = 0 |

Retail sends **no summon `ApplySpell` and spawns no horse entity** — the client animates the whole run-up and
mount from the ride buff plus the `Mount` stat. The 545 ms is measured (summon frame 498 → ride buff frame
520); applying the buff instantly mounts with no visible run-up.
[V2: NecroL80_mage-necromancer_login_mount_move_dismount.tshark.json]

The ride buff's payload is the 54-byte `MagicBuffIIR` apply form, constructed rather than replayed; its `@46`
expiry stamp is session state (server uptime ms), not a catalog constant.
[V3: MountCatalogTests.RideBuffPayload_ReproducesTheCapturedFrame520Blob]

The mount is persisted on the character and re-seated on world entry into a mount-allowed playfield, as a
fresh buff apply — retail's entry-restore record is byte-identical to a player-initiated one bar the effect
id and nonce, so there is no "silent" variant to reconstruct inside the snapshot.
[V2: NecroL80_mage-necromancer_login_leave_king_conan_castle-Dismount.tshark.json]

## Mounted combat

The client's data says a surprising amount about this, and all of it points the same way: **the saddle gate is
a lance charge and nothing else**, and **no magnitude for a charge exists anywhere in the client**.

**The gate.** Exactly two of the client's 260 GameAction types ask whether they may be used from the saddle —
`Charge` (type 53009) and `ChargeSpecialAbility` (53253), both carrying key index 245 →
`0x4FD GameAction_CanUseIfMounted`. There is no general "usable while mounted" property on any other action, so
the client never asks the question of an ordinary cast. [V1: docs/re/gameaction-keylists.tsv]

**What carries it.** `ChargeSpecialAbility` occurs **zero** times in the shipped catalog. `Charge` occurs on
**16** of the 21,502 spell records — and exactly **one** of them sets the flag:

```
4707261 Tiger Charge   187=0        248=0 245=1 243=1802 254=1801
4267183 Charge         187=4267326  248=0 245=0 243=1725 254=1726
5035719 Charge         187=5035720  248=0 245=0 243=203  254=119
… 13 more, all 245=0
```

`Tiger Charge` is also the only one whose `GameAction_TemplateID` is 0. Its ability twin is
`Mount Ability: Tiger Charge` (4707293), an adjacent id.

**The abilities exist and are already granted.** The catalog holds 14 `Mount Ability: …` rows, and six of them
carry real captured grant keys and bar slots — so retail's own `AddSpecialAbility` grant for them was observed:

| slot | ability |
|---|---|
| 269 | Mount Ability: Mammoth Charge |
| 270 | Mount Ability: Mammoth Sweep |
| 273 | Mount Ability: Cavalry Charge |
| 274 | Mount Ability: Trample |
| 275 | Mount Ability: Hind Leg Kick |
| 284 | Mount Ability: Quick Dismount |

The other eight (Rhino Charge/Sweep, Tiger Charge, Equestrian Bow/Dance, Maul, Venomous Spit, a second Quick
Dismount) are LDB-identity only — no capture ever granted them, and a grant needs the per-ability TEA key,
which is random.

Those six ids appear in the **login** burst of many unrelated captures, not in the three mount captures. So
retail grants the mount abilities like every other ability, at world entry, and the client itself decides
whether the button is usable — which is what `GameAction_CanUseIfMounted` is for. Our server already sends
them on the same path as every other ability; nothing mounted-specific is needed to put them on the bar.

⚠️ They currently sit in `ClassAbilities` under Necromancer and Demonologist at level 80 because those are the
captured characters. That is a **capture artifact**, not a rule the data states — a mount ability comes with
the mount, not the class. Widening it is the class-aware ability coverage problem, not mounted combat.

**What is missing is a capture, not a decode.** The `Charge` element carries only `TemplateID`, `TemplateID2`,
`CanUseIfMounted`, `BccEvent` and `PoseEvent` — a presentation action. There is no damage, no knockback, no
radius on it, none of the mount-ability ids has a spell record at all, and none of the 16 charge spells carries
a `[2][9]` stat-effect region. So a charge's magnitude is server-GDB-only, exactly like ordinary spell
magnitudes (`stat-engine.md`), and no capture in the corpus shows a mounted character fighting. Implementing
trample damage or a charge knockback today would mean inventing both the numbers and the packets.

Open work tracked in Gitea #142.

## Where the pieces live

| | |
|---|---|
| the catalog rows | `Mount` (`Mounts`), imported by `RdbSpellImporter` in the `rdb-spells` module |
| lookups | `IDatabaseRepository.GetMount` / `GetMounts` |
| the protocol constants | `MountCatalog` — channel, ride/dismount spell ids, `MountSpeed`, the buff payload |
| the summon/dismount handler | `GameServerListener.Inbound.cs`, `BehaviorId.Mount` |
| the entry re-seat | `GameServerListener.WorldEntry.cs`, `RestoreMount` |
| the mount creature model | `CaptureMountImporter` lifts it onto an `NpcTemplate` from the captures |

A **known cosmetic** during dismount: the character briefly dips into the floor and re-grounds at animation
end. Confirmed not server-side — retail sends nothing during the dismount window beyond the end twin and the
`StatUpd`, and both immediate and timer-deferred dismount show it.
