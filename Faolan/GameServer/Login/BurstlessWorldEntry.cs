using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
	//                      overridden onto the one server-owned template (PlayerCharacterIirTemplate) per character,
	//   - snapshot      -> BuildWorldEntrySnapshot (root from BuildRoot/BuildContainers, tails from BuildFromScratch),
	//   - world objects -> WorldObjectBuilder.TryBuild(row) framed per object.
	// Used by the FAOLAN_BURSTLESS_ENTRY path in RetailLoginReplay; the globals (MapChange/Vitals/GameTime) + the
	// interest NPC spawns are already burst-independent. Not yet in-client verified - the prep for that flip.
	public partial class GameServerListener
	{
		// The beach playfield id MapChange/BuildMapChange emits (0x004FE428); the spawn transform + world objects scope
		// to it. Threaded as one value so the snapshot/spawn/objects agree (per-map sourcing is a follow-up).
		private const uint BeachPlayfieldId = 0x004FE428;

		// Opt-in flag for the burst-less world entry (#210). Default off: the path is built + unit-tested but NOT yet
		// verified in-client, and is beach-shaped (the tails + playfield are beach values). Flip to test a beach login.
		private static readonly bool BurstlessEntryEnabled =
			string.Equals(Environment.GetEnvironmentVariable("FAOLAN_BURSTLESS_ENTRY"), "1", StringComparison.Ordinal);

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
				Logger.LogError("Burst-less world entry (map={Map}): no beach quest definition; falling back to the burst path.", mapId);
				var fallback = await Database.GetMapWorldBurst(mapId) ?? await Database.GetMapWorldBurst(DefaultWorldMapId);
				if (fallback != null)
				{
					await _broadcaster.EnsureZoneNpcs(mapId);
					_simulation.RegisterPlayerInWorld(client);
					await ReplayMapWorldBurst(client, fallback, mapId);
					await _broadcaster.BroadcastPlayerEntry(client);
					await _broadcaster.RestorePlayerQuests(client);
				}

				return;
			}

			// Populate the shared world model BEFORE building so the player entity exists for CaptureOwnSpawnBody.
			await _broadcaster.EnsureZoneNpcs(mapId);
			_simulation.RegisterPlayerInWorld(client);

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

			var snapshot = BuildBurstlessSnapshot(client, equipment, inventoryRows, characterTags, beachDef);
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

		// Server-owned player CharacterIIR template (Strie's 469B message); PlayerSpawnMessage overrides its
		// identity/looks/equipment per character. The one templated piece of the otherwise-generated player body.
		private static readonly byte[] PlayerCharacterIirTemplate = LoadEmbeddedAsset("PlayerCharacterIirTemplate.bin");

		// True when the embedded CharacterIIR template is present (always, once embedded) - guards the player spawn.
		private static bool BurstlessAvailable => PlayerCharacterIirTemplate.Length > 0 && BeachTailsBuilder.IsAvailable;

		// The complete burst-less player FA015807 spawn packet, or null when the character has no scaffold rows yet.
		private byte[] BuildBurstlessPlayerSpawn(INetworkClient client, CharacterEquipment equipment, Character scaffoldCharacter, uint playfield)
		{
			if (!BurstlessAvailable)
				return null;

			var character = client.Session.Character;
			var message = ((ReadOnlySpan<byte>)PlayerCharacterIirTemplate).FromProtobuf<ProtoCoreCharacterIIR>();
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
		private byte[] BuildBurstlessSnapshot(INetworkClient client, CharacterEquipment equipment, CharacterInventory[] inventoryRows, uint[] characterTags, QuestDefinition beachDef)
		{
			var instance = client.Session.Account.ClientInstance;
			var root = BuildBurstlessSnapshotRoot(instance, equipment, inventoryRows, characterTags);
			var tails = BeachTailsBuilder.BuildFromScratch(beachDef, client.Session.Character, instance);
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

		private static byte[] LoadEmbeddedAsset(string fileName)
		{
			var assembly = Assembly.GetExecutingAssembly();
			var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
			if (name == null)
				return [];

			using var stream = assembly.GetManifestResourceStream(name);
			if (stream == null)
				return [];

			using var ms = new MemoryStream();
			stream.CopyTo(ms);
			return ms.ToArray();
		}
	}
}
