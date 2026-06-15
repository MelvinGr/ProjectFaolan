# RDB item-template stat modifiers (#3)

Decode progress on the per-item stat bonuses needed for server-side MAX-STAT aggregation (#3 — so a
character's MaxHealth/attributes reflect worn gear and the regen bar can fill past the class base).

**Source.** The bonuses are NOT in our `Item` table or the old C++ `p_items` (both only carry name + the
Data quad). They live in the **encrypted RDB item-template**, which we already TEA-decrypt (key = the item's
Data1..4 quad; `RdbItemImporter`). Dump the decrypted templates with:

```
dotnet run --project Faolan.Importer -- --skip-characters --rdb <RDB dir> --rdb-items --dump-templates <dir>
```

Each file `<type>_<id>.bin` = `[12-byte RDB-Id header][body]`. Dissector: `Captures/decode_item_template.py`.

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
2. **Vital / HP gear bonuses — the actual bar-fill case.** NOT located. A raw scan found the HpMax (`0x1b`)
   and StaminaMax (`0x1f9`) stat ids in some bodies, but on inspection those were coincidental field VALUES
   (e.g. field 286 = 505), not stat modifiers. The ring-`+HP` case that motivates #3 therefore can't be
   imported yet.

## Status / next step

- Decoded + validated: primary-attribute modifier (`972`/`971`). Importable, but on its own it does NOT fix
  the bar-fill bug (that needs the HP/vital bonus, still open).
- Nothing imported yet (decode-first): no `ItemStat` table, no change to `faolan.db`.
- To unblock: grab a capture/screenshot of an item tooltip with known stats (ideally one carrying +HP and a
  secondary attribute), use it to pin the secondary-modifier-list semantics and find the vital-bonus field,
  then add an `ItemStat` table + a `MaxStatAggregator` (base from `CharacterMaxStats` + summed equipped-gear
  bonuses + attribute contributions) and feed it into `RegenerateMaxStats` / the vitals path.
