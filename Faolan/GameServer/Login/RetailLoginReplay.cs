using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Spawn;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using Faolan.Core.Network.Codecs.GameServer;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 in its tails for EVERY character
		// (verified across the Strie / hoxxling / randochar captures - it is the canonical beach starting quest, not
		// a per-character artifact). Since the snapshot is replayed and the client renders that quest, persist it so
		// the DB matches what the journal shows (otherwise CharacterQuests is empty while the journal shows a quest -
		// see snapshot_embeds_strie_quest). See EnsureBeachStarterQuest / WorldQuest.BeachStarterQuestId.
		private const uint BeachStarterQuestId = WorldQuest.BeachStarterQuestId;

		// Tortage Beach (0xFAA/4010) - the universal fallback world. Its burst is always seeded (the proven blob), so
		// any character whose own map has no usable burst still enters a real, rendered world instead of a hand-built
		// one. (There is no hand-built login/teleport path anymore.)
		//private const uint DefaultWorldMapId = Map.TortageBeachId;

		// Male (Sex 0): loincloth in the legs slot, no chest item.
		private const uint StarterMaleLegsItem = 3805575; // Red Loincloth

		// Default starter gear for beach characters (item ids that are fully buildable: p_items identity + harvested
		// hash), keyed by the CharacterEquipment slot they go in. Slot-appropriate so the client accepts them.
		// Harvested from the create-flow captures (#220): every fresh beach character wears the Slave Manacles, and
		// the rest of the slave outfit is sex-specific - males are bare-chested with a loincloth, females wear a gown
		// + bottom (the exact garment COLOUR is randomised per character in retail, so one representative set is used
		// per sex). Character.Sex 0 = male, 1 = female (the create-flow captures confirm this against the worn gear).
		private const uint StarterWristItem = 3361557; // Slave Manacles (wrist, 0x12) - universal

		// Female (Sex 1): a gown (chest) + matching bottom (legs). Retail randomises the garment COLOUR per
		// character, so pick a colour at random from the slave-gown set (all fully buildable - Hash + Data
		// present, Type 101 - same as the Silver pair that was the sole representative before).
		private static readonly (uint Gown, uint Bottom)[] StarterFemaleOutfits =
		[
			(324066, 324176), // Blue
			(324140, 324168), // Purple
			(324067, 324172), // Red
			(324139, 324171), // Silver
			(105844, 324175) // White
		];

		// Equipped-container slot id -> the CharacterEquipment nav (archive player_items slot bytes).
		private static readonly (ulong Slot, Func<CharacterEquipment, Item> Get)[] InventoryEquippedSlots =
		[
			(0x01, e => e.Neck), (0x02, e => e.Head), (0x03, e => e.Tokens), (0x05, e => e.Chest), (0x06, e => e.MainHandLeft), (0x07, e => e.Belt), (0x08, e => e.OffHandLeft), (0x09, e => e.RingLeft),
			(0x0a, e => e.RingRight), (0x0b, e => e.Cloak), (0x0c, e => e.Shoulder), (0x0e, e => e.Boots), (0x0f, e => e.Hands), (0x10, e => e.Legs), (0x12, e => e.Wrist), (0x14, e => e.MainHandRight), (0x15, e => e.OffHandRight)
		];

		// Per-class starting WEAPON (the main-hand-left slot, 0x06), harvested from the *create* captures (#220).
		// Coverage is only the classes a create dump actually shows armed - the rest start unarmed until more create
		// captures land. The Fleshy Club / Broken Oar / Broken Bottle seen elsewhere are NOT create-flow starters -
		// they came from a level-2 char and login captures respectively, so they are deliberately excluded.
		//
		// #276/#286: re-harvested every per-class CREATE capture (slot 0x06 of the equipped container, via the
		// ProtoAcgItem template1 fixed32; Captures/scripts decode). Result: the equipped container in the create
		// flow carries ONLY the slave outfit (chest gown / leg bottom + wrist Slave Manacles) - NO class delivers a
		// MainHandLeft weapon at create. The earlier Necromancer = Broken Bottle (3395744) was a mis-harvest from a
		// LOGIN capture (Broken Bottle is absent from every create dump), and Melvin confirmed a Broken Bottle is
		// wrong for a caster - so it is removed and casters now start unarmed, matching the create captures. Assassin
		// is the one documented create-verified entry (Strie) and is kept.
		private static readonly Dictionary<Class, uint> StarterClassWeapon = new Dictionary<Class, uint>
		{
			[Class.Assassin] = 3735601, // Marlin Spike
			[Class.Necromancer] = 3395744, // Broken Bottle (0x33D0A0; necro create capture, BccIIR slot-6)
			[Class.TempestOfSet] = 215711 // Broken Bottle (0x34A9F - the OTHER bottle; tempest create capture, BccIIR slot-6)
		};

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
				// Retail randomises the female slave-outfit colour per character (males always get the loincloth).
				var femaleOutfit = StarterFemaleOutfits[Random.Shared.Next(StarterFemaleOutfits.Length)];
				equipment = new CharacterEquipment
				{
					CharacterId = character.Id,
					Wrist = await Database.GetItem(StarterWristItem),
					Chest = female
						? await Database.GetItem(femaleOutfit.Gown)
						: null,
					Legs = await Database.GetItem(female
						? femaleOutfit.Bottom
						: StarterMaleLegsItem),
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
					var registry = await Database.GetMorphTargets(scaffoldCharacter.BodyResources.Select(r => r.Ref).ToArray());
					weighted = PlayerSpawnScaffoldRows.BuildWeightedEntries(scaffoldCharacter.BodyResources, registry);
					if (weighted == null)
					{
						Logger.LogWarning("Character {CharacterId} has {Count} chosen appearance resources but none resolved via MorphTargets (registry imported?); using the default weighted entries.",
							character.Id,
							scaffoldCharacter.BodyResources.Count);
					}
				}

				// Seeds a fresh scaffold on first login; an existing one is left as-is. Propagating a later appearance
				// EDIT into the scaffold is done at save-time by Faolan.Web (PropagateToScaffold, #290), so there's no
				// login-time refresh here.
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

		// Answers a client 0x2020 spawn poll with a 0x2008 SpawnCheck on the 201C conversation: body is
		// 0x0000C350 + the polled entity handle + a status byte (0x02 for the player's own avatar, else 0x00).
		private async Task SendSpawnCheckResponse(INetworkClient client, uint handle)
		{
			// The 0x2020 poll reply (0x2008) doubles as the NPC quest marker. Compute it from the player's quest
			// state for the polled handle (#241) instead of the old hardcoded 0x02-for-Casilda, which forced '!' on
			// EVERY poll and overrode the state-driven marker (so it never cleared after accept). Non-giver /
			// not-in-view -> 0x00 (still confirms the spawn). Capture-matched: poll reply was 0x02 before accept,
			// 0x00 after (Ranochar frames 125 -> 698).
			var status = await _broadcaster.QuestMarkerStateForHandle(client, handle);

			await new PacketStream()
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

			// First-entry seeding (#74/#71): a freshly-created character with an empty action bar gets its class's
			// starting spells (persisted) + the default bar layout, so the spell grant below sends them this session.
			await EnsureCharacterLoadout(client);

			// #237: re-grant the character's learned SPELLS (DispatchLearnSpell 0x200A) BEFORE the world snapshot, so
			// the client absorbs them as part of the initial load and stays SILENT - retail sends them pre-entry
			// (NecroL80 login: grants at frame 59, the 0x737A6DF9 snapshot at frame 152). Granting them post-ready
			// (the old PostReadyFollowUp call) made the client treat each as a fresh learn and re-toast "You have
			// learned a new spell" on EVERY login. Done here, the shared world-entry path, so it's silent on login AND
			// on changemap/teleport re-entry. (Abilities 0x2052 stay post-0x2024 - they don't toast.)
			await GrantCharacterSpells(client);

			var mapId = client.Session.Character.MapId;

			// Burst-less entry (#210) is the ONLY world-entry path: the whole world entry is built from
			// generated/structured pieces - no captured burst. (The FAOLAN_BURSTLESS_ENTRY env toggle + its
			// captured-burst replay fallback were removed once burstless became the verified default; the
			// captured-burst replay now survives only as SendBurstlessWorldEntry's internal safety net for a map
			// with no quest definition.)
			await SendBurstlessWorldEntry(client, mapId);
		}

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

		// SendPostReadyBurst (the post-0x2024 login record stream) is built as explicit PacketStream sends
		// in PostReadyBurst.cs.
	}
}
