# Protobuf message schema (generated)

> **GENERATED — do not edit.** Source: the compiled protobuf-net types in
> `Faolan.Core/Protobuf/AocProtocol.g.cs`, read by reflection over their
> `[ProtoContract]`/`[ProtoMember]` attributes (the actual wire contract).
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.

58 message types, 340 fields. These are the protobuf messages recovered from
`AgeOfConan.exe`; the human-readable schema is `Faolan.Core/Protobuf/aoc-client-protos.proto`,
and `AocProtocol.g.cs` is protobuf-net-generated from it (the reflected source here).

## Field encoding — read this before decoding by hand [V1]

The **Wire** column is the on-the-wire encoding of each field. The two that bite:

- **`zigzag`** (3 fields): a SIGNED varint (`(n << 1) ^ (n >> 31)`). Reading it as a plain
  varint is wrong for negatives and halves positives — this is the character-editor `int_parameter` bug.
- **`fixed32`/`fixed64`** (10 fields): **little-endian** fixed-width. Note protobuf fixed32 is LE
  while raw (non-protobuf) body floats in this protocol are big-endian — do not confuse the two.
- **`varint`**: base-128 LEB, unsigned. **length-delimited**: `[varint len][bytes]` (strings, bytes, sub-messages).

## CharacterTagTypeBuf

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `version` | `uint` | `varint` |  |
| 2 | `tag_type` | `List<TagType>` | `length-delimited` (repeated) |  |
| 3 | `max_tag_type_id` | `uint` | `varint` |  |

## CharacterTagTypeBuf.TagType

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `id` | `uint` | `varint` |  |
| 8 | `name` | `String` | `length-delimited` (string) |  |

## CollisionMaterialMessage

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `version` | `uint` | `varint` | yes |
| 2 | `properties` | `List<Properties>` | `length-delimited` (repeated) |  |

## CollisionMaterialMessage.Properties

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `materialid` | `uint` | `varint` |  |
| 2 | `name` | `String` | `length-delimited` (string) |  |
| 3 | `directocclusion` | `Single` | `varint`? |  |
| 4 | `reverbocclusion` | `Single` | `varint`? |  |
| 5 | `additiveocclusion` | `Boolean` | `varint` |  |

## CommandMessage

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `bcc_action_id` | `uint` | `varint` |  |
| 2 | `commandqueue_client_id` | `uint` | `varint` |  |
| 3 | `commandqueue_server_id` | `uint` | `varint` |  |
| 4 | `anim_random` | `uint` | `varint` |  |
| 5 | `flags` | `uint` | `varint` |  |
| 6 | `execution_status` | `uint` | `varint` |  |
| 7 | `combo_id` | `uint` | `varint` |  |
| 8 | `combo_attack_num` | `uint` | `varint` |  |
| 9 | `combo_attack_hits` | `uint` | `varint` |  |
| 10 | `target` | `ProtoID32` | `length-delimited` (message) |  |
| 11 | `recharge_time` | `uint` | `varint` |  |
| 12 | `activation_time` | `uint` | `varint` |  |

## GameTriggerBuf

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `version` | `uint` | `varint` |  |
| 2 | `trigger` | `List<Trigger>` | `length-delimited` (repeated) |  |
| 3 | `max_trigger_id` | `uint` | `varint` |  |

## GameTriggerBuf.Trigger

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `id` | `uint` | `varint` |  |
| 2 | `type` | `uint` | `varint` |  |
| 3 | `script_id` | `uint` | `varint` |  |
| 4 | `id_value` | `uint` | `varint` |  |
| 5 | `hash1_value` | `String` | `length-delimited` (string) |  |
| 6 | `hash2_value` | `String` | `length-delimited` (string) |  |
| 7 | `int_value` | `int` | **`zigzag`** (signed varint) |  |
| 8 | `str_value` | `String` | `length-delimited` (string) |  |
| 9 | `playfield_id` | `uint` | `varint` |  |

## InventoryEntry.GemSlot

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `pos` | `uint` | `varint` |  |
| 2 | `item` | `ProtoAcgItem` | `length-delimited` (message) |  |

## LooksPacket

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `Name` | `String` | `length-delimited` (string) |  |
| 2 | `Description` | `String` | `length-delimited` (string) |  |
| 3 | `Slots` | `List<LooksPacketSlot>` | `length-delimited` (repeated) |  |

## LooksPacket.LooksPacketSlot

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `RdbId` | `uint` | `varint` |  |
| 2 | `Tuck` | `Boolean` | `varint` |  |
| 3 | `Tint1` | `long` | `varint` |  |
| 4 | `Tint2` | `long` | `varint` |  |
| 5 | `DecalU` | `int` | `varint` |  |
| 6 | `DecalV` | `int` | `varint` |  |
| 7 | `Parent` | `LooksPacketSlot` | `length-delimited` (message) |  |
| 8 | `MeshName` | `String` | `length-delimited` (string) |  |
| 9 | `BodyLocation` | `int` | `varint` |  |
| 10 | `HasAttractorPlacement` | `Boolean` | `varint` |  |
| 11 | `AttractorPlacement` | `int` | `varint` |  |
| 12 | `ArmTuck` | `Boolean` | `varint` |  |
| 13 | `TintMask1` | `long` | `varint` |  |
| 14 | `TintMask2` | `long` | `varint` |  |
| 15 | `TintMask3` | `long` | `varint` |  |
| 16 | `TintMask4` | `long` | `varint` |  |
| 17 | `TintMask5` | `long` | `varint` |  |
| 18 | `TintMask6` | `long` | `varint` |  |

## LoreTreeBuf

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `version` | `uint` | `varint` |  |
| 2 | `node` | `List<LoreNode>` | `length-delimited` (repeated) |  |
| 3 | `max_id` | `uint` | `varint` |  |

## LoreTreeBuf.LinkType

*(no fields)*

## LoreTreeBuf.LoreLink

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `type` | `LinkType` | `varint` |  |
| 2 | `id` | `uint` | `varint` |  |

## LoreTreeBuf.LoreMedia

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `rdb_id` | `uint` | `varint` |  |
| 2 | `type` | `uint` | `varint` |  |

## LoreTreeBuf.LoreNode

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `id` | `uint` | `varint` |  |
| 2 | `parent_id` | `uint` | `varint` |  |
| 3 | `name` | `String` | `length-delimited` (string) |  |
| 4 | `child_id` | `UInt32[]` | `length-delimited` (repeated) |  |
| 5 | `link` | `List<LoreLink>` | `length-delimited` (repeated) |  |
| 6 | `criteria_lua_script_id` | `uint` | `varint` |  |
| 7 | `reward_lua_script_id` | `uint` | `varint` |  |
| 8 | `is_invisible` | `Boolean` | `varint` |  |
| 9 | `node_type` | `uint` | `varint` |  |
| 10 | `node_stats` | `List<LoreStat>` | `length-delimited` (repeated) |  |
| 11 | `node_media` | `List<LoreMedia>` | `length-delimited` (repeated) |  |
| 12 | `node_rewards` | `List<LoreReward>` | `length-delimited` (repeated) |  |

## LoreTreeBuf.LoreReward

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `reward_id` | `uint` | `varint` |  |
| 2 | `type` | `uint` | `varint` |  |

## LoreTreeBuf.LoreStat

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `id` | `uint` | `varint` |  |
| 2 | `action` | `uint` | `varint` |  |
| 3 | `int_value` | `int` | `varint` |  |
| 4 | `float_value` | `Single` | `varint`? |  |
| 5 | `string_value` | `String` | `length-delimited` (string) |  |

## LuaScript.Source

*(no fields)*

## LuaScriptList

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `version` | `uint` | `varint` |  |
| 2 | `script` | `List<LuaScript>` | `length-delimited` (repeated) |  |
| 3 | `max_id` | `uint` | `varint` |  |

## LuaScriptList.LuaScript

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `id` | `uint` | `varint` |  |
| 2 | `source` | `Source` | `varint` |  |
| 3 | `raw_script` | `String` | `length-delimited` (string) |  |
| 4 | `compiled_script` | `byte[]` | `length-delimited` (bytes) |  |

## ProtoAcgItem

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `key1` | `ProtoDecryptionKey` | `length-delimited` (message) |  |
| 2 | `key2` | `ProtoDecryptionKey` | `length-delimited` (message) |  |
| 3 | `key3` | `ProtoDecryptionKey` | `length-delimited` (message) |  |
| 4 | `template1` | `uint` | `fixed32` (LE) |  |
| 5 | `template2` | `uint` | `fixed32` (LE) |  |
| 6 | `template3` | `uint` | `fixed32` (LE) |  |
| 7 | `level` | `uint` | `varint` |  |
| 8 | `hash` | `uint` | `fixed32` (LE) |  |

## ProtoCoreCharacterIIR

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `name` | `String` | `length-delimited` (string) |  |
| 2 | `firstname` | `String` | `length-delimited` (string) |  |
| 3 | `lastname` | `String` | `length-delimited` (string) |  |
| 4 | `stats` | `CharacterStats` | `length-delimited` (message) |  |
| 5 | `npc_stats` | `NpcStats` | `length-delimited` (message) |  |
| 6 | `player_stats` | `PlayerStats` | `length-delimited` (message) |  |
| 7 | `bcc` | `CharacterBcc` | `length-delimited` (message) |  |
| 8 | `pet_owner_instance` | `uint` | `varint` |  |
| 9 | `pet_type` | `uint` | `varint` |  |
| 10 | `pet_points` | `uint` | `varint` |  |
| 11 | `character_kdtree` | `uint` | `varint` |  |
| 12 | `offensive_target` | `ProtoID32` | `length-delimited` (message) |  |
| 13 | `defensive_target` | `ProtoID32` | `length-delimited` (message) |  |
| 14 | `team_id` | `ProtoID32` | `length-delimited` (message) |  |
| 15 | `team_leader_id` | `ProtoID32` | `length-delimited` (message) |  |
| 16 | `team_leader_guild_id` | `ProtoID32` | `length-delimited` (message) |  |
| 17 | `raid_id` | `ProtoID32` | `length-delimited` (message) |  |
| 18 | `raid_leader_id` | `ProtoID32` | `length-delimited` (message) |  |
| 19 | `locked_to` | `ProtoID32` | `length-delimited` (message) |  |
| 20 | `can_fall` | `Boolean` | `varint` |  |
| 21 | `movement_mode` | `uint` | `varint` |  |
| 22 | `generic_stats` | `UInt32[]` | `length-delimited` (repeated) |  |
| 23 | `inventory_entry` | `List<InventoryEntry>` | `length-delimited` (repeated) |  |

## ProtoCoreCharacterIIR.CharacterBcc

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `anim_idle_id` | `uint` | `varint` |  |
| 2 | `anim_idle_timer` | `Single` | `varint`? |  |
| 3 | `anim_idle_blend` | `Boolean` | `varint` |  |
| 4 | `anim_action_id` | `uint` | `varint` |  |
| 5 | `anim_action_timer` | `Single` | `varint`? |  |
| 6 | `anim_action_blend` | `Boolean` | `varint` |  |
| 7 | `anim_forced_id` | `uint` | `varint` |  |
| 8 | `anim_forced_timer` | `Single` | `varint`? |  |
| 9 | `anim_forced_blend` | `Boolean` | `varint` |  |
| 10 | `anim_flags` | `uint` | `varint` |  |

## ProtoCoreCharacterIIR.CharacterStats

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `sex` | `uint` | `varint` |  |
| 2 | `side` | `uint` | `varint` |  |
| 3 | `race` | `uint` | `varint` |  |
| 4 | `monster_scale` | `uint` | `varint` |  |
| 5 | `monster_data` | `uint` | `varint` |  |
| 6 | `life` | `uint` | `varint` |  |
| 7 | `health` | `uint` | `varint` |  |
| 8 | `max_stamina` | `uint` | `varint` |  |
| 9 | `stamina` | `uint` | `varint` |  |
| 10 | `max_mana` | `uint` | `varint` |  |
| 11 | `mana` | `uint` | `varint` |  |
| 12 | `looks_package` | `uint` | `varint` |  |
| 13 | `mount_mesh` | `uint` | `varint` |  |
| 14 | `mount_scale` | `uint` | `varint` |  |
| 15 | `mount_flags` | `uint` | `varint` |  |
| 16 | `flags` | `uint` | `varint` |  |
| 17 | `head_mesh` | `uint` | `varint` |  |
| 18 | `level` | `uint` | `varint` |  |
| 19 | `apprentice_level` | `uint` | `varint` |  |
| 20 | `radius` | `int` | **`zigzag`** (signed varint) |  |
| 21 | `bcc` | `uint` | `varint` |  |
| 22 | `bcc_state` | `uint` | `varint` |  |
| 23 | `blood_type` | `uint` | `varint` |  |
| 24 | `skulk` | `uint` | `varint` |  |
| 25 | `max_skulk` | `uint` | `varint` |  |
| 26 | `tradeskill_flags` | `uint` | `varint` |  |
| 27 | `client_scry_script` | `uint` | `varint` |  |
| 28 | `sound_scry_script` | `uint` | `varint` |  |
| 29 | `cars_group` | `uint` | `varint` |  |
| 30 | `movement_multiplier` | `uint` | `varint` |  |
| 31 | `ooc_movement_multiplier` | `uint` | `varint` |  |
| 32 | `run_speed` | `uint` | `varint` |  |
| 33 | `max_run_speed` | `uint` | `varint` |  |
| 34 | `orientation_mode` | `uint` | `varint` |  |
| 35 | `guild_id` | `uint` | `varint` |  |
| 36 | `voice_set` | `uint` | `varint` |  |
| 37 | `chat_id` | `uint` | `varint` |  |

## ProtoCoreCharacterIIR.InventoryEntry

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `pos` | `uint` | `varint` |  |
| 2 | `item` | `ProtoAcgItem` | `length-delimited` (message) |  |
| 3 | `gem_slots` | `List<GemSlot>` | `length-delimited` (repeated) |  |

## ProtoCoreCharacterIIR.NpcStats

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `sight_skill` | `uint` | `varint` |  |
| 2 | `hearing_skill` | `uint` | `varint` |  |
| 3 | `smelling_skill` | `uint` | `varint` |  |
| 4 | `template_id` | `uint` | `fixed32` (LE) |  |
| 5 | `super_family` | `uint` | `varint` |  |
| 6 | `community` | `uint` | `varint` |  |
| 7 | `los_height` | `uint` | `varint` |  |
| 8 | `spawn_animation` | `uint` | `varint` |  |
| 9 | `npc_flags` | `uint` | `varint` |  |
| 10 | `normalset` | `uint` | `varint` |  |
| 11 | `profession_name` | `uint` | `varint` |  |
| 12 | `profession_type` | `uint` | `varint` |  |
| 13 | `faction_id` | `uint` | `varint` |  |
| 14 | `npc_type` | `uint` | `varint` |  |

## ProtoCoreCharacterIIR.PlayerStats

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `pvp_level` | `uint` | `varint` |  |
| 2 | `pvp_total_kills` | `uint` | `varint` |  |
| 3 | `pvp_total_deaths` | `uint` | `varint` |  |
| 4 | `ride_skill` | `uint` | `varint` |  |
| 5 | `gm_level` | `uint` | `varint` |  |
| 6 | `gm_ldb_name` | `uint` | `varint` |  |
| 7 | `gm_cloak_level` | `uint` | `varint` |  |
| 8 | `player_flags` | `uint` | `varint` |  |
| 9 | `expansion` | `uint` | `varint` |  |
| 10 | `heroic_mode` | `uint` | `varint` |  |
| 11 | `strength` | `uint` | `varint` |  |
| 12 | `expansion_flags` | `uint` | `varint` |  |
| 13 | `subscription_flags` | `uint` | `varint` |  |
| 14 | `pvp_total_kills_seasonal` | `uint` | `varint` |  |
| 15 | `pvp_total_deaths_seasonal` | `uint` | `varint` |  |
| 16 | `hardcore_level` | `int` | `varint` |  |

## ProtoDecryptionKey

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `key1` | `uint` | `fixed32` (LE) |  |
| 2 | `key2` | `uint` | `fixed32` (LE) |  |
| 3 | `key3` | `uint` | `fixed32` (LE) |  |
| 4 | `key4` | `uint` | `fixed32` (LE) |  |

## ProtoDeleteItemIIR

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `container_id` | `ProtoID32` | `length-delimited` (message) | yes |
| 2 | `item_pos` | `int` | `varint` | yes |
| 3 | `template_id` | `ProtoID32` | `length-delimited` (message) | yes |

## ProtoEmptyMessage

*(no fields)*

## ProtoEndpoint

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `type` | `uint` | `fixed32` (LE) |  |
| 2 | `instance` | `uint` | `varint` |  |
| 3 | `token` | `uint` | `varint` |  |

## ProtoFullCharacterIIR

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `character_tag` | `UInt32[]` | `length-delimited` (repeated) |  |
| 2 | `free_feat_respecs` | `uint` | `varint` |  |
| 3 | `feat_resets` | `uint` | `varint` |  |
| 4 | `expansion_flags` | `uint` | `varint` |  |
| 5 | `subscription_flags` | `uint` | `varint` |  |
| 6 | `inventory` | `List<ProtoInventoryGame>` | `length-delimited` (repeated) |  |
| 7 | `has_new_claim_items` | `Boolean` | `varint` |  |
| 8 | `has_spellbook` | `Boolean` | `varint` |  |

## ProtoGameCodeDynelIIR

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `position` | `ProtoVec3` | `length-delimited` (message) |  |
| 2 | `rotation` | `ProtoQuat` | `length-delimited` (message) |  |
| 3 | `playfield_instance` | `uint` | `varint` |  |
| 4 | `running_effects` | `List<RunningEffects>` | `length-delimited` (repeated) |  |

## ProtoGameCodeDynelIIR.RunningEffects

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `effect_id` | `uint` | `varint` |  |
| 2 | `run_time` | `Single` | `varint`? |  |

## ProtoID32

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `type` | `uint` | `varint` |  |
| 2 | `instance` | `uint` | `varint` |  |

## ProtoInventoryGame

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `inventory_id` | `ProtoID32` | `length-delimited` (message) | yes |
| 2 | `max_size` | `uint` | `varint` |  |
| 3 | `disabled_slots_count` | `uint` | `varint` |  |
| 4 | `items` | `List<InventoryListEntry>` | `length-delimited` (repeated) |  |

## ProtoInventoryGame.InventoryListEntry

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `pos` | `uint` | `varint` | yes |
| 2 | `item` | `ProtoInventoryGameEntry` | `length-delimited` (message) | yes |

## ProtoInventoryGameEntry

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `item_id` | `ulong` | `varint` |  |
| 2 | `stat_list` | `List<StatValue>` | `length-delimited` (repeated) |  |
| 3 | `item_data` | `ProtoAcgItem` | `length-delimited` (message) |  |
| 4 | `slot_map` | `List<SlotMap>` | `length-delimited` (repeated) |  |
| 5 | `creator_name` | `String` | `length-delimited` (string) |  |
| 6 | `maxAvailableItems` | `String` | `length-delimited` (string) |  |

## ProtoInventoryGameEntry.SlotMap

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `slot` | `uint` | `varint` |  |
| 2 | `item` | `ProtoAcgItem` | `length-delimited` (message) |  |

## ProtoInventoryGameEntry.StatValue

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `stat` | `uint` | `varint` |  |
| 2 | `value` | `uint` | `varint` |  |

## ProtoLootBagIIR

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `cash` | `uint` | `varint` |  |
| 2 | `current_looter_id` | `ProtoID32` | `length-delimited` (message) |  |
| 3 | `owner_id` | `ProtoID32` | `length-delimited` (message) |  |
| 4 | `dropper_id` | `ProtoID32` | `length-delimited` (message) |  |
| 5 | `allowed_looter_id` | `ProtoID32` | `length-delimited` (message) |  |
| 6 | `special_loot_owner` | `List<SpecialLootOwnerEntry>` | `length-delimited` (repeated) |  |
| 7 | `has_master_looter` | `Boolean` | `varint` |  |
| 8 | `inventory` | `ProtoInventoryGame` | `length-delimited` (message) |  |

## ProtoLootBagIIR.SpecialLootOwnerEntry

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `pos` | `uint` | `varint` | yes |
| 2 | `owner` | `ProtoID32` | `length-delimited` (message) |  |

## ProtoMoveInventoryItemIIR

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `source_container_id` | `ProtoID32` | `length-delimited` (message) | yes |
| 2 | `source_item_pos` | `int` | `varint` | yes |
| 3 | `destination_container_id` | `ProtoID32` | `length-delimited` (message) | yes |
| 4 | `destination_item_pos` | `int` | `varint` | yes |

## ProtoQuat

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `x` | `Single` | `varint`? |  |
| 2 | `y` | `Single` | `varint`? |  |
| 3 | `z` | `Single` | `varint`? |  |
| 4 | `w` | `Single` | `varint`? |  |

## ProtoScryScriptIIR

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `version` | `uint` | `varint` |  |
| 2 | `type` | `Type` | `varint` |  |
| 3 | `int_parameter` | `int` | **`zigzag`** (signed varint) |  |
| 4 | `string_parameter_one` | `String` | `length-delimited` (string) |  |
| 5 | `string_parameter_two` | `String` | `length-delimited` (string) |  |
| 6 | `id_parameter` | `ProtoID32` | `length-delimited` (message) |  |
| 7 | `variable` | `List<Variable>` | `length-delimited` (repeated) |  |

## ProtoScryScriptIIR.Type

*(no fields)*

## ProtoScryScriptIIR.Variable

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `name` | `String` | `length-delimited` (string) |  |
| 2 | `id_value` | `ProtoID32` | `length-delimited` (message) |  |

## ProtoSimpleDynelIIR

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `kdtree` | `uint` | `varint` |  |
| 2 | `name` | `String` | `length-delimited` (string) |  |
| 3 | `display_name` | `String` | `length-delimited` (string) |  |
| 4 | `show_popup_on_use` | `Boolean` | `varint` |  |
| 5 | `cars_group` | `uint` | `varint` |  |
| 6 | `quest_item_effect` | `uint` | `varint` |  |
| 7 | `waypoint_type` | `uint` | `varint` |  |
| 8 | `template_item_id` | `uint` | `varint` |  |
| 9 | `is_hidden` | `Boolean` | `varint` |  |
| 10 | `is_trap` | `Boolean` | `varint` |  |
| 11 | `is_locked` | `Boolean` | `varint` |  |
| 12 | `is_closed` | `Boolean` | `varint` |  |
| 13 | `can_be_lockpicked` | `Boolean` | `varint` |  |
| 14 | `ressurection_point_id` | `uint` | `varint` |  |
| 15 | `trap_owner` | `uint` | `varint` |  |
| 16 | `tag_id` | `uint` | `varint` |  |

## ProtoVec3

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `x` | `Single` | `varint`? |  |
| 2 | `y` | `Single` | `varint`? |  |
| 3 | `z` | `Single` | `varint`? |  |

## QuestGiverList

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `version` | `uint` | `varint` |  |
| 2 | `quest_giver` | `List<QuestGiver>` | `length-delimited` (repeated) |  |
| 3 | `main_quest` | `List<MainQuest>` | `length-delimited` (repeated) |  |
| 4 | `quest` | `List<Quest>` | `length-delimited` (repeated) |  |
| 5 | `quest_giver_pos` | `List<QuestGiverPos>` | `length-delimited` (repeated) |  |
| 6 | `quest_goal` | `List<QuestGoal>` | `length-delimited` (repeated) |  |

## QuestGiverList.MainQuest

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `template_id` | `uint` | `varint` |  |
| 2 | `hash` | `String` | `length-delimited` (string) |  |
| 3 | `name` | `String` | `length-delimited` (string) |  |
| 4 | `first_quest_template_id` | `uint` | `varint` |  |
| 5 | `last_quest_template_id` | `uint` | `varint` |  |
| 6 | `condition_script_id` | `uint` | `varint` |  |
| 7 | `type` | `uint` | `varint` |  |
| 8 | `prev_template_id` | `uint` | `varint` |  |
| 9 | `next_template_id` | `uint` | `varint` |  |
| 10 | `tier` | `uint` | `varint` |  |
| 11 | `first_main_quest_template_id` | `uint` | `varint` |  |
| 12 | `last_main_quest_template_id` | `uint` | `varint` |  |
| 13 | `reward` | `List<MainQuestReward>` | `length-delimited` (repeated) |  |
| 14 | `optional_reward` | `List<MainQuestReward>` | `length-delimited` (repeated) |  |
| 15 | `xp_reward` | `uint` | `varint` |  |
| 16 | `cash_reward` | `uint` | `varint` |  |

## QuestGiverList.MainQuestReward

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `template_id` | `uint` | `varint` |  |
| 2 | `type` | `uint` | `varint` |  |

## QuestGiverList.Quest

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `template_id` | `uint` | `varint` |  |
| 2 | `hash` | `String` | `length-delimited` (string) |  |
| 3 | `name` | `String` | `length-delimited` (string) |  |
| 4 | `main_quest_template_id` | `uint` | `varint` |  |
| 5 | `start_scry_script_id` | `uint` | `varint` |  |
| 6 | `tier` | `uint` | `varint` |  |
| 7 | `start_new_tier` | `Boolean` | `varint` |  |

## QuestGiverList.QuestGiver

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `type` | `QuestGiverType` | `varint` |  |
| 2 | `object_id` | `uint` | `varint` |  |
| 9 | `id` | `uint` | `varint` |  |
| 13 | `main_quest_template_id` | `uint` | `varint` |  |

## QuestGiverList.QuestGiverPos

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `type` | `QuestGiverType` | `varint` |  |
| 2 | `object_id` | `uint` | `varint` |  |
| 3 | `playfield_id` | `uint` | `varint` |  |
| 4 | `pos` | `ProtoVec3` | `length-delimited` (message) |  |

## QuestGiverList.QuestGiverType

*(no fields)*

## QuestGiverList.QuestGoal

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `template_id` | `uint` | `varint` |  |
| 2 | `hash` | `String` | `length-delimited` (string) |  |
| 3 | `name` | `String` | `length-delimited` (string) |  |
| 4 | `quest_template_id` | `uint` | `varint` |  |
| 5 | `phase` | `uint` | `varint` |  |

## RpcHeader

| Tag | Name | Type | Wire | Required |
|-----|------|------|------|----------|
| 1 | `caller_id` | `ProtoEndpoint` | `length-delimited` (message) | yes |
| 2 | `endpoint_id` | `ProtoEndpoint` | `length-delimited` (message) | yes |
| 3 | `full_routing_context` | `List<ProtoEndpoint>` | `length-delimited` (repeated) |  |
| 4 | `method_instance` | `uint` | `varint` | yes |
| 5 | `routing_destination` | `List<ProtoEndpoint>` | `length-delimited` (repeated) |  |
