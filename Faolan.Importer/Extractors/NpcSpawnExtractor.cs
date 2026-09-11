using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	public sealed record NpcSpawnEntry(uint MapId, string Name, uint Handle, float X, float Y, float Z, byte[] Data)
	{
		public uint TemplateId { get; init; }

		// Where in the capture this spawn arrived (the index into the packet list `Extract` was given). Needed
		// because HANDLES are RECYCLED: 91 handles in the corpus carry two different templates over one capture, so
		// "what was spawned on handle H" is only answerable at A point in time. A consumer correlating a later packet
		// to an NPC must take the most recent spawn before it, not the first or the last one overall.
		// Left 0 by the extractors that do not walk the whole packet list.
		public int SourceIndex { get; init; }

		// The class this placement belongs to, on a class-specific map (Map.ClassSpecific - the city armories).
		// Stamped by BuildBurstTruthSpawns from the class of the capture's own played character; null everywhere else.
		public ConanClass? ClassId { get; init; }

		// The humanoid appearance/morph table lifted from the post-IIR, empty for animals. Lets the spawn's
		// post-IIR generate from rows instead of the Data blob.
		public IReadOnlyList<AppearanceEntry> Morphs { get; init; } = [];

		// The worn equipment lifted from the CharacterIIR f23, empty for unarmed NPCs and for any NPC whose
		// items don't match the reproducible ProtoAcgItem shape (then equipment stays sourced from the Data blob).
		public IReadOnlyList<NpcSpawnEquipment> Equipment { get; init; } = [];
		public bool HasEquipment { get; init; }
		public float RotW { get; init; }
		public float RotX { get; init; }
		public float RotY { get; init; }
		public float RotZ { get; init; }

		// NpcStats.community = the faction id: 0 = neutral/friendly, > 0 = a hostile faction (drives aggro hostility).
		public uint Community { get; init; }
		public uint HeadMesh { get; init; }

		// Max health (player-facing; wire CharacterStats.health/life is ×100) - the real per-NPC combat HP.
		public uint Health { get; init; }
		public uint Level { get; init; }
		public uint MonsterData { get; init; }
		public uint MonsterScale { get; init; }
		public uint Race { get; init; }
		public uint Sex { get; init; }
	}

	// Extracts NPC entity spawns (FA015807) per map from a capture: tracks the current map (MapChange 5D85BFC7),
	// then for each spawn reads the handle (body@12), transform (position @24/29/34 + rotation @41.., LE) and the
	// identity/appearance lifted from the CharacterIIR (ProtoCoreCharacterIIR via SpawnBodyReader - the same
	// decode the player spawn uses). The player's own spawn is skipped - its handle is the session client instance
	// (>= 0x02000000), whereas NPC handles are small. Dedup key is (map, position); the handle/name vary.
	public static class NpcSpawnExtractor
	{
		// What ends the pre-poll login burst window. Named at last: on the 26,011 captured c2s frames carrying
		// this the RECEIVER class is `GameCharAgent` (0xA9ECB95D), so index 0x20 indexes GameCharAgent - and the
		// client's own method table calls that `UpdateQuestMarkerNPC`. The body agrees: it is two ID32s, the second
		// constant per capture (the player's own instance) and the first a handle that matches an FA015807 spawn from
		// the same capture in 1,054 of 1,137 cases (the residue spawned before the capture opened). So it is the
		// client asking "what quest marker do I draw over this NPC, for me" once per NPC it has taken delivery of -
		// which is why it reads like a spawn poll and why the first one marks the end of the burst.
		//
		// The direction filter is load-bearing, not decoration. 0x2020 is the wire rendering of index 0x20 on
		// whichever interface the frame is addressed to, and s2c that is `GameCharInterface.DispatchAddItem` - 165 of
		// them in the corpus. Without the c2s test an item delivery during world entry would close the burst window
		// early and truncate the map's spawn set.
		internal static readonly uint QuestMarkerQueryWireOpcode = CapturedPacket.Wire(GameCharAgentMethods.UpdateQuestMarkerNPC);

		// Every FA015807 spawn in the capture (login burst and mid-session interest/respawn spawns), all maps. This
		// accretes the cross-session UNION - the same physical NPC at slightly different spots across sessions - so
		// the live world over-populates (~4x retail). Use ExtractLoginBurst for the clean retail-shaped set.
		public static List<NpcSpawnEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<NpcSpawnEntry>();
			uint currentMap = 0;

			for (var i = 0; i < packets.Count; i++)
			{
				var p = packets[i];
				if (!p.IsIirCarrier)
					continue;

				if (p.SubOpcode == PlayfieldFCodec.Opcode)
				{
					// A MapChange we cannot read must clear the map, never leave the previous one standing.
					// Keeping it silently re-attributes everything that follows to the playfield the player just LEFT:
					// when PlayfieldFCodec refused the zero-scene-entry body, White Sands Night's NPCs were imported
					// as City of Tortage Night spawns, and the row counts looked like a healthy +50.
					//
					// And an EPIC-mode window must be skipped outright: the proxy's class word says which
					// difficulty this copy is, and epic re-tunes the same templates - measured on chat-annotated-3's
					// back-to-back Spider Caverns entries, Cavecrawler/Cavehunter carry ~8x the health at the same
					// name, template and level (147,600 -> 1,186,900 at L27), minion-type Spiderlings ~+6%. Harvesting
					// an epic window would write those into the NORMAL stat columns; only first-record-wins ordering
					// has kept them out so far.
					currentMap = p.TryParse(PlayfieldFCodec.Parse, out var change) && !change.Proxy.IsEpic ? change.MapId : 0;
					continue;
				}

				if (p.SubOpcode != SimpleCharFullUpdateCodec.Opcode || currentMap == 0)
					continue;

				if (TryBuildEntry(p.Body, p.Bytes, currentMap, out var entry))
					entries.Add(entry with { SourceIndex = i });
			}

			return entries;
		}

		// Only the NPCs of a map's LOGIN WORLD build: the FA015807 spawns in the pre-poll burst window (a MapChange
		// up to the client's first UpdateQuestMarkerNPC query - the same window MapWorldBurstExtractor delimits). This is the
		// retail-shaped set as it arrives on world entry; it EXCLUDES the mid-session interest/respawn spawns that
		// otherwise accrete duplicates. `allowedMaps` (null = all) restricts which maps to lift.
		public static List<NpcSpawnEntry> ExtractLoginBurst(IReadOnlyList<CapturedPacket> packets, ISet<uint>? allowedMaps = null)
		{
			var entries = new List<NpcSpawnEntry>();
			uint currentMap = 0;
			var sawPoll = false; // the first quest-marker query ends the pre-poll spawn burst

			foreach (var p in packets)
			{
				if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
				{
					// Clear rather than keep the previous map on an unreadable MapChange - see Extract, and
					// skip an EPIC-mode window outright - epic re-tunes the same templates (~8x health), see Extract.
					currentMap = p.TryParse(PlayfieldFCodec.Parse, out var change) && !change.Proxy.IsEpic ? change.MapId : 0;
					sawPoll = false;
					continue;
				}

				if (currentMap == 0)
					continue;

				if (!p.IsDownload && p.WireOpcode == QuestMarkerQueryWireOpcode) // the client's quest-marker query - ends the pre-poll burst window
				{
					sawPoll = true;
					continue;
				}

				if (sawPoll || p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
					continue;
				if (allowedMaps != null && !allowedMaps.Contains(currentMap))
					continue;

				if (TryBuildEntry(p.Body, p.Bytes, currentMap, out var entry))
					entries.Add(entry);
			}

			return entries;
		}

		// Dedup by (map, spawn point); keep the first seen.
		public static List<NpcSpawnEntry> Merge(IEnumerable<NpcSpawnEntry> all)
		{
			// Keyed by (map, position, class): on a class-specific map two classes' quartermasters legitimately stand
			// on the same spot, and collapsing them would silently drop one class's set. Null class (every ordinary
			// map) keeps the historical (map, position) behaviour.
			var seen = new Dictionary<(uint, float, float, float, ConanClass?), NpcSpawnEntry>();
			foreach (var e in all)
				seen.TryAdd((e.MapId, e.X, e.Y, e.Z, e.ClassId), e);

			return seen.Values.ToList();
		}

		// The entity name is a `0A <len> <utf8>` field whose offset varies; scan past the position block for the
		// first plausible printable-ASCII run. Best-effort (display only - not the dedup key).



		// Decodes the CharacterIIR (ProtoCoreCharacterIIR) and lifts the NPC identity/appearance. Returns an empty
		// entry (all defaults) if the body has no decodable IIR - the offset-based transform/name path still works.
		private static NpcSpawnEntry LiftIdentity(byte[] body)
		{
			var empty = new NpcSpawnEntry(0, string.Empty, 0, 0, 0, 0, null!);
			if (!SpawnBodyReader.TryReadCharacterMessage(body, out var read))
				return empty;

			var msg = read.Message;
			var stats = msg.Stats;
			var wireHealth = stats == null
				? 0
				: stats.Health != 0
					? stats.Health
					: stats.Life;

			// Lift worn equipment - but only when every item matches the reproducible ProtoAcgItem shape, so
			// the builder reproduces the f23 list byte-for-byte; otherwise leave equipment to the Data blob (no rows).
			var equipment = new List<NpcSpawnEquipment>();
			foreach (var entry in msg.InventoryEntries)
			{
				if (!NpcSpawnEquipmentBuilder.TryLift<NpcSpawnEquipment>(entry, out var equipRow))
				{
					equipment.Clear();
					break;
				}

				equipment.Add(equipRow);
			}

			return empty with
			{
				Equipment = equipment,
				Name = msg.Name ?? string.Empty,
				TemplateId = msg.npc_stats?.TemplateId ?? 0,
				// NpcStats f4 (fixed32) = the stable per-type identity
				Race = stats?.Race ?? 0,
				Sex = stats?.Sex ?? 0,
				Level = stats?.Level ?? 0,
				MonsterScale = stats?.MonsterScale ?? 0,
				MonsterData = stats?.MonsterData ?? 0,
				HeadMesh = stats?.HeadMesh ?? 0,
				HasEquipment = msg.InventoryEntries.Count > 0,
				Health = wireHealth >= 100
					? wireHealth / 100
					: 0,
				// ×100 scale; ignore absent/implausible
				Community = msg.npc_stats?.Community ?? 0
			};
		}

		// The transform floats, at the offsets `SpawnPacketBuilder` publishes. Those offsets are codec-owned, so this
		// reads them rather than declaring its own. The whole-record route (`SpawnBodyReader.TryParse`) is
		// deliberately not taken here: it is strict, and the rest of TryBuildEntry is built to lift
		// identity/appearance out of bodies the strict codec rejects - the mid-session humanoids - so gating the
		// entry on it would drop spawns rather than decode them.
		private static float ReadLE(byte[] body, int offset)
		{
			return body.ReadF32Le(offset);
		}

		// Builds one NPC spawn entry from an FA015807 body (transform + lifted identity/appearance/equipment). Returns
		// false for a body too short to hold the transform, or the player's own spawn (handle in the instance range).
		private static bool TryBuildEntry(byte[] body, byte[] fullBytes, uint currentMap, out NpcSpawnEntry entry)
		{
			entry = null!;
			if (body.Length < SpawnPacketBuilder.RotWOffset + 4)
				return false; // too short to hold the transform (position + rotation)

			var handle = body.ReadU32Be(12);
			if (handle >= EntityHandles.CharacterInstanceBase)
				return false; // the player's own spawn

			// Another visible player's spawn (not the own player - a different account in view): it carries
			// player_stats and no npc_stats, so it is not an NPC. Without this it becomes a phantom NpcSpawn row with
			// TemplateId 0 and no type.
			if (SpawnBodyReader.TryReadCharacterMessage(body, out var pr) && pr.Message.player_stats != null)
				return false;

			// The name comes off the CoreCharacter message and nowhere else. A lenient byte scan for the name field
			// adds nothing: over the whole corpus the scan and the message reader name the same spawns bar one, and
			// on that one the scan is wrong (a 1-character "\" where the message says "Crocodile").
			var identity = LiftIdentity(body);
			var name = identity.Name;

			// Humanoid appearance/morph table - decoded from the post-IIR; empty for animals. The clean shape
			// decodes directly; a mid-session humanoid capture carries the same table behind a runtime record1, lifted
			// by the lenient decode so those spawns get morph rows too (and build from scratch, not Data).
			var morphs = NpcSpawnAppearance.TryLift(body, out var decoded)
				? decoded
				: NpcSpawnAppearance.TryLiftLenient(body, out var lenient)
					? lenient
					: [];

			entry = identity with
			{
				MapId = currentMap,
				Name = name,
				Handle = handle,
				X = ReadLE(body, SpawnPacketBuilder.PosXOffset),
				Y = ReadLE(body, SpawnPacketBuilder.PosYOffset),
				Z = ReadLE(body, SpawnPacketBuilder.PosZOffset),
				Data = fullBytes,
				RotX = ReadLE(body, SpawnPacketBuilder.RotXOffset),
				RotY = ReadLE(body, SpawnPacketBuilder.RotYOffset),
				RotZ = ReadLE(body, SpawnPacketBuilder.RotZOffset),
				RotW = ReadLE(body, SpawnPacketBuilder.RotWOffset),
				Morphs = morphs
			};
			return true;
		}
	}
}
