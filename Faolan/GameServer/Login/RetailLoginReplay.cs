using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.Spawn;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Tortage Beach (0xFAA/4010) - the universal fallback world. Its burst is always seeded (the proven blob), so
		// any character whose own map has no usable burst still enters a real, rendered world instead of a hand-built
		// one. (There is no hand-built login/teleport path anymore.)
		private const uint DefaultWorldMapId = Map.TortageBeachId;

		// The one beach NPC whose SpawnCheck answer carries status 0x02 (it is Casilda / the intro NPC, handle
		// 0x00081906 in the capture); every other polled entity gets status 0x00. This is an NPC handle from the
		// burst, NOT the player (the client never polls its own handle), so it is unchanged across characters.
		private const uint SpawnCheckStatusTwoHandle = 0x00081906;

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 in its tails for EVERY character
		// (verified across the Strie / hoxxling / randochar captures - it is the canonical beach starting quest, not
		// a per-character artifact). Since the snapshot is replayed and the client renders that quest, persist it so
		// the DB matches what the journal shows (otherwise CharacterQuests is empty while the journal shows a quest -
		// see snapshot_embeds_strie_quest). See EnsureBeachStarterQuest / WorldQuest.BeachStarterQuestId.
		private const uint BeachStarterQuestId = WorldQuest.BeachStarterQuestId;

		// Default starter gear for beach characters (item ids that are fully buildable: p_items identity + harvested
		// archetype), keyed by the CharacterEquipment slot they go in. Slot-appropriate so the client accepts them.
		// Harvested from the create-flow captures (#220): every fresh beach character wears the Slave Manacles, and
		// the rest of the slave outfit is sex-specific - males are bare-chested with a loincloth, females wear a gown
		// + bottom (the exact garment COLOUR is randomised per character in retail, so one representative set is used
		// per sex). Character.Sex 0 = male, 1 = female (the create-flow captures confirm this against the worn gear).
		private const uint StarterWristItem = 3361557; // Slave Manacles (wrist, 0x12) - universal

		// Male (Sex 0): loincloth in the legs slot, no chest item.
		private const uint StarterMaleLegsItem = 3805575; // Red Loincloth

		// Female (Sex 1): a gown (chest) + matching bottom (legs).
		private const uint StarterFemaleChestItem = 324139; // Silver Gown
		private const uint StarterFemaleLegsItem = 324171; // Silver Bottom

		// Per-class starting WEAPON (the main-hand-left slot, 0x06), harvested from the *create* captures (#220):
		// Tortage hands each recruit an improvised weapon, and it is CLASS-specific (per-archetype is disproven - the
		// two Soldier classes in the captures, Conqueror and Guardian, do NOT share a weapon). Coverage is only the
		// classes the create dumps actually contain, like the ability kit's partial coverage; the rest start unarmed
		// until more create captures land. Necromancer is confirmed by two independent create dumps (randochar +
		// faolanx); Assassin by one (Strie). Barbarian + Guardian create dumps show NO starting weapon. (The Fleshy
		// Club / Broken Oar seen elsewhere are NOT create-flow starters - they came from a level-2 char and a login
		// capture respectively, so they are deliberately excluded.)
		private static readonly Dictionary<Class, uint> StarterClassWeapon = new()
		{
			[Class.Necromancer] = 3395744, // Broken Bottle
			[Class.Assassin] = 3735601     // Marlin Spike
		};

		// Rebuilds the (already instance-substituted) world-entry inventory snapshot (0x737A6DF9): patches the LIVE
		// character's level/vitals into the tails and substitutes the player's DB containers for the captured ones
		// (the correct BE32 blob-length framing, see below) - this snapshot drives the bag AND the equipment doll.
		//
		// HISTORY: container substitution used to stall world-load/chat ("any container mutation breaks chat",
		// [[protocol_chat_connection_regression]]) - that bisect PRE-DATED the ContentMirror; the stall was the
		// client thrashing on item resources it could not fetch from the dead retail CDN. With the mirror serving
		// resources locally, substitution is in-client verified safe (2026-06-12: world loads, chat connects, DB
		// items render in bag + doll). A SECOND snapshot later in the session is NOT a delivery mechanism - the
		// client ignores/rejects re-constructs for the same identity (see Packets.cs post-load note).
		//
		// IMPORTANT: built entirely with manual byte assembly + manual CRC and the ORIGINAL packet's header copied
		// verbatim - NOT via PacketStream/WriteLengthHash. An earlier PacketStream-based rebuild stalled the client
		// on the loading screen even though its output was byte-exact, whereas the manual byte path works.
		private byte[] BuildBeachInventoryPacket(byte[] substitutedPacket, Character character, CharacterEquipment equipment = null, CharacterInventory[] inventoryRows = null, byte[] generatedQuestPortion = null,
			uint[] characterTags = null, QuestDefinition beachDefForScratch = null)
		{
			var conanPacket = new ConanPacket(substitutedPacket);
			var body = new FaolanStream(conanPacket.Body.ReadArrayUInt32Length());

			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

			// Patch the LIVE character's level + current vitals into the tails (the captured player's values
			// otherwise ride along; tail stat edits are proven safe in-client - they do NOT affect the chat gate).
			// Full vitals at login; the 0x96B8DC59 vitals update after the spawn sets the max side. Best-effort: a
			// tails layout this model can't index just keeps the captured values.
			try
			{
				if (character != null)
				{
					parts.Tails.Level = character.Level;
					if (parts.Tails.HasKnownRepeatedStatTable)
					{
						parts.Tails.PostAccessBlock.CurrentXp = character.Experience;
						if (character.MaxStats != null)
						{
							// Author the WHOLE per-class vitals block (displayed + base/secondary slots) so it is
							// internally consistent (#4) - not just the displayed maxima, which left a Necromancer's
							// [60] mana copy at the captured Strie 0. (For the beach this captured block is replaced
							// wholesale by BuildFromScratch below; this still applies on every other map.)
							var v = character.MaxStats.SnapshotVitals(character.Class);
							parts.Tails.PostAccessBlock.ApplyClassVitals(v.Hp, v.HpBase, v.Mana, v.ManaBase, v.Stamina, v.StaminaBase);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Tail stat patch failed; keeping the captured tail values.");
			}

			// Containers VERBATIM (the captured inventory tree, re-serialized losslessly - proven byte-identical by
			// InventorySnapshotTests). No DB item substitution here by default (see the method header).
			var rootBytes = parts.Root.ToArray();
			var prefix = parts.Prefix;

			// Substitute the player's DB containers into the WORLD-ENTRY snapshot (the bag + equipment doll).
			// In-client verified 2026-06-13: chat connects, names render, the bag shows the player's real DB items.
			// FRAMING (recovered from AgeOfConan.exe via Ghidra, 2026-06-13 — see docs/archive/db-container-handoff.md):
			// n3InfoItemRemote::Construct reads the body as [u32 KEY=subop][u32 identity.type=50000][u32 instance]
			// [u8 flag][u32 BE blobLen][protobuf blob = blobLen bytes][u32 BE version=0x18][tails subclass]. The
			// "blob" = [field5 (the captured `28 81 06`, = our parts.Prefix[5..])] + [the FullCharacterIIR root =
			// field 6 containers + f7/f8]. The client reads EXACTLY blobLen bytes then expects the 0x18 version — so
			// blobLen MUST equal field5Len + rootLen. The old AdjustSnapshotPrefixRootLength treated the same bytes as
			// a protobuf varint (the byte-misaligned model: `00 00 00 02`=ver, `ae 28`=varint 4483+rootLen, `81 06`=
			// trailing) and on resize wrote the wrong field, leaving blobLen ~0x2xx regardless of the real root → the
			// client over-read into the tails and rejected the FullChar ("Wrong IIR_t version"). SetSnapshotBlobLength
			// writes the correct BE32 length; the captured prefix already carries the right [flag][len][field5] shape.
			if (equipment != null)
			{
				try
				{
					var equipped = new Dictionary<ulong, (Item Item, uint Archetype)>();
					foreach (var (slot, get) in InventoryEquippedSlots)
					{
						var item = get(equipment);
						if (item is { Archetype: not 0 })
							equipped[slot] = (item, item.Archetype);
					}

					(Item, uint)? bagFirst = equipment.FirstPosBag is { Archetype: not 0 }
						? (equipment.FirstPosBag, equipment.FirstPosBag.Archetype)
						: null;

					// AUTHOR the root (#57) fully from typed inputs instead of mutating the captured one: f1
					// character_tag = the character's DB quest-state tags (empty on a fresh beach char), f4/f5 from the
					// server's account-entitlement policy (FullExpansion/SubscriptionFlags), f6 from the DB containers,
					// f7 false, f8 has_spellbook = true. f8 is a CONSTANT, not spell-derived: it decodes to 1 on EVERY
					// captured login (necro/herald/demo casters, but ALSO pure soldiers guardian+conqueror and rogue
					// assassins, and the freshest captured chars) - the "from the Spells nav" follow-up was disproven by
					// the capture sweep. The captured root is no longer read. A fresh beach char has no tags (f1 absent),
					// so the in-client-proven beach snapshot stays byte-identical. BuildRoot is proven to reproduce any
					// captured root (FullCharIirRoundTripTests).
					var typedRoot = FullCharSnapshotBuilder.BuildRoot(
						characterTags,
						FullCharSnapshotBuilder.FullExpansionFlags,
						FullCharSnapshotBuilder.FullSubscriptionFlags,
						false,
						true,
						FullCharSnapshotBuilder.BuildContainers(instance, equipped, bagFirst, inventoryRows ?? []));
					var newRoot = typedRoot.ToProtobufBytes();
					prefix = SetSnapshotBlobLength(parts.Prefix, newRoot.Length);
					Logger.LogInformation("World-entry DB containers: root {Old} -> {New} bytes ({Equipped} equipped + {Rows} rows); blobLen={Blob}.", rootBytes.Length, newRoot.Length, equipped.Count,
						(inventoryRows ?? []).Length, (parts.Prefix.Length - 5) + newRoot.Length);
					rootBytes = newRoot;
				}
				catch (Exception ex)
				{
					Logger.LogWarning(ex, "World-entry DB container substitution failed; keeping verbatim containers.");
				}
			}

			// Body content: [subop][c350][instance][prefix][root][tails], then prefixed by its uint32 length.
			using var contentStream = new FaolanStream();
			contentStream.WriteUInt32(subOpcode);
			contentStream.WriteUInt32(c350);
			contentStream.WriteUInt32(instance);
			contentStream.WriteArray(prefix);
			contentStream.WriteArray(rootBytes);
			byte[] tailsBytes;

			// On the beach, AUTHOR the whole tails from scratch (#186): generated quest portion + the game-static
			// access-window catalog + the per-class stat block, with NO captured-tails dependency. Verified safe: the
			// captured beach burst tails contain BeachTailsTemplate.bin as a contiguous substring, and BuildFromScratch
			// reproduces that template byte-for-byte for the baseline char - so for a fresh char the ONLY change vs the
			// proven captured path is the intended per-class stat block (the in-client gate). Falls back to the captured
			// tails on any failure.
			byte[] fromScratch = null;
			if (beachDefForScratch != null)
			{
				try
				{
					fromScratch = BeachTailsBuilder.BuildFromScratch(beachDefForScratch, character, instance);
					Logger.LogInformation("Snapshot tails: authored beach tails from scratch ({Len} B; per-class stat block, no captured-tails template).", fromScratch.Length);
				}
				catch (Exception ex)
				{
					Logger.LogWarning(ex, "From-scratch beach tails failed; falling back to the captured tails + quest splice.");
				}
			}

			if (fromScratch != null)
			{
				tailsBytes = fromScratch;
			}
			else
			{
				tailsBytes = parts.Tails.ToArray();

				// Splice the DB-GENERATED quest portion (Quest_c + @609 + index, built from the character's beach quest
				// definition) over the captured tails' @0..1292 region - the construct-from-DB login quest log (#186).
				// Byte-identical to the captured region for a fresh beach char; in-client verified (chat connects).
				if (generatedQuestPortion is { Length: BeachTailsBuilder.QuestPortionLength } && tailsBytes.Length >= BeachTailsBuilder.QuestPortionLength)
				{
					var identical = tailsBytes.AsSpan(0, BeachTailsBuilder.QuestPortionLength).SequenceEqual(generatedQuestPortion);
					Array.Copy(generatedQuestPortion, 0, tailsBytes, 0, BeachTailsBuilder.QuestPortionLength);
					Logger.LogInformation("Snapshot quests: spliced DB-generated quest portion ({Len} B; identical-to-captured={Same}).", BeachTailsBuilder.QuestPortionLength, identical);
				}
			}

			contentStream.WriteArray(tailsBytes);
			var bodyContent = contentStream.ToArray();

			// Copy the original header (len4 + crc4 + headerLen4 + header bytes) verbatim, then append the rebuilt
			// body (length-prefixed). Fix up the outer length + CRC by hand.
			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(substitutedPacket.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;

			var rebuilt = new byte[bodyStart + 4 + bodyContent.Length];
			Array.Copy(substitutedPacket, 0, rebuilt, 0, bodyStart);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(bodyStart, 4), (uint)bodyContent.Length);
			bodyContent.CopyTo(rebuilt, bodyStart + 4);

			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(0, 4), (uint)(rebuilt.Length - 4));
			var crc = Crc32.Calculate(rebuilt[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(4, 4), crc);

			return rebuilt;
		}

		// Equipped-container slot id -> the CharacterEquipment nav (archive player_items slot bytes).
		private static readonly (ulong Slot, Func<CharacterEquipment, Item> Get)[] InventoryEquippedSlots =
		[
			(0x01, e => e.Neck), (0x02, e => e.Head), (0x03, e => e.Tokens), (0x05, e => e.Chest), (0x06, e => e.MainHandLeft), (0x07, e => e.Belt), (0x08, e => e.OffHandLeft), (0x09, e => e.RingLeft),
			(0x0a, e => e.RingRight), (0x0b, e => e.Cloak), (0x0c, e => e.Shoulder), (0x0e, e => e.Boots), (0x0f, e => e.Hands), (0x10, e => e.Legs), (0x12, e => e.Wrist), (0x14, e => e.MainHandRight), (0x15, e => e.OffHandRight)
		];

		// Sets the snapshot's protobuf-blob length to match a regenerated root. The 8-byte prefix the client reads
		// is [u8 flag][u32 BE blobLen][field5 = the rest], where the client (n3InfoItemRemote::Construct ->
		// FUN_00cbfb80 in AgeOfConan.exe) reads EXACTLY blobLen bytes as the protobuf blob = field5 + the
		// FullCharacterIIR root, then expects the 0x18 version. So blobLen = field5Len + rootLength, written as a
		// 4-byte big-endian int at prefix offset 1. (This SUPERSEDES AdjustSnapshotPrefixRootLength, which adjusted
		// a byte-misaligned protobuf-varint model and corrupted the real length on resize — see
		// docs/archive/db-container-handoff.md / [[protocol_containeradditem]].) field5Len = prefix.Length - 5 (the flag +
		// the 4 length bytes); the captured field5 bytes at prefix[5..] are preserved unchanged.
		internal static byte[] SetSnapshotBlobLength(byte[] prefix, int rootLength)
		{
			if (prefix.Length < 5 || rootLength < 0)
				return prefix;

			var field5Len = prefix.Length - 5;
			var result = (byte[])prefix.Clone();
			BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1, 4), (uint)(field5Len + rootLength));
			return result;
		}

		// Loads the character's CharacterEquipment (creating it and seeding default starter gear if absent), with the
		// item navs included so they can be built into the snapshot. Beach characters only.
		private async Task<CharacterEquipment> EnsureBeachStarterEquipment(Character character)
		{
			if (character == null)
				return null;

			try
			{
				var equipment = await Database.GetCharacterEquipment(character.Id);
				if (equipment != null)
					return equipment;

				// Seed the default starter gear ONLY for a brand-new equipment row. Re-filling emptied slots on every
				// login would undo the player's own unequips (which persistence writes back), so an existing row is
				// left exactly as the player left it. Sex-specific slave outfit (#220): both sexes wear the manacles;
				// males add a loincloth (legs), females a gown (chest) + bottom (legs). See the Starter* constants.
				var female = character.Sex == Sex.Female;
				equipment = new CharacterEquipment
				{
					Id = character.Id,
					CharacterId = character.Id,
					Wrist = await Database.GetItem(StarterWristItem),
					Chest = female ? await Database.GetItem(StarterFemaleChestItem) : null,
					Legs = await Database.GetItem(female ? StarterFemaleLegsItem : StarterMaleLegsItem),
					// Class-specific improvised weapon (main-hand-left); only the classes the create dumps cover.
					MainHandLeft = StarterClassWeapon.TryGetValue(character.Class, out var weaponId)
						? await Database.GetItem(weaponId)
						: null
				};
				await Database.SaveCharacterEquipment(equipment);

				return equipment;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to load/seed CharacterEquipment for {CharacterId}; sending inventory verbatim.", character.Id);
				return null;
			}
		}

		// Persists the beach starting quest (10238) the world-entry snapshot already shows, so the journal matches the
		// DB. Seeded ONCE: only when the character has NO row for it at all (so a later complete/abandon isn't undone by
		// re-seeding an Active row on every login). Best-effort - a failure just leaves the journal/DB out of sync.
		private async Task EnsureBeachStarterQuest(Character character)
		{
			if (character == null)
				return;

			try
			{
				var quests = await Database.GetCharacterQuests(character.Id);
				if (quests.Any(q => q.QuestId == BeachStarterQuestId))
					return; // already has it (active or completed) - leave the player's state alone

				await Database.SetCharacterQuest(character.Id, BeachStarterQuestId, QuestState.Active);
				Logger.LogInformation("Seeded beach starting quest {QuestId} for {Name}.", BeachStarterQuestId, character.Name);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to seed beach starting quest for {CharacterId}.", character.Id);
			}
		}

		// Loads the character's spawn-scaffold rows, seeding the fresh-character defaults once for characters that
		// have none (like EnsureBeachStarterEquipment seeds starter gear). Returns null on failure - the spawn
		// then keeps the captured scaffold frame.
		private async Task<Character> EnsureSpawnScaffoldRows(Character character)
		{
			if (character == null)
				return null;

			try
			{
				var scaffoldCharacter = await Database.GetCharacterScaffold(character.Id) ?? character;

				// Resolve the character's chosen appearance resources (editor SetMorphValue refs) to the
				// weighted entry tags via the imported client registry; null (nothing stored / registry not
				// imported) falls back to the Dikketest defaults inside EnsureFreshRows.
				List<ScaffoldEntry> weighted = null;
				if (scaffoldCharacter.BodyResources is { Count: > 0 })
				{
					var registry = await Database.GetMorphTargets(scaffoldCharacter
						.BodyResources
						.Select(r => r.Ref)
						.ToArray());
					weighted = PlayerSpawnScaffoldRows.BuildWeightedEntries(scaffoldCharacter.BodyResources, registry);
					if (weighted == null)
					{
						Logger.LogWarning("Character {CharacterId} has {Count} chosen appearance resources but none resolved via MorphTargets (registry imported?); using the default weighted entries.", character.Id,
							scaffoldCharacter.BodyResources.Count);
					}
				}

				if (!PlayerSpawnScaffoldRows.EnsureFreshRows(scaffoldCharacter, weighted))
					return scaffoldCharacter;

				await Database.SaveCharacterScaffold(scaffoldCharacter);
				Logger.LogInformation("Seeded fresh spawn-scaffold rows for character {CharacterId}.", character.Id);
				return scaffoldCharacter;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to load/seed spawn-scaffold rows for {CharacterId}; keeping the captured scaffold frame.", character.Id);
				return null;
			}
		}

		// Regenerates the spawn body's scaffold frame from the character's DB rows via the byte-exact codec
		// (PlayerSpawnScaffoldCodec round-trips every captured player spawn). Returns the body unchanged when it
		// doesn't parse or the character has no rows.
		private byte[] RebuildScaffoldFromRows(byte[] body, Character scaffoldCharacter)
		{
			if (scaffoldCharacter == null)
				return body;

			if (!PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out var error))
			{
				Logger.LogWarning("Player spawn body did not parse as a scaffold ({Error}); keeping the captured frame.", error);
				return body;
			}

			if (!PlayerSpawnScaffoldRows.TryApplyFrom(scaffoldCharacter, scaffold))
				return body;

			return PlayerSpawnScaffoldCodec.Generate(scaffold);
		}

		// Replays a map's captured world burst (from the DB), re-addressed from the burst's captured instance to the
		// logged-in character. Works for ANY map that has a stored burst (beach included - its burst is the proven
		// blob, seeded into the DB by Faolan.Importer).
		// `mapId` is the player's ACTUAL destination map; `burst` may be a fallback from another map (the beach) when the
		// destination has no captured burst of its own. NPC/object rows + the playfield token are sourced by `mapId`,
		// NOT burst.MapId, so a fallback renders the destination map's contents (empty if it has no rows), never the
		// fallback map's (#210). On a same-map entry mapId == burst.MapId, so this is a no-op there.
		private async Task ReplayMapWorldBurst(INetworkClient client, MapWorldBurst burst, uint mapId)
		{
			// Rebuild the exact original packet bytes from the structured IIR records (IirRecordCodec is round-trip
			// lossless), then drive the same proven byte-preserving replay (instance rewrite + inventory/spawn rebuild).
			// Drop transient gameplay records (movement/combat/regen from a mid-session capture) that would feed the
			// client movement/combat for entities that never cleanly spawned, stalling the playfield build. The
			// importer already drops these (IirMessageTypes.IsTransientGameplay), so this is a no-op on a freshly
			// imported burst - kept as defense-in-depth against an older/dirty burst. Vitals are server-generated
			// below (BuildVitals).
			var records = burst
				.Entities
				.OrderBy(e => e.Sequence)
				.Where(e => !IirMessageTypes.IsTransientGameplay(e.IirType))
				.ToList();
			var dropped = burst.Entities.Count - records.Count;
			if (dropped > 0)
				Logger.LogInformation("World burst for map {Map}: dropped {Dropped} transient gameplay records ({Kept} structural records kept).", burst.MapId, dropped, records.Count);
			var packets = records
				.Select(IirRecordCodec.Rebuild)
				.ToList();
			var capturedInstance = burst.CapturedInstance;
			var liveInstance = client.Session.Account.ClientInstance;
			Logger.LogInformation("Replaying world burst for map {Map} ({Count} packets, instance {Old:X8} -> {New:X8}).", burst.MapId, packets.Count, capturedInstance, liveInstance);

			// Record this playfield's despawn token from its 0x737A6DF9 snapshot tail (preserved verbatim in our
			// regenerated snapshot), so n3ToClientQuit removals carry the value the client learned here (WorldDespawn).
			var fullChar = records.FirstOrDefault(e => e.IirType == IirMessageTypes.FullChar);
			if (fullChar?.Body != null)
				_broadcaster.CapturePlayfieldToken(mapId, fullChar.Body);

			client.Session.WorldEntered = true;

			// Load the character's CharacterEquipment (seeding default starter gear once for new beach characters).
			// This drives the player's WORN gear into their entity SPAWN (TryRebuildPlayerSpawnFromDb), which is the
			// chat-safe, DB-driven path for visible equipment. The world-entry inventory SNAPSHOT (0x737A6DF9), by
			// contrast, is sent verbatim - substituting DB items into it stalls the client's world-load completion
			// (it can't resolve arbitrary items' RDB resources) so it never dials the chat server. See
			// BuildBeachInventoryPacket / [[protocol_chat_connection_regression]].
			var equipment = await EnsureBeachStarterEquipment(client.Session.Character);
			await EnsureBeachStarterQuest(client.Session.Character);
			var inventoryRows = client.Session.Character != null
				? await Database.GetCharacterInventory(client.Session.Character.Id)
				: [];

			// The snapshot root's per-character f1 character_tag (#57) = the character's quest-state tag ids
			// (CharacterTag rows; empty on a fresh beach char). Authored from the DB instead of carried from the
			// captured root; emitted ascending to match the wire. (f8 has_spellbook is NOT per-character: it is 1 on
			// EVERY captured login regardless of class/level - see BuildBeachInventoryPacket.)
			var characterTags = client.Session.Character != null
				? (await Database.GetCharacterTags(client.Session.Character.Id)).OrderBy(t => t).ToArray()
				: [];

			// The spawn-scaffold rows (entry table / feat-ID list / pre-IIR frame) drive the player spawn's
			// scaffold frame; characters without rows are seeded with the fresh-character defaults once.
			var scaffoldCharacter = await EnsureSpawnScaffoldRows(client.Session.Character);

			// NPC spawns are NOT rebuilt in the burst: every FA015807 NPC record + its 96C46740 MoveTask is dropped
			// from the burst (IsBurstNpcRecord -> continue) and each NPC is spawned individually via interest
			// (WorldBroadcaster.EnsureZoneNpcs -> NpcSpawnBuilder, the from-DB construct-don't-replay path; #185).

			// Static world objects (decor/containers/zones) sourced from the WorldObjects table the same way -
			// keyed by position, rebuilt via WorldObjectBuilder with the burst's handle, put on the wire only when
			// byte-identical to the re-addressed burst body (zero render risk).
			var worldObjectRows = await Database.GetWorldObjects(mapId);
			// Keyed by (position, identity): two props can share an exact position (#222), so the display_name identity
			// (read from the row's own Data) disambiguates them - position alone would drop one and mis-match the other.
			var objectByPosition = new Dictionary<(int, int, int, string), WorldObject>();
			foreach (var o in worldObjectRows)
			{
				var (ox, oy, oz) = PositionKey(o.PositionX, o.PositionY, o.PositionZ);
				objectByPosition.TryAdd((ox, oy, oz, WorldObjectBuilder.ReadIdentity(o)), o);
			}
			var objectBuilt = 0;
			var objectIdentical = 0;

			// IMPORTANT: do ALL per-packet work (instance rewrite, player-spawn rebuild, inventory parse/rebuild)
			// HERE, before the send loop. The inventory rebuild in particular parses ~9 KB of tails and re-serializes
			// - doing that mid-loop adds a CPU stall between sends that reshapes the burst's TCP segmentation, and
			// the client (no reassembly on the uncompressed download) then misframes and hangs on the loading
			// screen. Pre-building keeps the actual send loop tight and uniform.
			var toSend = new List<byte[]>(packets.Count + 2);

			// MapChange must lead the world build. It's a global record the server always generates from the live
			// map/instance - it is never stored in the burst - so emit it first.
			toSend.Add(BuildMapChange(client));

			// Interest is the SOLE NPC spawn path: every NPC record (FA015807 spawns + their 96C46740 MoveTasks) is
			// dropped from the burst below and the interest system spawns each NPC individually at entry
			// (BroadcastPlayerEntry). This removes the burst-vs-model handle duplication (the old "nameplate flickers
			// then clears" double-spawn). The player's own spawn, containers, world objects and the snapshot stay in
			// the burst. Viable since #23 made mid-session/interest spawns render (in-client verified 2026-06-16).

			// Generate the snapshot's quest portion (Quest_c + @609 + index) from the beach starter's DB definition
			// instead of carrying the captured one - the construct-from-DB login quest log (#186). Byte-identical for a
			// fresh beach char (in-client verified 2026-06-16). state 1 = active on login.
			byte[] generatedBeachQuestPortion = null;
			QuestDefinition beachDefForScratch = null;
			{
				try
				{
					var beachDef = await Database.GetQuestDefinition(WorldQuest.BeachStarterQuestId);
					if (beachDef is { Goals.Count: > 0 } && !string.IsNullOrEmpty(beachDef.IndexGoalSteps))
					{
						generatedBeachQuestPortion = BeachTailsBuilder.BuildBeachQuestPortion(beachDef, state: 1, liveInstance);
						// On the beach itself, author the WHOLE tails from scratch (no captured-tails dependency, #186) -
						// the quest portion + the game-static access-window catalog + the per-class stat block (#4). For
						// any other map the captured burst tails stay the source (they carry that map's own regions).
						if (mapId == DefaultWorldMapId)
							beachDefForScratch = beachDef;
					}
				}
				catch (Exception ex)
				{
					Logger.LogWarning(ex, "Generated beach quest portion failed; keeping the captured quest region.");
				}
			}

			foreach (var packet in packets)
			{
				// Re-address the capture's instance to the logged-in character so the burst works for any character.
				// The NPC handles in the burst are left as-is.
				var pkt = RewriteBurstInstance(packet, capturedInstance, liveInstance);

				if (IsBurstNpcRecord(client, pkt))
					continue; // NPC spawn/MoveTask - spawned individually via interest instead (no burst double-spawn)

				if (IsInventorySnapshot(pkt))
				{
					// The world-entry snapshot: tails patched to the live character, containers substituted with the
					// player's DB items (BuildBeachInventoryPacket) - this is THE inventory delivery (bag + doll).
					pkt = BuildBeachInventoryPacket(pkt, client.Session.Character, equipment, inventoryRows, generatedBeachQuestPortion, characterTags, beachDefForScratch);
				}
				else if (TryRebuildPlayerSpawnFromDb(client, equipment, scaffoldCharacter, pkt, out var rebuilt))
				{
					pkt = rebuilt;
					_broadcaster.CaptureOwnSpawnBody(client, pkt); // stash the player's FA015807 body so other players can be sent it
				}
				else if (TryRebuildWorldObjectFromDb(objectByPosition, pkt, out var objRebuilt, out var objIdentical))
				{
					objectBuilt++;
					if (objIdentical)
					{
						pkt = objRebuilt;
						objectIdentical++;
					}
				}

				// Record each spawned NPC's handle -> name so a clicked handle can be routed to its conversation by
				// the stable name (handles are session-volatile). Reads the final bytes the client receives.
				RecordSpawnedNpcName(client, pkt);
				toSend.Add(pkt);
			}

			if (objectByPosition.Count > 0)
				Logger.LogInformation("World objects for map {Map}: {Identical}/{Built} table-built objects put on the wire byte-identical, {Rows} rows available.", burst.MapId, objectIdentical, objectBuilt,
					objectByPosition.Count);

			// The player's own vitals are server-generated from the live character (not the captured player's), emitted
			// after the player has spawned in the burst.
			toSend.Add(BuildVitals(client));

			// GameTime (the world clock) is a global record the server always generates - never stored in the burst.
			toSend.Add(BuildGameTime(client));

			// Tight send loop - no per-packet computation here.
			foreach (var bytes in toSend)
				await client.SendAsync(bytes);
		}

		// Answers a client 0x2020 spawn poll with a 0x2008 SpawnCheck on the 201C conversation: body is
		// 0x0000C350 + the polled entity handle + a status byte (0x02 for the player's own avatar, else 0x00).
		private Task SendSpawnCheckResponse(INetworkClient client, uint handle)
		{
			var status = (byte)(handle == SpawnCheckStatusTwoHandle
				? 0x02
				: 0x00);

			return new PacketStream()
				.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchSetQuestMarker)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(handle)
				.WriteByte(status)
				.SendAsync(client);
		}

		// The single existing-character WORLD ENTRY used by both login and the teleport GM command: send the 0x201c
		// channel handshake, then replay the character's current-map world burst from the DB (MapChange + world
		// objects + NPC spawns + the player spawn + the inventory snapshot, all re-addressed and DB-driven). Falls
		// back to the beach burst for any map without a usable burst. The caller has already set Character.MapId/Pos.
		private async Task SendWorldEntry(INetworkClient client)
		{
			await Send0X201C(client);

			// #237: re-grant the character's learned SPELLS (DispatchLearnSpell 0x200A) BEFORE the world snapshot, so
			// the client absorbs them as part of the initial load and stays SILENT - retail sends them pre-entry
			// (NecroL80 login: grants at frame 59, the 0x737A6DF9 snapshot at frame 152). Granting them post-ready
			// (the old PostReadyFollowUp call) made the client treat each as a fresh learn and re-toast "You have
			// learned a new spell" on EVERY login. Done here, the shared world-entry path, so it's silent on login AND
			// on changemap/teleport re-entry. (Abilities 0x2052 stay post-0x2024 - they don't toast.)
			await GrantStartingSpells(client);

			var mapId = client.Session.Character.MapId;

			// Burst-less entry (#210) is the ONLY world-entry path: the whole world entry is built from
			// generated/structured pieces - no captured burst. (The FAOLAN_BURSTLESS_ENTRY env toggle + its
			// captured-burst replay fallback were removed once burstless became the verified default; the
			// captured-burst replay now survives only as SendBurstlessWorldEntry's internal safety net for a map
			// with no quest definition.)
			await SendBurstlessWorldEntry(client, mapId);
		}

		// If the (already re-addressed) burst packet is an NPC entity Spawn (FA015807 whose handle is NOT the
		// player's instance) and a matching NpcSpawns row exists at the same spawn position, rebuild its body from
		// that row via NpcSpawnBuilder (addressed to the burst's own handle) and splice it in - keeping the burst
		// packet's re-addressed header + framing, exactly like the player-spawn rebuild. `wasIdentical` reports
		// whether the built body equals the burst's (true for same-source rows: the table reproduces the capture).
		// Falls back (returns false) when there is no matching row or the build fails, so the NPC keeps its burst body.
		// True if the burst packet is an NPC spawn (FA015807 whose handle is NOT the player's own) or an NPC MoveTask
		// (96C46740). Used by the FAOLAN_NPC_INDIVIDUAL_SPAWN path to drop NPC records from the burst so the interest
		// system is the sole NPC spawn path. The player's own FA015807 (handle == client instance) returns false.
		private static bool IsBurstNpcRecord(INetworkClient client, byte[] packet)
		{
			if (packet.Length < 12)
				return false;

			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			if (bodyStart + 16 > packet.Length)
				return false;

			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 4, 4));
			if (subOpcode == MoveTaskBuilder.SubOpcode)
				return true; // 96C46740 NPC movement registration

			if (subOpcode != 0xFA015807u)
				return false;

			var handle = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 12, 4));
			return handle != client.Session.Account.ClientInstance; // an NPC spawn, not the player's own
		}

		// If the burst packet is the player's own entity Spawn (FA015807 whose handle is the client instance),
		// rebuild it from the DB character: transform (position from the DB, rotation kept), the
		// ProtoCoreCharacterIIR (name/sex/race/level/vitals + worn equipment) and the scaffold frame (from the
		// character's scaffold rows via the codec). Each step falls back to the captured bytes on mismatch.
		private bool TryRebuildPlayerSpawnFromDb(INetworkClient client, CharacterEquipment equipment, Character scaffoldCharacter, byte[] packet, out byte[] rebuilt)
		{
			rebuilt = null;

			// Full Conan packet: [len4][crc4][headerLen4][header][body]; the body starts after the header.
			if (packet.Length < 12)
				return false;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart + 16 > packet.Length)
				return false;

			// Body: [len4][subop4][c350 4][handle4]...
			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 4, 4));
			var handle = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 12, 4));
			if (subOpcode != 0xFA015807u || handle != client.Session.Account.ClientInstance)
				return false;

			var character = client.Session.Character;
			if (character == null)
				return false;

			// The transform's rotation is a ProtoQuat (X/Y/Z/W); we author it from the character's DB facing
			// (Character.Rotation, a quaternion - captured into the row by the importer, identity for fresh chars).
			// An earlier attempt stalled the client because the DB carried a DEGENERATE quaternion (W defaulted to
			// 0 -> a zero rotation the avatar can't realize); RotationW now defaults to 1, but as a belt-and-braces
			// guard a near-zero-magnitude stored quaternion falls back to the captured rotation rather than risk it.
			var body = packet[bodyStart..];
			var capturedRotation = new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotXOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotYOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotZOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotWOffset, 4)));
			var dbRotation = character.Rotation;
			var rotation = dbRotation.LengthSquared() > 0.0001f
				? dbRotation
				: capturedRotation;

			// Rebuild the body from the DB: position (fixed-length) then the character message. If the character has
			// fully-buildable worn gear, rebuild the whole CharacterIIR (name + the worn equipment as its
			// InventoryEntries) from the DB via the typed message; otherwise fall back to the proven name-only splice.
			var newBody = SpawnPacketBuilder.BuildSpawnBody(body, handle, character.Position, rotation);

			if (PlayerSpawnReader.TryReadCharacterMessage(newBody, out var read))
			{
				// Apply the character's identity / appearance / worn gear onto the (captured) message; shared with
				// the burst-less path so both produce the same per-character overrides (PlayerSpawnMessage).
				var message = read.Message;
				PlayerSpawnMessage.ApplyCharacter(message, character, equipment);

				newBody = SpawnPacketBuilder.SetCharacterMessage(newBody, message);

				// Regenerate the scaffold frame (pre-IIR islands + entry table + feat-ID list + pair lists) from
				// the character's own DB rows, replacing the captured player's scaffold. No-op fallback when the
				// body doesn't parse or the character has no rows.
				newBody = RebuildScaffoldFromRows(newBody, scaffoldCharacter);
			}
			// else: no decodable ProtoCoreCharacterIIR in the body (a malformed / non-standard spawn). There is no
			// structural way to rename it, and the old fixed-Strie-offset SetSpawnName fallback could only have fired
			// on a body whose name signature TryReadCharacterMessage (which scans every 0x0A) would already have
			// found - so it was dead in practice (#201). Ship the body with its captured identity rather than
			// raw-patch a malformed one.

			rebuilt = new byte[bodyStart + newBody.Length];
			Array.Copy(packet, 0, rebuilt, 0, bodyStart); // [len][crc][headerLen][header]
			newBody.CopyTo(rebuilt, bodyStart);

			// Set the outer packet length (total - 4) and recompute the CRC over everything after it; we send via
			// raw client.SendAsync (not PacketStream), so both are done in-place, big-endian like the framing.
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(0, 4), (uint)(rebuilt.Length - 4));
			var crc = Crc32.Calculate(rebuilt[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(4, 4), crc);

			Logger.LogInformation("Rebuilt player spawn from DB: handle=0x{Handle:X}, name='{Name}', pos=({X},{Y},{Z}).", handle, character.Name, character.Position.X, character.Position.Y, character.Position.Z);
			return true;
		}

		// As TryRebuildNpcSpawnFromDb, for the static world objects (66AEDD50/F096251E/33A56FB0). Matched by
		// position, rebuilt with the burst's handle via WorldObjectBuilder; `wasIdentical` is the safety signal.
		private bool TryRebuildWorldObjectFromDb(IReadOnlyDictionary<(int, int, int, string), WorldObject> objectByPosition, byte[] packet, out byte[] rebuilt, out bool wasIdentical)
		{
			rebuilt = null;
			wasIdentical = false;
			if (packet.Length < 12)
				return false;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart + SpawnPacketBuilder.PosZOffset + 4 > packet.Length)
				return false;

			var body = packet.AsSpan(bodyStart);
			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
			if (subOpcode is not (0x66AEDD50u or 0xF096251Eu or 0x33A56FB0u or 0x6BD56736u or 0x1FD6412Bu))
				return false; // decor/container/zone + destructible (6BD56736) + climbing dynel (1FD6412B)

			var (px, py, pz) = PositionKey(BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosXOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosYOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosZOffset, 4)));
			var key = (px, py, pz, WorldObjectBuilder.ReadIdentity(body));
			// Splice the DB object identity onto the LIVE placement (handle/transform/playfield from the burst body),
			// so the per-session playfield + handle stay correct and only the object definition comes from the table.
			if (!objectByPosition.TryGetValue(key, out var row) || !WorldObjectBuilder.TryBuildOntoLivePlacement(row, body, out var newBody))
				return false;

			wasIdentical = packet.Length - bodyStart == newBody.Length && packet
				.AsSpan(bodyStart)
				.SequenceEqual(newBody);

			rebuilt = new byte[bodyStart + newBody.Length];
			Array.Copy(packet, 0, rebuilt, 0, bodyStart);
			newBody.CopyTo(rebuilt, bodyStart);

			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(0, 4), (uint)(rebuilt.Length - 4));
			var crc = Crc32.Calculate(rebuilt[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(4, 4), crc);
			return true;
		}

		// Encodes value as a base-128 varint of EXACTLY byteLength bytes, padding the high groups with overlong
		// (non-minimal) continuation bytes when value needs fewer. Overlong varints are valid - a decoder reads the
		// continuation bytes and reconstructs the same value - so this keeps a varint substitution length-preserving
		// when the captured id encodes to more bytes than the live id. Returns null if value does not fit in
		// byteLength*7 bits (the live id can't be made to fit the captured slot without shifting later bytes).
		internal static byte[] EncodeVarintPadded(uint value, int byteLength)
		{
			if (byteLength is < 1 or > 5)
				return null;

			var bytes = new byte[byteLength];
			var v = value;
			for (var i = 0; i < byteLength; i++)
			{
				bytes[i] = (byte)((v & 0x7F) | (i < byteLength - 1
					? 0x80u
					: 0u));
				v >>= 7;
			}

			return v != 0
				? null // value didn't fit in byteLength 7-bit groups
				: bytes;
		}

		// True if the packet is the 0x2000 inventory snapshot (sub-opcode 0x737A6DF9).
		private static bool IsInventorySnapshot(byte[] packet)
		{
			if (packet.Length < 12)
				return false;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart + 8 <= packet.Length && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 4, 4)) == 0x737A6DF9u;
		}

		// Quantizes a world position to cm (0.01) so a burst spawn matches its NpcSpawns row across float
		// round-trips; collisions (two NPCs within a cm) are not a concern at beach scale.
		private static (int, int, int) PositionKey(float x, float y, float z)
		{
			return ((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), (int)MathF.Round(z * 100f));
		}

		// The entity name is the first `0x0A <len 1..63> <printable-ascii>` run in the spawn body.
		private static string ReadSpawnName(ReadOnlySpan<byte> body)
		{
			for (var i = 16; i + 2 < body.Length; i++)
			{
				if (body[i] != 0x0A)
					continue;

				int len = body[i + 1];
				if (len is < 1 or > 63 || i + 2 + len > body.Length)
					continue;

				var ok = true;
				for (var j = 0; j < len; j++)
				{
					var c = body[i + 2 + j];
					if (c is < 0x20 or > 0x7E)
					{
						ok = false;
						break;
					}
				}

				if (ok)
					return Encoding.ASCII.GetString(body.Slice(i + 2, len));
			}

			return null;
		}

		// Records a world-burst spawn's handle -> NPC name into the session map (for conversation routing). No-op for
		// non-FA015807 packets and the player's own spawn (handle == client instance).
		private static void RecordSpawnedNpcName(INetworkClient client, byte[] packet)
		{
			if (packet.Length < 12)
				return;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart + 16 > packet.Length)
				return;

			var body = packet.AsSpan(bodyStart);
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != 0xFA015807u)
				return;

			var handle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(12, 4));
			if (handle == client.Session.Account.ClientInstance)
				return; // the player's own spawn

			var name = ReadSpawnName(body);
			if (name != null)
				client.Session.SpawnedNpcNamesByHandle[handle] = name;

			// Record the NPC's real max health from its spawn CharacterStats (health, else life; both ×100 on the
			// wire), so combat seeds its vitals from this instead of the flat DefaultNpcMaxHealth placeholder - the
			// bar then moves relative to the HP the client actually rendered from this spawn.
			if (PlayerSpawnReader.TryReadCharacterMessage(packet[bodyStart..], out var read) && read.Message.Stats is { } stats)
			{
				var wireHealth = stats.Health != 0
					? stats.Health
					: stats.Life;
				if (wireHealth >= 100) // ×100 scale; ignore implausibly small/absent values
					client.Session.SpawnedNpcMaxHealthByHandle[handle] = wireHealth / 100;
			}
		}

		// Replaces every non-overlapping occurrence of find with repl (equal length) in buffer, after the 8-byte
		// length+crc prefix.
		private static void ReplaceAll(byte[] buffer, byte[] find, byte[] repl)
		{
			for (var i = 8; i + find.Length <= buffer.Length; i++)
			{
				var match = true;
				for (var j = 0; j < find.Length; j++)
				{
					if (buffer[i + j] != find[j])
					{
						match = false;
						break;
					}
				}

				if (match)
				{
					repl.CopyTo(buffer, i);
					i += find.Length - 1;
				}
			}
		}

		// SendPostReadyBurst (the post-0x2024 login record stream) is built as explicit PacketStream sends
		// in PostReadyBurst.cs.

		// Re-addresses every reference to the captured Strie instance (0x02165EFF) in a burst packet to the live
		// character instance: the receiver-conversation varint (header) and the inventory nested varints appear as
		// varint(0x02165EFF) = FF BD D9 10; entity handles / body fields appear as the raw uint32 02 16 5E FF. The
		// raw form is always 4 bytes; the live id's varint is padded (overlong) to the captured varint's byte length
		// so the substitution stays length-preserving even for maps whose captured instance encodes to more bytes
		// (1000/11040 captured a 5-byte instance like 0x110099BD). We then recompute the CRC. (This is the interim
		// burst-replay path; construct-don't-replay supersedes it per-entity, but this is still the active fallback.)
		private static byte[] RewriteBurstInstance(byte[] packet, uint oldInstance, uint newInstance)
		{
			if (oldInstance == newInstance)
				return packet;

			var oldVarint = Varint.Encode(oldInstance);
			var newVarint = EncodeVarintPadded(newInstance, oldVarint.Length);
			if (newVarint == null)
				return packet; // live id needs more varint bytes than the captured id; can't substitute in place

			var oldRaw = new byte[4];
			var newRaw = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(oldRaw, oldInstance);
			BinaryPrimitives.WriteUInt32BigEndian(newRaw, newInstance);

			var p = (byte[])packet.Clone();
			ReplaceAll(p, oldVarint, newVarint);
			ReplaceAll(p, oldRaw, newRaw);

			var crc = Crc32.Calculate(p[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4, 4), crc);
			return p;
		}
	}
}
