# RDB item-template stat modifiers (#3)

Decode progress on the per-item stat bonuses needed for server-side MAX-STAT aggregation (#3 — so a
character's MaxHealth/attributes reflect worn gear and the regen bar can fill past the class base).

**Source.** The bonuses are NOT in our `Item` table or the old C++ `p_items` (both only carry name + the
Data quad). They live in the **encrypted RDB item-template**, which we already TEA-decrypt (key = the item's
`Key1..4` quad, stored as `Item.Key`; `RdbItemImporter`). Dump the decrypted templates with:

```
dotnet run --project Faolan.Importer -- --only rdb-items --rdb <RDB dir> --dump-templates <dir>
```

Each file `<type>_<id>.bin` = `[12-byte RDB-Id header][body]`. Dissector: `Captures/scripts/decode_item_template.py`.

## Body grammar

After a short fixed header the body is an RDB **property table**: a run of `[fieldId u32][value u32]` pairs
(LE) with **strictly DECREASING fieldId**. Field ids are RDB template field numbers (973, 972, 971, 857,
615, 499, 496, 462, 460, 436, 360, 316, 294, 287, 286, 285, 211, 148, …), NOT wire stat ids.

### VALIDATED — the primary-attribute modifier

| field | meaning | values seen |
|-------|---------|-------------|
| `972` | modifier's **wire stat id** | only `804`=Str / `806`=Con / `808`=Int / `810`=Wis / `814`=Dex (`0x324/0x326/0x328/0x32a/0x32e`, per `Archive` `devStuff/StatIds.txt`) |
| `971` | amount | `+1` / `+2` / `+3` |

Self-validation: across all 1964 decrypted templates, field 972 holds an attribute stat id in **253** items
and a non-attribute value in **0** — so this slot is safe to import as a `(statId, amount)` attribute bonus.
In practice only Str (178) and Dex (75) appear as the *primary* attribute.

## OPEN — what's NOT decoded yet (blocks the #3 headline)

1. **Secondary modifier list.** After the property table there's a separate region of repeating
   `[2][9][statId][amount][…]` records that ALSO carry attribute ids — Con/Int/Wis turn up *only* here. But
   the amounts look too large to be raw attribute points (e.g. Con=30 on a Tortage bow), so the semantic
   (requirement? scaled? percentage?) is unconfirmed. **Needs a reference item with a known tooltip** to
   disambiguate before import.
2. **Vital / HP gear bonuses — the actual bar-fill case.** NOT located. A raw scan found the HealthCurrent (`0x1b`)
   and StaminaMax (`0x1f9`) stat ids in some bodies (HealthMax is actually `0x0001`), but on inspection those were coincidental field VALUES
   (e.g. field 286 = 505), not stat modifiers. The ring-`+HP` case that motivates #3 therefore can't be
   imported yet.

## Status / next step

- Decoded + validated: primary-attribute modifier (`972`/`971`).
- **IMPORTED (2026-07-10):** the `ItemStat` table (`ItemId`, `WireStatId`, `Amount`) is populated by `RdbItemImporter`
  from field 972/971 - **359 rows** across 2412 decrypted templates, all clean: `WireStatId` only ever Str (804, 248)
  or Dex (814, 111), `Amount` only +1/+2/+3 (matching this doc). `MaxStatAggregator.AggregateGearBonuses` sums a
  character's equipped `ItemStat` rows by wire stat id (unit-tested). This is the decode-first-clean foundation.
- **STILL OPEN (reference-gated, needs an item tooltip):**
  1. the **secondary-modifier list** semantics + the **vital/HP bonus** field - not decoded, so not in `ItemStat`
     (they slot into the same table + aggregator sum when a known-tooltip reference pins them);
  2. the **wire application** - adding the gear bonus onto the character's base attribute IN the stat block. The
     base attributes are currently the fixed baseline (`SubClassStatBlock`, e.g. `Strength = 0xA`), and the exact
     ×10 scale a +N modifier displays as isn't proven without a reference, so the aggregator's total is not yet
     patched into the sent stats. That last hop is what makes the +Str/+Dex visible in-client (and, with the vital
     field, fixes the health-bar-fill bug).
