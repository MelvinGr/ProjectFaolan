using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Spawn
{
	// Bridges a stored WorldObject row to a wire body (66AEDD50/F096251E/33A56FB0), addressed to a live session
	// handle. Sibling of NpcSpawnBuilder, but simpler: a world object has no CharacterIIR to splice, so only the
	// transform is rebuilt (from the row's columns) - the object message + raw tail are carried over from the
	// row's captured Data. The sub-opcode is preserved from the template, so one builder handles all three
	// classes. Byte-identical to the captured body when the row's columns match the capture (WorldObjectBuilderTests).
	public static class WorldObjectBuilder
	{
		public static bool TryBuild(WorldObject row, uint handle, out byte[] body)
		{
			body = null;
			var template = ExtractBody(row?.Data);
			if (template == null || template.Length < SpawnPacketBuilder.RotWOffset + 4)
				return false;

			var position = new Vector3(row.PositionX, row.PositionY, row.PositionZ);
			var rotation = new Quaternion(row.RotationX, row.RotationY, row.RotationZ, row.RotationW);
			body = SpawnPacketBuilder.BuildSpawnBody(template, handle, position, rotation);
			return true;
		}

		// The captured `Data` is the full Conan packet ([u32 len][u32 crc][u32 headerLen][header][body]); the
		// object body starts after the 12-byte prefix + the header. Returns null if the framing is malformed.
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
