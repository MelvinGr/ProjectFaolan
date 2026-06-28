using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;

namespace Faolan.Importer.Extractors
{
	public sealed record NpcSpawnEntry(uint MapId, string Name, uint Handle, float X, float Y, float Z, byte[] Data)
	{
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
		public uint TemplateId { get; init; }

		// The humanoid appearance/morph table lifted from the post-IIR (#185), empty for animals. Lets the spawn's
		// post-IIR generate from rows instead of the Data blob.
		public IReadOnlyList<NpcAppearanceMorph> Morphs { get; init; } = [];

		// The worn equipment lifted from the CharacterIIR f23 (#185), empty for unarmed NPCs and for any NPC whose
		// items don't match the reproducible ProtoAcgItem shape (then equipment stays sourced from the Data blob).
		public IReadOnlyList<NpcSpawnEquipment> Equipment { get; init; } = [];
	}

	// Extracts NPC entity spawns (FA015807) per map from a capture: tracks the current map (MapChange 5D85BFC7),
	// then for each spawn reads the handle (body@12), transform (position @24/29/34 + rotation @41.., LE) and the
	// identity/appearance lifted from the CharacterIIR (ProtoCoreCharacterIIR via PlayerSpawnReader - the same
	// decode the player spawn uses). The player's own spawn is skipped - its handle is the session client instance
	// (>= 0x02000000), whereas NPC handles are small. Dedup key is (map, position); the handle/name vary.
	public static class NpcSpawnExtractor
	{
		private const uint InstanceRangeStart = 0x02000000; // player/own-entity handles live in the instance range
		private const uint MapChangeSubOpcode = 0x5D85BFC7;
		private const uint Opcode2000 = 0x2000;
		private const uint Opcode2020 = 0x2020; // client spawn poll - ends the pre-poll login burst window
		private const uint SpawnSubOpcode = 0xFA015807;

		// Every FA015807 spawn in the capture (login burst AND mid-session interest/respawn spawns), all maps. This
		// accretes the cross-session UNION - the same physical NPC at slightly different spots across sessions - so
		// the live world over-populates (~4x retail). Use ExtractLoginBurst for the clean retail-shaped set.
		public static List<NpcSpawnEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<NpcSpawnEntry>();
			uint currentMap = 0;

			foreach (var p in packets)
			{
				if (p.Opcode != Opcode2000)
					continue;

				var body = GetBody(p.Bytes);
				if (body.Length < 38)
					continue;

				var subop = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4));

				if (subop == MapChangeSubOpcode)
				{
					currentMap = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
					continue;
				}

				if (subop != SpawnSubOpcode || currentMap == 0)
					continue;

				if (TryBuildEntry(body, p.Bytes, currentMap, out var entry))
					entries.Add(entry);
			}

			return entries;
		}

		// Only the NPCs of a map's LOGIN WORLD BUILD: the FA015807 spawns in the pre-poll burst window (a MapChange
		// up to the client's first 0x2020 spawn poll - the same window MapWorldBurstExtractor delimits). This is the
		// retail-shaped set as it arrives on world entry; it EXCLUDES the mid-session interest/respawn spawns that
		// otherwise accrete duplicates (Gitea #185 / #244). `allowedMaps` (null = all) restricts which maps to lift.
		public static List<NpcSpawnEntry> ExtractLoginBurst(IReadOnlyList<CapturedPacket> packets, ISet<uint> allowedMaps = null)
		{
			var entries = new List<NpcSpawnEntry>();
			uint currentMap = 0;
			var sawPoll = false; // the first 0x2020 poll ends the pre-poll spawn burst

			foreach (var p in packets)
			{
				var body = GetBody(p.Bytes);
				var subop = body.Length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) : 0;

				if (p.Opcode == Opcode2000 && p.IsDownload && subop == MapChangeSubOpcode && body.Length >= 38)
				{
					currentMap = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
					sawPoll = false;
					continue;
				}

				if (currentMap == 0)
					continue;
				if (p.Opcode == Opcode2020) // client spawn poll - ends the pre-poll burst window
				{
					sawPoll = true;
					continue;
				}

				if (sawPoll || p.Opcode != Opcode2000 || !p.IsDownload || subop != SpawnSubOpcode)
					continue;
				if (allowedMaps != null && !allowedMaps.Contains(currentMap))
					continue;

				if (TryBuildEntry(body, p.Bytes, currentMap, out var entry))
					entries.Add(entry);
			}

			return entries;
		}

		// Builds one NPC spawn entry from an FA015807 body (transform + lifted identity/appearance/equipment). Returns
		// false for a body too short to hold the transform, or the player's own spawn (handle in the instance range).
		private static bool TryBuildEntry(byte[] body, byte[] fullBytes, uint currentMap, out NpcSpawnEntry entry)
		{
			entry = null;
			if (body.Length < SpawnPacketBuilder.RotWOffset + 4)
				return false; // too short to hold the transform (position + rotation)

			var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
			if (handle >= InstanceRangeStart)
				return false; // the player's own spawn

			var identity = LiftIdentity(body);
			var name = !string.IsNullOrEmpty(identity.Name)
				? identity.Name
				: ExtractName(body);

			// Humanoid appearance/morph table (#185) - decoded from the post-IIR; empty for animals.
			var morphs = NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var decoded)
				? decoded
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

		// Dedup by (map, spawn point); keep the first seen.
		public static List<NpcSpawnEntry> Merge(IEnumerable<NpcSpawnEntry> all)
		{
			var seen = new Dictionary<(uint, float, float, float), NpcSpawnEntry>();
			foreach (var e in all)
				seen.TryAdd((e.MapId, e.X, e.Y, e.Z), e);

			return seen.Values.ToList();
		}

		// The entity name is a `0A <len> <utf8>` field whose offset varies; scan past the position block for the
		// first plausible printable-ASCII run. Best-effort (display only - not the dedup key).
		private static string ExtractName(byte[] body)
		{
			for (var i = 40; i + 2 < body.Length; i++)
			{
				if (body[i] != 0x0A)
					continue;

				int len = body[i + 1];
				if (len < 2 || len > 40 || i + 2 + len > body.Length)
					continue;

				var slice = body.AsSpan(i + 2, len);
				if (IsPrintableName(slice))
					return Encoding.ASCII.GetString(slice);
			}

			return string.Empty;
		}

		private static byte[] GetBody(byte[] full)
		{
			if (full.Length < 12)
				return [];

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= full.Length
				? full[bodyStart..]
				: [];
		}

		private static bool IsPrintableName(ReadOnlySpan<byte> s)
		{
			var hasLetter = false;
			foreach (var c in s)
			{
				if (c < 32 || c > 126)
					return false;

				if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
					hasLetter = true;
			}

			return hasLetter;
		}

		// Decodes the CharacterIIR (ProtoCoreCharacterIIR) and lifts the NPC identity/appearance. Returns an empty
		// entry (all defaults) if the body has no decodable IIR - the offset-based transform/name path still works.
		private static NpcSpawnEntry LiftIdentity(byte[] body)
		{
			var empty = new NpcSpawnEntry(0, string.Empty, 0, 0, 0, 0, null);
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return empty;

			var msg = read.Message;
			var stats = msg.Stats;
			var wireHealth = stats == null ? 0 : stats.Health != 0 ? stats.Health : stats.Life;

			// Lift worn equipment (#185) - but only when EVERY item matches the reproducible ProtoAcgItem shape, so
			// the builder reproduces the f23 list byte-for-byte; otherwise leave equipment to the Data blob (no rows).
			var equipment = new List<NpcSpawnEquipment>();
			foreach (var entry in msg.InventoryEntries)
			{
				if (!NpcSpawnEquipmentBuilder.TryLift(entry, out var equipRow))
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
					: 0, // ×100 scale; ignore absent/implausible
				Community = msg.npc_stats?.Community ?? 0
			};
		}

		private static float ReadLE(byte[] body, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(offset, 4));
		}
	}
}
