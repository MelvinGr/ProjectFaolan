# How a caster's spell damage scales off their attributes

Two independent sources agree on this, which is why it is written down as a decode rather than a model: the
client's own magic-damage accessor, recovered from `AgeOfConan.exe`, and RDB property field **847** on the spell
records. Neither was fitted to the other.

## The client's rule — by CLASS

`FUN_01118ce0` is the client's magic-damage accessor (`tools/ghidra/DumpSpellScaling.java` finds it: the
`TKW_MagicalDpsAbilityModifier` tweak name is a static `Tweak_c` global at `0x2d24368`, its float value at
`+8` = `0x2d24370`, and that address has exactly one reader).

```c
int FUN_01118ce0(uint schoolStatId, char includeExtra)
{
  if (!HasManaPool()) return 0;
  if (GetStat(0x1fb /* ManaMax */, 2) <= 0) return 0;

  uint attr = 0x32a;                                  // Wisdom
  int  cls  = GetStat(0x130 /* class */, 0);
  if (cls == 0x2c || cls == 0x29 || cls == 0x2b)      // Demonologist, Necromancer, Herald of Xotli
      attr = 0x328;                                   // Intelligence

  int flat = GetStat(0x35d, 2);                       // the worn-gear Magic Damage rating
  if (includeExtra) flat += GetStat(0xe2, 2);

  float tweak = *(float *)0x02d24370;                 // TKW_MagicalDpsAbilityModifier = 0.6
  float score = GetAttributeScore(attr);
  return GetStat(schoolStatId, 2) + (int)(score * tweak * 100.0f) + flat;
}
```

The disassembly is what pins the multiply order and the truncation — the decompiler folds the tweak into
`fVar4 * fVar5 * 100.0`, which reads as a constant `0.6 * 0.6 * 100` and is not what the code does:

```
01118d9c: MOVSS  XMM0, dword ptr [0x02d24370]   ; the tweak
01118daa: MOVSS  dword ptr [ESP + 0xc], XMM0    ; spill it
01118db7: CALL   0x007184d0                     ; attribute score -> XMM0
01118dc8: MULSS  XMM0, dword ptr [ESP + 0xc]    ; x tweak
01118dce: MULSS  XMM0, dword ptr [0x01f24468]   ; x 100  (the wire's hundredths)
01118de2: CVTTSS2SI EBX, XMM0                   ; TRUNCATE, not round
01118de8: ADD    EAX, EBX
```

So, in display units: **`magicDamage = schoolStat + trunc(attribute × 0.6) + flatGearMagicDamage`**, and the
scaling attribute is **Wisdom for every caster except Necromancer / Herald of Xotli / Demonologist, which use
Intelligence**. The whole thing is gated on the character having a mana pool at all.
[V3: CombatResolverTests.MagicDamage_ScalesOffTheClassAttribute_AtTheClientsTweakedRate]

### The tweak family it belongs to

`TKW_MagicalDpsAbilityModifier` is the client's own misspelling of the `TWK_` prefix every other tweak carries.
Its neighbours in RDB 1000516 (`--report rdb-tweaks`) are the same mechanism for the other derived stats, and
the physical twin `FUN_011187f0` has the identical shape — attribute × tweak, added to a flat gear rating:

| tweak | value | used by |
|---|---|---|
| `TKW_MagicalDpsAbilityModifier` | 0.6 | `FUN_01118ce0` — magic damage |
| `TWK_PhysicalDpsAbilityModifierStrength` | 3 | `FUN_011187f0` — physical DPS, the default branch |
| `TWK_PhysicalDpsAbilityModifierDexterity` | 3 | `FUN_011187f0`, when the class's primary attribute (stat `0x3cc`) is Dexterity |
| `TWK_ArmorAbilityModifier` | 2 | |
| `TWK_ProtectionAbilityModifier` | 0.5 | |

The physical branch does **not** carry the `× 100`; only the magical one does. They are not the same unit.

## The RDB's rule — by SPELL (field 847)

Spell record property field **847** (`0x34F`) names an attribute as a wire stat id. 716 of the 21,502
type-1040005 records carry one, and it takes exactly three values:

| value | attribute | records |
|---|---|---|
| 808 | Intelligence | 482 |
| 810 | Wisdom | 224 |
| 804 | Strength | 10 |

Grouping those records by the class encoded in `GuiStackingLine / 1000` (see `Spell.GuiStackingLine`, the client's
`GUI Stacking Line` — its class-encoded values are what this grouping uses) reproduces the client's class rule exactly, from data the client-side decode never touched:

| class | attribute | records |
|---|---|---|
| Necromancer (41) | Intelligence | 153 |
| Demonologist (44) | Intelligence | 126 |
| Herald of Xotli (43) | Intelligence | 41 |
| Tempest of Set (28) | Wisdom | 48 |
| Priest of Mitra (24) | Wisdom | 32 |
| Dark Templar (31) | Wisdom | 3 |
| Conqueror (22) | Strength | 5 |
| Demonologist (44) | **Wisdom** | **6** |

The Conqueror's five are a non-caster taking the physical branch. The six Demonologist spells on Wisdom are the
point of the field: **it is an OVERRIDE, not the rule**. The class rule already covers every spell in the game,
so a per-spell field would be redundant unless it existed to contradict it — which is how
`CombatResolver.MagicDamageAttribute` reads it, and why a 0 (the overwhelming majority of templates) is not a
data gap.

Shipped as `Spell.ScalingAttribute`, imported by `rdb-spells` (`RdbSpellImporter`), resolved per character at
world entry (`IDatabaseRepository.GetCharacterSpellScalingAttributes`) and applied by `WorldEntity.MagicDamageFor`.

Two class ids appear in field 847's data that the class registry does not name — `GuiStackingLine / 1000` of **50**
(5 records) and **440** (1). The 50 is the same anomaly the feat modifiers show.

## Reproducing it

```zsh
# the field itself, and cross-tabulated against anything else on the same record
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:847 --Content:RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:348,551x847 --Content:RdbDir <rdb-dir>

# the tweak values
dotnet run --project Faolan.Importer -- --report rdb-tweaks --Content:RdbDir <rdb-dir>

# the client side
FAOLAN_GHIDRA_OUT=<dir> /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
  -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpSpellScaling.java
```

## Where the spell's own magnitude comes from

Field 348 is not a damage rating: the client names `0x15C` `SplashDamage`, and it occurs on 215 of the 21,502
records — the same 215 that carry 349 `SplashDamageMaxTargets`, inside 347 `SplashRadius`'s 231
(`spell-record-fields.md`). The per-spell power is the magnitude the spell's own template states on health, rolled
within its stated range at the caster's level (`SpellPoolEffects` resolves it; 2,149 spells carry one), and
`CombatResolver.ResolveSpellDamage` adds the attribute term (inside `magicDamage`) to it. A spell that states no
hit falls back to a flat `20 + level × 5` placeholder.
