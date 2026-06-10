using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Spawn
{
	// Bridges a stored NpcSpawn row to a wire FA015807 (SimpleCharFullUpdate) body, addressed to a live session
	// handle. The transform (position/rotation) is driven by the row's structured columns; the CharacterIIR
	// (identity/appearance/equipment) and the still-raw pre-IIR float block + post-IIR scaffold come from the
	// row's captured `Data` (docs/spawn-dissection-plan.md Phase 4 - those two blocks are the last un-lifted part).
	// Byte-identical to the captured body when the row's columns match the capture (NpcSpawnBuilderTests); this is
	// the unit the world-entry path will call per map instead of replaying the MapWorldBurst blob.
	public static class NpcSpawnBuilder
	{
		// Produces the FA015807 body for `row`, addressed to `handle`. Returns false if the row has no usable
		// captured body (no Data / no decodable CharacterIIR).
		public static bool TryBuild(NpcSpawn row, uint handle, out byte[] body)
		{
			body = null;
			var template = ExtractBody(row?.Data);
			if (template == null || !PlayerSpawnReader.TryReadCharacterMessage(template, out var read))
				return false;

			var position = new Vector3(row.PositionX, row.PositionY, row.PositionZ);
			var rotation = new Quaternion(row.RotationX, row.RotationY, row.RotationZ, row.RotationW);
			body = SpawnPacketBuilder.BuildSpawn(template, handle, position, rotation, read.Message);
			return true;
		}

		// The captured `Data` is the full Conan packet ([u32 len][u32 crc][u32 headerLen][header][body]); the
		// FA015807 body starts after the 12-byte prefix + the header. Returns null if the framing is malformed.
		private static byte[] ExtractBody(byte[] data)
		{
			if (data == null || data.Length < 12)
				return null;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= data.Length
				? data[bodyStart..]
				: null;
		}
	}
}
