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

		// The CLASS this placement belongs to, on a class-specific map (Map.ClassSpecific - the city armories).
		// Stamped by BuildBurstTruthSpawns from the class of the capture's own played character; null everywhere else.
		public ConanClass? ClassId { get; init; }

		// The humanoid appearance/morph table lifted from the post-IIR (#185), empty for animals. Lets the spawn's
		// post-IIR generate from rows instead of the Data blob.
		public IReadOnlyList<NpcAppearanceMorph> Morphs { get; init; } = [];

		// The worn equipment lifted from the CharacterIIR f23 (#185), empty for unarmed NPCs and for any NPC whose
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
		// The client's spawn poll - ends the pre-poll login burst window. A WIRE opcode (the method tag 0x20 plus the
		// method's first varint group) rather than a named method: the class it is sent to has never been pinned, and a
		// method index is only a name once you know which interface it indexes (#615).
		private const uint SpawnPollWireOpcode = 0x2020;

		// Every FA015807 spawn in the capture (login burst AND mid-session interest/respawn spawns), all maps. This
		// accretes the cross-session UNION - the same physical NPC at slightly different spots across sessions - so
		// the live world over-populates (~4x retail). Use ExtractLoginBurst for the clean retail-shaped set.
		public static List<NpcSpawnEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<NpcSpawnEntry>();
			uint currentMap = 0;

			foreach (var p in packets)
			{
				if (!p.IsIirCarrier)
					continue;

				if (p.SubOpcode == PlayfieldFCodec.Opcode)
				{
					if (p.TryParse(PlayfieldFCodec.Parse, out var change))
						currentMap = change.MapId;
					continue;
				}

				if (p.SubOpcode != SimpleCharFullUpdateCodec.Opcode || currentMap == 0)
					continue;

				if (TryBuildEntry(p.Body, p.Bytes, currentMap, out var entry))
					entries.Add(entry);
			}

			return entries;
		}

		// Only the NPCs of a map's LOGIN WORLD BUILD: the FA015807 spawns in the pre-poll burst window (a MapChange
		// up to the client's first 0x2020 spawn poll - the same window MapWorldBurstExtractor delimits). This is the
		// retail-shaped set as it arrives on world entry; it EXCLUDES the mid-session interest/respawn spawns that
		// otherwise accrete duplicates (Gitea #185 / #244). `allowedMaps` (null = all) restricts which maps to lift.
		public static List<NpcSpawnEntry> ExtractLoginBurst(IReadOnlyList<CapturedPacket> packets, ISet<uint>? allowedMaps = null)
		{
			var entries = new List<NpcSpawnEntry>();
			uint currentMap = 0;
			var sawPoll = false; // the first 0x2020 poll ends the pre-poll spawn burst

			foreach (var p in packets)
			{
				if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
				{
					if (!p.TryParse(PlayfieldFCodec.Parse, out var change))
						continue;

					currentMap = change.MapId;
					sawPoll = false;
					continue;
				}

				if (currentMap == 0)
					continue;

				if (p.WireOpcode == SpawnPollWireOpcode) // client spawn poll - ends the pre-poll burst window
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
			// Keyed by (map, position, CLASS): on a class-specific map two classes' quartermasters legitimately stand
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

			// Lift worn equipment (#185) - but only when EVERY item matches the reproducible ProtoAcgItem shape, so
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

		// The transform floats, at the offsets `SpawnPacketBuilder` publishes. Those offsets are CODEC-owned and
		// deferred by #530 §7 along with the captured template they patch - this reads them rather than declaring its
		// own. The whole-record route (`SpawnBodyReader.TryParse`) is deliberately not taken here: it is strict, and
		// the rest of TryBuildEntry is built to lift identity/appearance out of bodies the strict codec rejects
		// (#243's mid-session humanoids), so gating the entry on it would drop spawns rather than decode them.
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

			// Another visible PLAYER's spawn (not the own player - a different account in view): it carries
			// player_stats + no npc_stats, so it isn't an NPC. These were previously imported as phantom NpcSpawn
			// rows with TemplateId 0 / no type (found via the CoreChar-header X RE, #244) - drop them here.
			if (SpawnBodyReader.TryReadCharacterMessage(body, out var pr) && pr.Message.player_stats != null)
				return false;

			// The name comes off the CoreCharacter message and nowhere else (#541). This used to fall back to a
			// lenient byte scan for the name field; measured over the whole corpus, that fallback NEVER fires - of
			// 11632 captured char spawns the scan and the message reader name the same 11631, and the one they
			// disagree on is the scan getting it wrong (a 1-character "\" where the message says "Crocodile").
			var identity = LiftIdentity(body);
			var name = identity.Name;

			// Humanoid appearance/morph table (#185) - decoded from the post-IIR; empty for animals. The clean shape
			// decodes directly; a mid-session humanoid capture carries the SAME table behind a runtime record1, lifted
			// by the lenient decode (#243) so those spawns get morph rows too (and build from scratch, not Data).
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
