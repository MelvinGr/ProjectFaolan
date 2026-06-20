using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Protobuf;
using Microsoft.Extensions.Logging;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.Spawn;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Builds the world-entry packets entirely from generated/structured pieces - NO captured burst (#210). Each
	// record is assembled from a row/the character + wrapped in the shared world-entry header (FrameWorldEntryRecord):
	//   - player spawn  -> PlayerSpawnBuilder.BuildFromScratch (scaffold + transform from DB) with the CharacterIIR
	//                      built from code (PlayerCharacterIir.BuildBaseMessage) + overridden per character,
	//   - snapshot      -> BuildWorldEntrySnapshot (root from BuildRoot/BuildContainers, tails from BuildFromScratch),
	//   - world objects -> WorldObjectBuilder.TryBuild(row) framed per object.
	// Used by the FAOLAN_BURSTLESS_ENTRY path in RetailLoginReplay; the globals (MapChange/Vitals/GameTime) + the
	// interest NPC spawns are already burst-independent. Not yet in-client verified - the prep for that flip.
	public partial class GameServerListener
	{
		// The beach playfield id MapChange/BuildMapChange emits (0x004FE428); the spawn transform + world objects scope
		// to it. Threaded as one value so the snapshot/spawn/objects agree (per-map sourcing is a follow-up).
		private const uint BeachPlayfieldId = 0x004FE428;

		// Builds + sends the entire world entry from generated/structured pieces - no captured burst (#210). Mirrors
		// SendWorldEntry's world-model setup + record order (MapChange, objects, player spawn, snapshot, vitals,
		// gametime), then runs interest for the NPCs. Beach-shaped (BeachTailsBuilder + BeachPlayfieldId); a non-beach
		// map would get beach tails, so the flag is for a beach login until per-map sourcing lands.
		private async Task SendBurstlessWorldEntry(INetworkClient client, uint mapId)
		{
			var character = client.Session.Character;
			var equipment = await EnsureBeachStarterEquipment(character);
			await EnsureBeachStarterQuest(character);
			var inventoryRows = character != null
				? await Database.GetCharacterInventory(character.Id)
				: [];
			var characterTags = character != null
				? (await Database.GetCharacterTags(character.Id)).OrderBy(t => t).ToArray()
				: [];
			var scaffoldCharacter = await EnsureSpawnScaffoldRows(character);
			var worldObjectRows = await Database.GetWorldObjects(mapId);

			QuestDefinition beachDef = null;
			try
			{
				beachDef = await Database.GetQuestDefinition(BeachStarterQuestId);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Burst-less entry: beach quest definition load failed.");
			}

			if (beachDef is not { Goals.Count: > 0 })
			{
				// The beach starter quest is always seeded (importer SeedBeachQuestGoals), so this is a broken-DB case.
				// The legacy captured-burst replay fallback was removed with the rest of the snapshot-splice path;
				// burst-less entry needs the beach quest definition to author the snapshot, so abort the world entry.
				Logger.LogError("Burst-less world entry (map={Map}): no beach quest definition; cannot build the world entry.", mapId);
				return;
			}

			// Populate the shared world model BEFORE building so the player entity exists for CaptureOwnSpawnBody.
			await _broadcaster.EnsureZoneNpcs(mapId);
			_simulation.RegisterPlayerInWorld(client);

			// Mark this connection as having entered the world. The conversation system (ConversationService), the
			// 0x2020 -> 0x2008 spawn-check (GameServerListener), and the dialogue-close fallback (Handle0x2000) are
			// all gated on this - without it NPC dialogue silently never opens. The internal burst fallback below
			// sets it via ReplayMapWorldBurst.
			client.Session.WorldEntered = true;

			var toSend = new List<byte[]>(worldObjectRows.Length + 5)
			{
				BuildMapChange(client)
			};
			toSend.AddRange(BuildBurstlessWorldObjects(client, worldObjectRows, BeachPlayfieldId));

			var playerSpawn = BuildBurstlessPlayerSpawn(client, equipment, scaffoldCharacter, BeachPlayfieldId);
			if (playerSpawn != null)
			{
				toSend.Add(playerSpawn);
				_broadcaster.CaptureOwnSpawnBody(client, playerSpawn); // so other players can be sent this player's spawn
			}
			else
			{
				Logger.LogWarning("Burst-less entry (map={Map}, '{Name}'): player spawn not built (no scaffold rows?); avatar may not render.", mapId, character?.Name);
			}

			var (snapshotQuests, primaryQuest) = await LoadSnapshotQuests(character, beachDef);
			// #74/#71: the character's learned spells ride the snapshot's known-list so they survive relog + place on the
			// action bar (instead of the per-login DispatchLearnSpell band-aid). Loaded after EnsureStartingLoadout has
			// seeded a fresh character's class spells.
			var knownSpellIds = character != null
				? (await Database.GetCharacterSpells(character.Id)).Select(s => s.SpellId).ToArray()
				: [];
			var snapshot = BuildBurstlessSnapshot(client, equipment, inventoryRows, characterTags, snapshotQuests, primaryQuest, knownSpellIds);
			toSend.Add(snapshot);
			// Record this playfield's despawn token from the snapshot we actually send (WorldDespawn), as the burst path does.
			var snapBodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(snapshot.AsSpan(8, 4));
			_broadcaster.CapturePlayfieldToken(mapId, snapshot.AsSpan(snapBodyStart));

			toSend.Add(BuildVitals(client));
			toSend.Add(BuildGameTime(client));

			foreach (var bytes in toSend)
				await client.SendAsync(bytes);

			Logger.LogInformation("Burst-less world entry (map={Map}, '{Name}'): sent {Count} constructed packets (no burst).", mapId, character?.Name, toSend.Count);

			// Interest spawns the NPCs + this player to nearby players; restore the quest journal.
			await _broadcaster.BroadcastPlayerEntry(client);
			await _broadcaster.RestorePlayerQuests(client);
		}

		// The complete burst-less player FA015807 spawn packet, or null when the character has no scaffold rows yet.
		private byte[] BuildBurstlessPlayerSpawn(INetworkClient client, CharacterEquipment equipment, Character scaffoldCharacter, uint playfield)
		{
			var character = client.Session.Character;
			// The base CharacterIIR is built from code (PlayerCharacterIir, formerly PlayerCharacterIirTemplate.bin, #258);
			// PlayerSpawnMessage overrides name/identity/looks/worn gear per character.
			var message = PlayerCharacterIir.BuildBaseMessage();
			PlayerSpawnMessage.ApplyCharacter(message, character, equipment);

			// A near-zero stored quaternion (the degenerate W=0 default) can't be realised by the avatar; fall back to
			// identity rather than risk it (the live splice path guards the same way).
			var rotation = character.Rotation.LengthSquared() > 0.0001f
				? character.Rotation
				: Quaternion.Identity;

			var body = PlayerSpawnBuilder.BuildFromScratch(client.Session.Account.ClientInstance, playfield, character.Position, rotation, message.ToProtobufBytes(), scaffoldCharacter);
			return body == null
				? null
				: BuildWorldEntrySpawn(client, body);
		}

		// The complete burst-less 0x737A6DF9 snapshot packet (DB containers + from-scratch tails).
		// #255: the player's snapshot-eligible held quests + the primary (tracked) quest. The beach starter is the
		// primary (always held + seeded). A held quest joins only if its definition is snapshot-eligible (Quest_c
		// scalars seeded + the 5 header text refs) - else BuildQuestPortion would throw; those fall to the live
		// RestorePlayerQuests path.
		private async Task<(System.Collections.Generic.List<QuestDefinition> Quests, QuestDefinition Primary)> LoadSnapshotQuests(Character character, QuestDefinition beachDef)
		{
			var quests = new System.Collections.Generic.List<QuestDefinition> { beachDef };
			if (character != null)
				foreach (var cq in await Database.GetCharacterQuests(character.Id))
				{
					if (cq.QuestId == BeachStarterQuestId)
						continue;
					var def = await Database.GetQuestDefinition(cq.QuestId);
					if (BeachTailsBuilder.IsSnapshotEligible(def))
						quests.Add(def);
				}

			return (quests, beachDef);
		}

		private byte[] BuildBurstlessSnapshot(INetworkClient client, CharacterEquipment equipment, CharacterInventory[] inventoryRows, uint[] characterTags, System.Collections.Generic.IReadOnlyList<QuestDefinition> snapshotQuests, QuestDefinition primaryQuest, IReadOnlyList<uint> knownSpellIds)
		{
			var instance = client.Session.Account.ClientInstance;
			var root = BuildBurstlessSnapshotRoot(instance, equipment, inventoryRows, characterTags);
			var tails = BeachTailsBuilder.BuildFromScratch(snapshotQuests, primaryQuest, client.Session.Character, instance, null, knownSpellIds);
			return BuildWorldEntrySnapshot(client, root, tails);
		}

		// The FullCharacterIIR root (containers) as protobuf bytes - the same typed authoring BuildBeachInventoryPacket
		// does, but produced standalone (no captured packet to splice into).
		private static byte[] BuildBurstlessSnapshotRoot(uint instance, CharacterEquipment equipment, CharacterInventory[] inventoryRows, uint[] characterTags)
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

			var typedRoot = FullCharSnapshotBuilder.BuildRoot(
				characterTags,
				FullCharSnapshotBuilder.FullExpansionFlags,
				FullCharSnapshotBuilder.FullSubscriptionFlags,
				false,
				true,
				FullCharSnapshotBuilder.BuildContainers(instance, equipped, bagFirst, inventoryRows ?? []));
			return typedRoot.ToProtobufBytes();
		}

		// The burst-less world-object packets: each WorldObjects row built via WorldObjectBuilder and framed. Built on
		// the row's captured handle + the live playfield instance (the rewrite the live splice path applies).
		private IEnumerable<byte[]> BuildBurstlessWorldObjects(INetworkClient client, IReadOnlyList<WorldObject> rows, uint playfieldInstance)
		{
			foreach (var row in rows)
			{
				if (WorldObjectBuilder.TryBuild(row, row.CapturedHandle, playfieldInstance, out var body))
					yield return FrameWorldEntryRecord(client, body);
			}
		}
	}
}
