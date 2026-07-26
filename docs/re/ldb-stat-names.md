# The client's stat-name table (LDB category 615)

The client ships a table that names every wire STAT ID: LDB (RDB type 1030002) **category 615**, an
`id -> name` map keyed by the same id space as the `0x96B8DC59` vitals block, the `0x737A6DF9` snapshot stat
sheet, the item `[2][9]` modifier region and the spell stat effects. It is what let the remaining
`CharacterStatId.Unknown##` slots be named from the client rather than guessed.

Regenerate (and re-run the cross-check against `CharacterStatId`) with:

```zsh
dotnet run --project Faolan.Importer -- --report ldb-stat-names --RdbDir <rdb-dir>
```

## Reading it

- **The category is LOCALIZED**, like every other LDB category: 7 pages of the same 891 ids, one per shipped
  language. The English page is CRIBBED, never picked by order - scored against the names `CharacterStatId`
  already carried (English matches 80, every other language 12-15).
- **Not every stat id is in it.** 891 ids are named; the slots the table skips (e.g. `0x534`, `0x163`,
  `0x0F3`) stay `Unknown##` in the enum rather than getting an invented name.
- The ids above `0x2000` in the raw table are not character stats (they are the client's internal effect/tap
  ids, e.g. `0xFF5C910 Physical Stamina Tap`) and are left out of the table below.
- A few names are Anarchy Online leftovers (`Nano Interval`, `Awarded Omni-Tek Tokens`, `Memorize Delay`) -
  dead slots in Conan, named as the client names them.

## Cross-check against `CharacterStatId`

The report ends with a diff of our enum against this table. Everything we had named the hard way (from
tooltips, Ghidra, or `.setstat` probing) agrees; the entries it lists as differing are deliberate:

- `HealthCurrent`/`HealthMax`, `StaminaCurrent`/`StaminaMax`, `ManaCurrent`/`ManaMax` - the table calls both
  halves of each pair just "Health"/"Stamina"/"Mana"; our names keep the current/max distinction that was
  proven in-client with `.setstat`.
- `Exp`/`MaxExp`/`ExpCurrentLevelBase` vs "XP"/"Next XP"/"Last XP", `KillingBlows` vs "Kills",
  `CombatMode` vs "BCC State", `PlayfieldToken` vs "Last Concrete Playfield Instance" - same slot, our name
  says what the server uses it for.

## The table

| id | hex | name |
| --- | --- | --- |
| 0 | 0x0000 | Flags |
| 1 | 0x0001 | Health |
| 2 | 0x0002 | Volume Mass |
| 3 | 0x0003 | Attack Speed |
| 5 | 0x0005 | Clan |
| 6 | 0x0006 | Team |
| 7 | 0x0007 | State |
| 8 | 0x0008 | Duration |
| 9 | 0x0009 | Map Flags |
| 11 | 0x000B | Previous Health |
| 12 | 0x000C | Mesh |
| 13 | 0x000D | Anim |
| 14 | 0x000E | Name |
| 15 | 0x000F | Info |
| 16 | 0x0010 | Amount |
| 17 | 0x0011 | Memorization Slot |
| 18 | 0x0012 | Session Timer |
| 19 | 0x0013 | Feat Trainer ID |
| 20 | 0x0014 | BCC State |
| 21 | 0x0015 | BCC |
| 22 | 0x0016 | AMS |
| 23 | 0x0017 | Static Instance |
| 24 | 0x0018 | Max Mass |
| 25 | 0x0019 | Static Type |
| 26 | 0x001A | Stack Size |
| 27 | 0x001B | Health |
| 28 | 0x001C | Height |
| 29 | 0x001D | DMS |
| 30 | 0x001E | Can |
| 31 | 0x001F | Archetype |
| 32 | 0x0020 | Divine Favor |
| 33 | 0x0021 | Side |
| 34 | 0x0022 | Dead Timer |
| 35 | 0x0023 | Access Count |
| 36 | 0x0024 | Attack Count |
| 37 | 0x0025 | Sprinting Stamina Drain |
| 38 | 0x0026 | Back Mesh |
| 39 | 0x0027 | Shoulder Mesh |
| 40 | 0x0028 | Favor Value |
| 41 | 0x0029 | Receiver Material |
| 42 | 0x002A | CAT Mesh |
| 43 | 0x002B | Parent Type |
| 44 | 0x002C | Parent Instance |
| 45 | 0x002D | Belt Slots |
| 46 | 0x002E | Bandolier Slots |
| 47 | 0x002F | Hair Render Type |
| 48 | 0x0030 | Organization rank |
| 49 | 0x0031 | Spell Damage Coefficient |
| 50 | 0x0032 | Inventory Timeout |
| 51 | 0x0033 | PvP XP |
| 52 | 0x0034 | XP |
| 53 | 0x0035 | Fame Point Cost |
| 54 | 0x0036 | Level |
| 55 | 0x0037 | Inventory Id |
| 56 | 0x0038 | Time Since Creation |
| 57 | 0x0039 | Last XP |
| 58 | 0x003A | Played |
| 59 | 0x003B | Sex |
| 61 | 0x003D | Money |
| 62 | 0x003E | Alignment |
| 63 | 0x003F | Attitude |
| 64 | 0x0040 | Head Mesh |
| 65 | 0x0041 | Recast Time |
| 67 | 0x0043 | Class |
| 68 | 0x0044 | Faction Group ID |
| 69 | 0x0045 | Chat ID |
| 70 | 0x0046 | PVP Level |
| 71 | 0x0047 | Total Mass |
| 72 | 0x0048 | Item Type |
| 73 | 0x0049 | RepairDifficulty |
| 74 | 0x004A | Price |
| 75 | 0x004B | Awarded Omni-Tek Tokens |
| 76 | 0x004C | Item Class |
| 77 | 0x004D | Repair Skill |
| 78 | 0x004E | Current Mass |
| 79 | 0x004F | Icon |
| 80 | 0x0050 | Primary Item Type |
| 81 | 0x0051 | Primary Item Instance |
| 82 | 0x0052 | Secondary Item Type |
| 83 | 0x0053 | Secondary Item Instance |
| 84 | 0x0054 | User Type |
| 85 | 0x0055 | User Instance |
| 86 | 0x0056 | Area Type |
| 87 | 0x0057 | Area Instance |
| 88 | 0x0058 | Default Position |
| 89 | 0x0059 | Race |
| 90 | 0x005A | Magic Rating (Frost) |
| 91 | 0x005B | Elder Frost Specialization |
| 92 | 0x005C | Guild ID |
| 93 | 0x005D | NPC Title |
| 95 | 0x005F | Cold AC |
| 96 | 0x0060 | Poison AC |
| 97 | 0x0061 | Fire AC |
| 98 | 0x0062 | State Action |
| 99 | 0x0063 | Item Anim |
| 100 | 0x0064 | Respec Points |
| 101 | 0x0065 | Bleeding |
| 102 | 0x0066 | Bleeding Chance |
| 103 | 0x0067 | Bleeding Damage |
| 104 | 0x0068 | Extra Inventory |
| 105 | 0x0069 | Last Kill Time |
| 106 | 0x006A | Last Kill Number |
| 107 | 0x006B | Stun Chance |
| 108 | 0x006C | Stun Duration |
| 109 | 0x006D | Max Slow Limit |
| 110 | 0x006E | Berserk Percent Level |
| 112 | 0x0070 | NPC Template Instance |
| 113 | 0x0071 | Gem Size |
| 114 | 0x0072 | Gem Slots |
| 115 | 0x0073 | Gem Slot 1 |
| 116 | 0x0074 | Gem Slot 2 |
| 117 | 0x0075 | Gem Slot 3 |
| 118 | 0x0076 | Gem Slot 4 |
| 120 | 0x0078 | Money |
| 121 | 0x0079 | Super Family |
| 122 | 0x007A | Max Memorization Slots |
| 123 | 0x007B | GUI Stacking Order |
| 124 | 0x007C | AI Spell Category |
| 125 | 0x007D | AI Powerrating |
| 126 | 0x007E | Targeting Mode |
| 127 | 0x007F | Memorize Delay |
| 128 | 0x0080 | GUI Stacking Line |
| 129 | 0x0081 | Spell Critical Damage |
| 130 | 0x0082 | Intoxication Level |
| 131 | 0x0083 | Max Spell Weave |
| 132 | 0x0084 | Mana Cost |
| 133 | 0x0085 | Entrance Playfield |
| 134 | 0x0086 | Barter |
| 135 | 0x0087 | Max Barter |
| 136 | 0x0088 | Craftmanship |
| 137 | 0x0089 | Max Craftmanship |
| 138 | 0x008A | Disruptive |
| 139 | 0x008B | Max Disruptive |
| 140 | 0x008C | Resilence |
| 141 | 0x008D | Max Resilence |
| 142 | 0x008E | Taunt |
| 143 | 0x008F | Max Taunt |
| 144 | 0x0090 | Soul Fragments |
| 146 | 0x0092 | Respawn Time |
| 147 | 0x0093 | Spell Critical Chance |
| 148 | 0x0094 | Occupy Slots |
| 149 | 0x0095 | Available Slots |
| 150 | 0x0096 | Casting Level |
| 151 | 0x0097 | Casting Level Modifier |
| 152 | 0x0098 | Penetration Rating |
| 153 | 0x0099 | Interruption Reduction |
| 156 | 0x009C | Chance of Being Staggered |
| 157 | 0x009D | Protection (Cold) |
| 158 | 0x009E | Magic Damage (Cold) |
| 159 | 0x009F | Pharmaceuticals |
| 160 | 0x00A0 | Cold Damage (ranged) |
| 161 | 0x00A1 | Cold Damage |
| 162 | 0x00A2 | Combat Rating (Cold) |
| 163 | 0x00A3 | Percent Frost Damage |
| 164 | 0x00A4 | Brawl Wins |
| 165 | 0x00A5 | Shield Allocation |
| 166 | 0x00A6 | Track Frost Damage |
| 167 | 0x00A7 | Cold Invulnerability |
| 168 | 0x00A8 | Attackable |
| 169 | 0x00A9 | ChosenGod |
| 170 | 0x00AA | Hate Modifier |
| 173 | 0x00AD | Current Movement Mode |
| 174 | 0x00AE | Previous Movement Mode |
| 175 | 0x00AF | Autolock Time Default |
| 176 | 0x00B0 | Auto Unlock Time Default |
| 177 | 0x00B1 | More Flags |
| 178 | 0x00B2 | Fame Points |
| 179 | 0x00B3 | NPC Flags |
| 180 | 0x00B4 | Current Buff Slots |
| 181 | 0x00B5 | Max Buff Slots |
| 182 | 0x00B6 | Specialization |
| 183 | 0x00B7 | EffectIcon |
| 184 | 0x00B8 | Building Type |
| 185 | 0x00B9 | Building Instance |
| 189 | 0x00BD | Exit Instance |
| 190 | 0x00BE | Next Door In Building |
| 191 | 0x00BF | Last Concrete Playfield Instance |
| 192 | 0x00C0 | External Playfield Instance |
| 193 | 0x00C1 | External Door Instance |
| 194 | 0x00C2 | In Play |
| 197 | 0x00C5 | Orientation Mode |
| 198 | 0x00C6 | Session Time |
| 199 | 0x00C7 | RP |
| 200 | 0x00C8 | Brain |
| 201 | 0x00C9 | Unarmed Critical Damage |
| 202 | 0x00CA | Unarmed Damage Type |
| 203 | 0x00CB | Min. Level |
| 204 | 0x00CC | Max. Level |
| 205 | 0x00CD | Normalset |
| 206 | 0x00CE | Unarmed Min. Damage |
| 207 | 0x00CF | Unarmed Max. Damage |
| 208 | 0x00D0 | GM Disable Target System |
| 209 | 0x00D1 | Weapon Mesh |
| 210 | 0x00D2 | Recharge Delay |
| 211 | 0x00D3 | Equip Delay |
| 212 | 0x00D4 | Max Stack Size |
| 213 | 0x00D5 | Team Side |
| 214 | 0x00D6 | Nano Points |
| 215 | 0x00D7 | GM Level |
| 216 | 0x00D8 | PvP Attack Rating |
| 217 | 0x00D9 | PvP Magic Rating |
| 218 | 0x00DA | PvP Defence Rating |
| 219 | 0x00DB | PvP Resistance Rating |
| 220 | 0x00DC | Current Body Location |
| 222 | 0x00DE | Spell column radius |
| 223 | 0x00DF | Can Change Clothes |
| 224 | 0x00E0 | Features |
| 225 | 0x00E1 | PvP Combat Rating |
| 226 | 0x00E2 | PvP Magic Damage |
| 227 | 0x00E3 | Physical Health Tap |
| 228 | 0x00E4 | Slashing |
| 229 | 0x00E5 | Piercing |
| 230 | 0x00E6 | Crushing |
| 231 | 0x00E7 | Fire |
| 232 | 0x00E8 | Cold |
| 233 | 0x00E9 | Electrical |
| 234 | 0x00EA | Holy |
| 235 | 0x00EB | Berserk Mode |
| 236 | 0x00EC | Siege |
| 237 | 0x00ED | Change Sidecount |
| 238 | 0x00EE | Ride Skill |
| 240 | 0x00F0 | Movement Multiplier |
| 241 | 0x00F1 | Out of Combat Movement Speed |
| 242 | 0x00F2 | Sneaking movement bonus |
| 248 | 0x00F8 | Birthdate |
| 249 | 0x00F9 | Last Saved |
| 252 | 0x00FC | Miss Chance |
| 253 | 0x00FD | Miss Level Growth |
| 254 | 0x00FE | Evade Chance |
| 255 | 0x00FF | Evade Level Growth |
| 256 | 0x0100 | Mission Bits 1 |
| 257 | 0x0101 | Mission Bits 2 |
| 259 | 0x0103 | Door Flags |
| 260 | 0x0104 | Clan Hierarchy |
| 261 | 0x0105 | Quest Stat |
| 262 | 0x0106 | Client Activated |
| 263 | 0x0107 | Brawl 1 Weapon |
| 264 | 0x0108 | Brawl 2 Weapon |
| 265 | 0x0109 | Dimach Weapon |
| 266 | 0x010A | Sound Script |
| 267 | 0x010B | Riposte Weapon |
| 268 | 0x010C | Liquid Type |
| 269 | 0x010D | Gather Sound |
| 270 | 0x010E | Cast Sound |
| 271 | 0x010F | Travel Sound |
| 272 | 0x0110 | Hit Sound |
| 273 | 0x0111 | Secondary Item Template |
| 274 | 0x0112 | Equipped Weapons |
| 275 | 0x0113 | XP Kill Range |
| 276 | 0x0114 | Physical Mana Tap |
| 277 | 0x0115 | Physical Stamina Tap |
| 278 | 0x0116 | Non Combat Health Regen |
| 279 | 0x0117 | Non Combat Stamina Regen |
| 280 | 0x0118 | Non Combat Mana Regen |
| 281 | 0x0119 | Unholy |
| 282 | 0x011A | Poison |
| 283 | 0x011B | Item Hate Value |
| 284 | 0x011C | Damage Bonus |
| 285 | 0x011D | Max Damage |
| 286 | 0x011E | Min Damage |
| 287 | 0x011F | Attack Range |
| 288 | 0x0120 | Hate Value Modifyer |
| 289 | 0x0121 | Trap Difficulty |
| 290 | 0x0122 | Stat One |
| 291 | 0x0123 | Bow Range Modifier |
| 292 | 0x0124 | Crossbow Range Modifier |
| 293 | 0x0125 | Item Skill |
| 294 | 0x0126 | Casting Time (Seconds) |
| 295 | 0x0127 | Item Opposed Skill |
| 296 | 0x0128 | Item SIS |
| 297 | 0x0129 | Interaction Radius |
| 298 | 0x012A | Placement |
| 299 | 0x012B | Lock Difficulty |
| 300 | 0x012C | Members |
| 301 | 0x012D | Min Members |
| 302 | 0x012E | Prestige Class |
| 303 | 0x012F | Organization upkeep |
| 305 | 0x0131 | Bloodtype |
| 308 | 0x0134 | Evade Rating |
| 309 | 0x0135 | PvP Evade Rating |
| 310 | 0x0136 | Hit Rating |
| 311 | 0x0137 | PvP Hit Rating |
| 312 | 0x0138 | Critical Rating |
| 313 | 0x0139 | Guile |
| 314 | 0x013A | Max Guile |
| 316 | 0x013C | Variance |
| 318 | 0x013E | % Add. Nano Cost |
| 319 | 0x013F | XP Modifier |
| 320 | 0x0140 | Magic Variance |
| 324 | 0x0144 | Fizzle Chance |
| 325 | 0x0145 | Fizzle Growth |
| 326 | 0x0146 | Immunity |
| 329 | 0x0149 | Max Immunity |
| 330 | 0x014A | Received Healing Modifier |
| 332 | 0x014C | Debuff Formula |
| 333 | 0x014D | PvP_Rating |
| 334 | 0x014E | Bonus Protection |
| 335 | 0x014F | Door Block Time |
| 337 | 0x0151 | Heroism |
| 338 | 0x0152 | Death Reason |
| 339 | 0x0153 | Damage Override Type |
| 341 | 0x0155 | Bonus XP |
| 342 | 0x0156 | Heal Interval |
| 343 | 0x0157 | Heal Delta |
| 344 | 0x0158 | Monster Texture |
| 345 | 0x0159 | Has Always Lootable |
| 346 | 0x015A | Trade Limit |
| 347 | 0x015B | Splash Radius |
| 348 | 0x015C | Splash Damage |
| 349 | 0x015D | Splash damage max targets |
| 350 | 0x015E | Next XP |
| 351 | 0x015F | Teleport Pause Milliseconds |
| 352 | 0x0160 | SIS Cap |
| 354 | 0x0162 | Attack Type |
| 356 | 0x0164 | NPC Hash |
| 357 | 0x0165 | Collision Radius |
| 358 | 0x0166 | Outer Radius |
| 359 | 0x0167 | Monster Data |
| 360 | 0x0168 | Scale |
| 361 | 0x0169 | Hit Effect Type |
| 362 | 0x016A | Resurrect Dest |
| 363 | 0x016B | Nano Interval |
| 364 | 0x016C | Nano Delta |
| 365 | 0x016D | Reclaim Item |
| 366 | 0x016E | Gather Effect Type |
| 367 | 0x016F | Visual Breed |
| 369 | 0x0171 | Visual Sex |
| 370 | 0x0172 | Ritual Target Inst |
| 371 | 0x0173 | Skill Time On Selected Target |
| 373 | 0x0175 | Extended Time |
| 375 | 0x0177 | Fatality Chance |
| 376 | 0x0178 | Gather Abstract Anim |
| 377 | 0x0179 | Cast Target Abstract Anim |
| 378 | 0x017A | Cast Self Abstract Anim |
| 380 | 0x017C | Spell Fatality Chance |
| 381 | 0x017D | Range Inc. NF |
| 382 | 0x017E | Skill Lock Modifier |
| 384 | 0x0180 | ACG Entrance Styles |
| 385 | 0x0181 | Chance Of Break On Spell Attack |
| 386 | 0x0182 | Chance Of Break On Debuff |
| 387 | 0x0183 | Health Break Threshold |
| 389 | 0x0185 | Expansions |
| 390 | 0x0186 | Lowres Mesh |
| 392 | 0x0188 | Old Time Exist |
| 393 | 0x0189 | Resist Modifier |
| 394 | 0x018A | Chest Flags |
| 395 | 0x018B | Primary Template ID |
| 396 | 0x018C | Number Of Items |
| 397 | 0x018D | Selected Target Type |
| 398 | 0x018E | Corpse_Hash |
| 400 | 0x0190 | Rotation |
| 401 | 0x0191 | CAT Anim |
| 402 | 0x0192 | CAT Anim Flags |
| 403 | 0x0193 | Display CAT Anim |
| 404 | 0x0194 | Display CAT Mesh |
| 405 | 0x0195 | School |
| 406 | 0x0196 | Nano Speed |
| 407 | 0x0197 | Nano Points |
| 409 | 0x0199 | Emitter Material |
| 410 | 0x019A | Is Fighting Me |
| 411 | 0x019B | Voice set |
| 413 | 0x019D | Effect Type |
| 414 | 0x019E | Impact Effect Type |
| 415 | 0x019F | Corpse Type |
| 416 | 0x01A0 | Corpse Instance |
| 419 | 0x01A3 | Tracer Effect Type |
| 420 | 0x01A4 | Ammo Type |
| 421 | 0x01A5 | Char Radius |
| 422 | 0x01A6 | Chance Of Break On Attack |
| 423 | 0x01A7 | Current State |
| 424 | 0x01A8 | Armor Type |
| 425 | 0x01A9 | Rest Modifier |
| 426 | 0x01AA | Buy Modifier |
| 427 | 0x01AB | Sell Modifier |
| 428 | 0x01AC | Cast Effect Type |
| 430 | 0x01AE | Wait State |
| 431 | 0x01AF | Selected Target |
| 432 | 0x01B0 | Error Code |
| 433 | 0x01B1 | Owner Instance |
| 434 | 0x01B2 | Char State |
| 435 | 0x01B3 | Read Only |
| 436 | 0x01B4 | Damage Type |
| 437 | 0x01B5 | Collide Check Interval |
| 438 | 0x01B6 | Playfield Type |
| 439 | 0x01B7 | NPC Command |
| 440 | 0x01B8 | InitiativeType |
| 445 | 0x01BD | NPC Command Arg |
| 446 | 0x01BE | Name Template |
| 447 | 0x01BF | Channeling Interval |
| 448 | 0x01C0 | Armor |
| 449 | 0x01C1 | Armor Penetration |
| 450 | 0x01C2 | Bonus Armor |
| 451 | 0x01C3 | Protection |
| 452 | 0x01C4 | Spell Penetration |
| 454 | 0x01C6 | PvP Armor |
| 455 | 0x01C7 | Monster Type |
| 456 | 0x01C8 | Pet Points |
| 458 | 0x01CA | PvP Protection |
| 459 | 0x01CB | OD Min Size Add |
| 462 | 0x01CE | Enemy Damage |
| 463 | 0x01CF | OD Max Size Add |
| 464 | 0x01D0 | Duration Modifier |
| 465 | 0x01D1 | Pet Damage Coefficient |
| 466 | 0x01D2 | LOS Height |
| 470 | 0x01D6 | Map Options |
| 471 | 0x01D7 | Map Area |
| 472 | 0x01D8 | Map Area |
| 473 | 0x01D9 | Fixture Flags |
| 474 | 0x01DA | Fall Damage Resistance |
| 475 | 0x01DB | Fatality Rating |
| 476 | 0x01DC | Magic Life Tap Rating |
| 477 | 0x01DD | Health Tap Rating |
| 478 | 0x01DE | PvP Health Tap Rating |
| 479 | 0x01DF | PvP Magic Health Tap Rating |
| 480 | 0x01E0 | Magic Mana Tap Rating |
| 481 | 0x01E1 | Mana Tap Rating |
| 482 | 0x01E2 | PvP Mana Tap Rating |
| 483 | 0x01E3 | PvP Magic Mana Tap Rating |
| 484 | 0x01E4 | Proximity Range Indoors |
| 485 | 0x01E5 | Pet Req Val 1 |
| 486 | 0x01E6 | Pet Req Val 2 |
| 487 | 0x01E7 | Pet Req Val 3 |
| 488 | 0x01E8 | Hate Decrease Rating |
| 489 | 0x01E9 | Backstab |
| 490 | 0x01EA | Immunity Rating |
| 491 | 0x01EB | Hate Increase Rating |
| 492 | 0x01EC | Magic Stamina Tap Rating |
| 493 | 0x01ED | Stamina Tap Rating |
| 494 | 0x01EE | PvP Stamina Tap Rating |
| 495 | 0x01EF | PvP Magic Stamina Tap Rating |
| 497 | 0x01F1 | Weapon Bonus Electrical Damage |
| 498 | 0x01F2 | Natural Mana Regen |
| 499 | 0x01F3 | Power Level |
| 500 | 0x01F4 | Anim Pos |
| 501 | 0x01F5 | Anim Play |
| 502 | 0x01F6 | Anim Speed |
| 504 | 0x01F8 | Stamina |
| 505 | 0x01F9 | Stamina |
| 506 | 0x01FA | Mana |
| 507 | 0x01FB | Mana |
| 510 | 0x01FE | Quest Timeout |
| 516 | 0x0204 | Attack Shield |
| 517 | 0x0205 | Special Attack Shield |
| 519 | 0x0207 | NPC Use Fight Mode Regen Rate |
| 520 | 0x0208 | Random |
| 521 | 0x0209 | Social Status |
| 522 | 0x020A | Last Round |
| 523 | 0x020B | Item Delay Cap |
| 524 | 0x020C | Recharge Delay Cap |
| 525 | 0x020D | Percentage of remaining health |
| 526 | 0x020E | Percent Remaining Nano |
| 527 | 0x020F | Target Distance |
| 528 | 0x0210 | Team Closeness |
| 530 | 0x0212 | Condition State |
| 531 | 0x0213 | Expansion Playfield |
| 533 | 0x0215 | Magical Health Tap |
| 534 | 0x0216 | Dud Chance |
| 535 | 0x0217 | Healing Efficiency |
| 536 | 0x0218 | Magic Damage Modifier |
| 537 | 0x0219 | Magic Vulnerability |
| 538 | 0x021A | Ams Cap |
| 539 | 0x021B | Proc Initiative 1 |
| 540 | 0x021C | Proc Initiative 2 |
| 541 | 0x021D | Proc Initiative 3 |
| 542 | 0x021E | Proc Initiative 4 |
| 543 | 0x021F | Faction Modifier |
| 544 | 0x0220 | Magical Mana Tap |
| 545 | 0x0221 | Magical Stamina Tap |
| 546 | 0x0222 | Stacking Line 2 |
| 547 | 0x0223 | Stacking Line 3 |
| 548 | 0x0224 | Stacking Line 4 |
| 549 | 0x0225 | Stacking Line 5 |
| 550 | 0x0226 | Stacking Line 6 |
| 551 | 0x0227 | Stacking Order |
| 552 | 0x0228 | Proc Nano 1 |
| 553 | 0x0229 | Proc Nano 2 |
| 554 | 0x022A | Proc Nano 3 |
| 555 | 0x022B | Proc Nano 4 |
| 556 | 0x022C | Proc Chance 1 |
| 557 | 0x022D | Proc Chance 2 |
| 558 | 0x022E | Proc Chance 3 |
| 559 | 0x022F | Proc Chance 4 |
| 560 | 0x0230 | Magic Health Tap |
| 561 | 0x0231 | Magic Mana Tap |
| 562 | 0x0232 | Magic Stamina Tap |
| 573 | 0x023D | Drunkness |
| 574 | 0x023E | Conciousness |
| 575 | 0x023F | Bliss |
| 576 | 0x0240 | Player Options |
| 577 | 0x0241 | Feat Resets |
| 578 | 0x0242 | Current Time |
| 579 | 0x0243 | Focus |
| 580 | 0x0244 | NPC Vicinity Family |
| 581 | 0x0245 | Brawling |
| 582 | 0x0246 | Apartments Allowed |
| 583 | 0x0247 | Apartments Owned |
| 584 | 0x0248 | Apartment Access Card |
| 585 | 0x0249 | Map Area |
| 586 | 0x024A | Map Area |
| 587 | 0x024B | Number of team members |
| 588 | 0x024C | Action Category |
| 589 | 0x024D | Current Playfield |
| 590 | 0x024E | District Nano |
| 591 | 0x024F | District Nano Interval |
| 592 | 0x0250 | Unsaved XP |
| 593 | 0x0251 | Regain XP Percentage |
| 594 | 0x0252 | Temp Save Team ID |
| 595 | 0x0253 | Temp Save Playfield |
| 596 | 0x0254 | Temp Save X |
| 597 | 0x0255 | Temp Save Y |
| 598 | 0x0256 | Extended Flags |
| 599 | 0x0257 | Price |
| 606 | 0x025E | Max Shop Items |
| 607 | 0x025F | Player ID |
| 608 | 0x0260 | Shop Rent |
| 609 | 0x0261 | Synergy Hash |
| 610 | 0x0262 | Shop Flags |
| 611 | 0x0263 | Shop Last Used |
| 612 | 0x0264 | Shop Type |
| 613 | 0x0265 | Lock Down Time |
| 614 | 0x0266 | Leader Lock Down Time |
| 616 | 0x0268 | Swaying |
| 620 | 0x026C | House Template |
| 638 | 0x027E | NPC Game Action Arg 1 |
| 639 | 0x027F | NPC Game Action Ret 1 |
| 640 | 0x0280 | City Instance |
| 641 | 0x0281 | Distance To Spawnpoint |
| 642 | 0x0282 | City Terminal Recharge Percent |
| 651 | 0x028B | Advantage Hash 1 |
| 652 | 0x028C | Advantage Hash 2 |
| 653 | 0x028D | Advantage Hash 3 |
| 654 | 0x028E | Advantage Hash 4 |
| 655 | 0x028F | Advantage Hash 5 |
| 656 | 0x0290 | Kills |
| 658 | 0x0292 | Deaths |
| 666 | 0x029A | Annihilation Wins |
| 667 | 0x029B | Annihilation Losses |
| 668 | 0x029C | Capture the Skull Wins |
| 669 | 0x029D | Capture the Skull Losses |
| 670 | 0x029E | Siege Wins |
| 671 | 0x029F | Siege Losses |
| 690 | 0x02B2 | PvP XP Modifier |
| 701 | 0x02BD | ACG Item Level |
| 702 | 0x02BE | ACG Item Template ID |
| 703 | 0x02BF | ACG Item Template ID 2 |
| 710 | 0x02C6 | Percent Critical Bonus Damage |
| 711 | 0x02C7 | Critical Damage Rating |
| 713 | 0x02C9 | Heal Rating |
| 768 | 0x0300 | Has Knubot Data |
| 800 | 0x0320 | Quest Booth Difficulty |
| 801 | 0x0321 | Quest AS Minimum Range |
| 802 | 0x0322 | Quest AS Maximum Range |
| 803 | 0x0323 | Spell interrupt chance |
| 804 | 0x0324 | Strength |
| 805 | 0x0325 | Max Strength |
| 806 | 0x0326 | Constitution |
| 807 | 0x0327 | Max Constitution |
| 808 | 0x0328 | Intelligence |
| 809 | 0x0329 | Max Intelligence |
| 810 | 0x032A | Wisdom |
| 811 | 0x032B | Max Wisdom |
| 812 | 0x032C | Charisma |
| 813 | 0x032D | Max Charisma |
| 814 | 0x032E | Dexterity |
| 815 | 0x032F | Max Dexterity |
| 816 | 0x0330 | Casting Concentration |
| 817 | 0x0331 | Max Casting Concentration |
| 818 | 0x0332 | Climbing |
| 819 | 0x0333 | Max Climbing |
| 820 | 0x0334 | Pickpocket |
| 821 | 0x0335 | Max Pickpocket |
| 822 | 0x0336 | Bandaging |
| 823 | 0x0337 | Max Bandaging |
| 824 | 0x0338 | Trap Disarm |
| 825 | 0x0339 | Max Trap Disarm |
| 826 | 0x033A | Lockpicking |
| 827 | 0x033B | Max Lockpicking |
| 828 | 0x033C | Hiding |
| 829 | 0x033D | Max Hiding |
| 830 | 0x033E | Perception |
| 831 | 0x033F | Max Perception |
| 832 | 0x0340 | Renew Mana |
| 833 | 0x0341 | Max Renew Mana |
| 834 | 0x0342 | Fast Healing |
| 835 | 0x0343 | Max Fast Healing |
| 836 | 0x0344 | Mana Attractor |
| 837 | 0x0345 | Max Mana Attractor |
| 838 | 0x0346 | Added Corruption |
| 839 | 0x0347 | Corruption Factor |
| 840 | 0x0348 | Damage Multiplier |
| 841 | 0x0349 | Elder XP |
| 842 | 0x034A | Guaranteed Corruption |
| 843 | 0x034B | Secondary Resist Rating |
| 844 | 0x034C | Attack Rating Type |
| 845 | 0x034D | AT Percent Magic Modifier |
| 846 | 0x034E | DC Percent Magic Modifier |
| 847 | 0x034F | Attack Attribute Bonus |
| 848 | 0x0350 | Defense Attribute Bonus |
| 849 | 0x0351 | AT Percent Range Modifier |
| 850 | 0x0352 | Increased Defense vs Ranged Attacks |
| 851 | 0x0353 | AT Percent Melee Modifier |
| 852 | 0x0354 | DC Percent Melee Modifier |
| 853 | 0x0355 | Spell Magic Modifier |
| 854 | 0x0356 | Spell Resist Modifier |
| 855 | 0x0357 | Blocking Bonus |
| 856 | 0x0358 | Disarm Resist |
| 857 | 0x0359 | Secondary Attack Rating |
| 861 | 0x035D | Magic Damage |
| 862 | 0x035E | Immunity Flags |
| 863 | 0x035F | Weapon Flags |
| 864 | 0x0360 | Player Flags |
| 865 | 0x0361 | Bleeding Resistance |
| 866 | 0x0362 | Combat Rating (1HE) |
| 867 | 0x0363 | Combat Rating (2HE) |
| 868 | 0x0364 | Combat Rating (1HB) |
| 869 | 0x0365 | Combat Rating (2HB) |
| 870 | 0x0366 | Combat Rating (Dagger) |
| 871 | 0x0367 | Combat Rating (Polearm) |
| 872 | 0x0368 | Combat Rating (Bow) |
| 873 | 0x0369 | Combat Rating (Crossbow) |
| 874 | 0x036A | Combat Rating (Thrown) |
| 875 | 0x036B | Combat Rating |
| 876 | 0x036C | Magic Damage (Fire) |
| 877 | 0x036D | Magic Damage (Electrical) |
| 878 | 0x036E | Magic Damage (Holy) |
| 879 | 0x036F | Magic Damage (Unholy) |
| 880 | 0x0370 | Poison Damage |
| 881 | 0x0371 | Siege Damage |
| 882 | 0x0372 | Slashing Damage |
| 883 | 0x0373 | Crushing Damage |
| 884 | 0x0374 | Piercing Damage |
| 886 | 0x0376 | Unarmed Speed |
| 887 | 0x0377 | Unarmed Range |
| 888 | 0x0378 | Visual LOD Level |
| 889 | 0x0379 | Target Distance Change |
| 894 | 0x037E | Physical Health Tap |
| 895 | 0x037F | Physical Mana Tap |
| 896 | 0x0380 | Physical Stamina Tap |
| 897 | 0x0381 | Additional Attacks |
| 898 | 0x0382 | Additional Attack Chance |
| 899 | 0x0383 | Natural Health Regen |
| 900 | 0x0384 | TideRequiredDynelID |
| 901 | 0x0385 | Natural Stamina Regen |
| 902 | 0x0386 | Slashing Invulnerability |
| 903 | 0x0387 | Piercing Invulnerability |
| 904 | 0x0388 | Crushing Invulnerability |
| 905 | 0x0389 | Fire Invulnerability |
| 906 | 0x038A | Electrical Invulnerability |
| 907 | 0x038B | Holy Invulnerability |
| 908 | 0x038C | Unholy Invulnerability |
| 909 | 0x038D | Poison Invulnerability |
| 910 | 0x038E | Siege Invulnerability |
| 911 | 0x038F | Damage Mitigation |
| 912 | 0x0390 | Bonus Carrying Capacity |
| 913 | 0x0391 | Corruption Pool |
| 914 | 0x0392 | Soul Corruption |
| 915 | 0x0393 | Corruption Modifier |
| 916 | 0x0394 | Focus Level |
| 917 | 0x0395 | Buff Slots |
| 918 | 0x0396 | Critical Heal Chance |
| 919 | 0x0397 | Critical Blast Chance |
| 920 | 0x0398 | Attack Rating |
| 921 | 0x0399 | Elder Attack Rating Bonus |
| 922 | 0x039A | Defense Rating |
| 923 | 0x039B | Magic Rating |
| 924 | 0x039C | Elder Magic Rating Bonus |
| 925 | 0x039D | Magical Resist |
| 926 | 0x039E | Protection (Fire) |
| 927 | 0x039F | Protection (Electrical) |
| 928 | 0x03A0 | Protection (Holy) |
| 929 | 0x03A1 | Protection (Unholy) |
| 930 | 0x03A2 | Curse Resist |
| 931 | 0x03A3 | Protection (poison) |
| 932 | 0x03A4 | Magic Rating (Fire) |
| 933 | 0x03A5 | Magic Rating (Electrical) |
| 934 | 0x03A6 | Magic Rating (Holy) |
| 935 | 0x03A7 | Magic Rating (Unholy) |
| 936 | 0x03A8 | Magic Rating (Curse) |
| 937 | 0x03A9 | Magic Rating (Poison) |
| 938 | 0x03AA | Attack Rating (One-Handed Edged) |
| 939 | 0x03AB | Attack Rating (Two-Handed Edged) |
| 940 | 0x03AC | Attack Rating (One-Handed Blunt) |
| 941 | 0x03AD | Attack Rating (Two-Handed Blunt) |
| 942 | 0x03AE | Attack Rating (Dagger) |
| 943 | 0x03AF | Attack Rating (Polearm) |
| 944 | 0x03B0 | Attack Rating (Bow) |
| 945 | 0x03B1 | Attack Rating (Crossbow) |
| 946 | 0x03B2 | Attack Rating (Thrown) |
| 947 | 0x03B3 | Attack Rating (Heroic) |
| 948 | 0x03B4 | Defense Rating (Heroic) |
| 949 | 0x03B5 | Magic Rating (Heroic) |
| 950 | 0x03B6 | Resistance Rating (Heroic) |
| 951 | 0x03B7 | Elder Curse Specialization |
| 952 | 0x03B8 | Elder Poison Specialization |
| 953 | 0x03B9 | Elder 1H Edged Specialization |
| 954 | 0x03BA | Elder 2H Edged Specialization |
| 955 | 0x03BB | Elder 1H Blunt Specialization |
| 956 | 0x03BC | Elder 2H Blunt Specialization |
| 957 | 0x03BD | Elder Dagger Specialization |
| 958 | 0x03BE | Elder Polearm Specialization |
| 959 | 0x03BF | Elder Bow Specialization |
| 960 | 0x03C0 | Elder Crossbow Specialization |
| 961 | 0x03C1 | Elder Thrown Specialization |
| 962 | 0x03C2 | Slow |
| 963 | 0x03C3 | Bonus Caster Level |
| 964 | 0x03C4 | Bonus Melee Level |
| 965 | 0x03C5 | Apprentice Level |
| 966 | 0x03C6 | Breaking Multiplier |
| 967 | 0x03C7 | Durability |
| 968 | 0x03C8 | Combo Attack Number |
| 969 | 0x03C9 | Disarm Speed |
| 970 | 0x03CA | Lung Capacity |
| 971 | 0x03CB | Weapon Critical Chance |
| 972 | 0x03CC | Weapon Attribute |
| 973 | 0x03CD | Weapon Critical Damage |
| 974 | 0x03CE | Damage Deflection |
| 975 | 0x03CF | Absorb |
| 976 | 0x03D0 | Damage reduction per hit on absorb shield |
| 977 | 0x03D1 | Retributive Damage (Melee) |
| 978 | 0x03D2 | Retributive Damage (Ranged) |
| 979 | 0x03D3 | Retributive Damage (Magic) |
| 980 | 0x03D4 | Max Strength Modifier |
| 981 | 0x03D5 | Max Constitution Modifier |
| 982 | 0x03D6 | Max Dexterity Modifier |
| 983 | 0x03D7 | Max Intelligence Modifier |
| 984 | 0x03D8 | Max Wisdom Modifier |
| 985 | 0x03D9 | Max Charisma Modifier |
| 986 | 0x03DA | Slashing Damage (ranged) |
| 987 | 0x03DB | Piercing Damage (ranged) |
| 988 | 0x03DC | Crushing Damage (ranged) |
| 989 | 0x03DD | Fire Damage (ranged) |
| 990 | 0x03DE | Electrical Damage (ranged) |
| 991 | 0x03DF | Holy Damage (ranged) |
| 992 | 0x03E0 | Unholy Damage (ranged) |
| 993 | 0x03E1 | Poison Damage (ranged) |
| 994 | 0x03E2 | Siege Damage |
| 995 | 0x03E3 | Slashing Damage |
| 996 | 0x03E4 | Piercing Damage |
| 997 | 0x03E5 | Crushing Damage |
| 998 | 0x03E6 | Fire Damage |
| 999 | 0x03E7 | StreamCheckMagic |
| 1000 | 0x03E8 | Holy Damage (weapon) |
| 1001 | 0x03E9 | Unholy Damage (weapon) |
| 1002 | 0x03EA | Poison Damage |
| 1003 | 0x03EB | Siege Damage |
| 1004 | 0x03EC | Combat Rating |
| 1005 | 0x03ED | Combat Rating |
| 1006 | 0x03EE | Combat Rating |
| 1007 | 0x03EF | Combat Rating (Fire) |
| 1008 | 0x03F0 | Combat Rating (Electrical) |
| 1009 | 0x03F1 | Combat Rating (Holy) |
| 1010 | 0x03F2 | Combat Rating (Unholy) |
| 1011 | 0x03F3 | Combat Rating (Poison) |
| 1012 | 0x03F4 | Siege Damage |
| 1013 | 0x03F5 | Percent Slashing Damage |
| 1014 | 0x03F6 | Percent Piercing Damage |
| 1015 | 0x03F7 | Percent Crushing Damage |
| 1016 | 0x03F8 | Percent Fire Damage |
| 1017 | 0x03F9 | Percent Electrical Damage |
| 1018 | 0x03FA | Percent Holy Damage |
| 1019 | 0x03FB | Percent Unholy Damage |
| 1020 | 0x03FC | Percent Poison Damage |
| 1021 | 0x03FD | Percent Siege Damage |
| 1022 | 0x03FE | Total Damage |
| 1023 | 0x03FF | Track Slashing Damage |
| 1024 | 0x0400 | Track Piercing Damage |
| 1025 | 0x0401 | Track Crushing Damage |
| 1026 | 0x0402 | Track Fire Damage |
| 1027 | 0x0403 | Track Electrical Damage |
| 1028 | 0x0404 | Track Holy Damage |
| 1029 | 0x0405 | Track Unholy Damage |
| 1030 | 0x0406 | Track Poison Damage |
| 1031 | 0x0407 | Track Siege Damage |
| 1032 | 0x0408 | Challenge Pet |
| 1033 | 0x0409 | Hinder Movement |
| 1034 | 0x040A | Max Team Size |
| 1035 | 0x040B | Death Effect XP |
| 1036 | 0x040C | Dual Wield Proficiency |
| 1037 | 0x040D | Knockback Resistance |
| 1038 | 0x040E | Combat Flags |
| 1039 | 0x040F | Shielding Bonus |
| 1040 | 0x0410 | Max Blocking Chance |
| 1041 | 0x0411 | Base Spell Damage |
| 1042 | 0x0412 | Max Pupils |
| 1043 | 0x0413 | Noise Level |
| 1044 | 0x0414 | Hearing Skill |
| 1045 | 0x0415 | Smell Level |
| 1046 | 0x0416 | Smelling Skill |
| 1047 | 0x0417 | Elf Level |
| 1048 | 0x0418 | Elf Sensing Skill |
| 1049 | 0x0419 | Sight Skill |
| 1057 | 0x0421 | Upper Left Defense |
| 1058 | 0x0422 | Upper Right Defense |
| 1059 | 0x0423 | Head Defense |
| 1060 | 0x0424 | Lower Left Defense |
| 1061 | 0x0425 | Lower Right Defense |
| 1062 | 0x0426 | Thrust Defense |
| 1063 | 0x0427 | Back Defense |
| 1071 | 0x042F | Chance of Offhand Attack |
| 1083 | 0x043B | Endurance |
| 1085 | 0x043D | Run Speed |
| 1087 | 0x043F | Subtlety |
| 1089 | 0x0441 | Recovery |
| 1091 | 0x0443 | Fast Recovery |
| 1093 | 0x0445 | Skulk |
| 1095 | 0x0447 | Weapon Damage (Melee) |
| 1096 | 0x0448 | Weapon Damage (Ranged) |
| 1097 | 0x0449 | Murder Points |
| 1099 | 0x044B | Offhand Rating |
| 1100 | 0x044C | PvP Immunity Rating |
| 1101 | 0x044D | Type |
| 1102 | 0x044E | Instance |
| 1115 | 0x045B | PvP Slashing Invulnerability Rating |
| 1116 | 0x045C | PvP Piercing Invulnerability Rating |
| 1117 | 0x045D | PvP Crushing Invulnerability Rating |
| 1118 | 0x045E | PvP Fire Invulnerability Rating |
| 1119 | 0x045F | PvP Cold Invulnerability Rating |
| 1120 | 0x0460 | PvP Electrical Invulnerability Rating |
| 1121 | 0x0461 | PvP Holy Invulnerability Rating |
| 1287 | 0x0507 | Valor |
| 1351 | 0x0547 | Natural Energy Regeneration |
| 1376 | 0x0560 | Minigames Abandoned |
| 1378 | 0x0562 | XP from PvP Kills |
| 1380 | 0x0564 | Fatalities |
| 1403 | 0x057B | Ferocity |
