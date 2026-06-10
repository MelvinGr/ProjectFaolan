using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Faolan.GameServer.Spawn
{
	// Builds a 96C46740 MoveTask body - the per-entity movement registration paired with each FA015807 spawn
	// (without it the client never polls 0x2020 for the entity). The full form carries a multi-segment patrol
	// path; the MINIMAL stationary form - confirmed constant across 58 captured instances - is just the entity's
	// position, which is all a non-patrolling NPC needs and is constructible from data we already have. NOTE: the
	// MoveTask floats are BIG-ENDIAN, unlike the spawn transform's little-endian (docs/spawn-dissection-plan.md).
	//
	//   [u32 bodyLen=27][u32 0x96C46740][u32 ack=0xC350][u32 handle][00 00][vec3 pos (3x BE f32)][06]
	public static class MoveTaskBuilder
	{
		public const uint SubOpcode = 0x96C46740;
		private const uint Ack = 0x0000C350;
		private const int BodyLength = 31;

		public static byte[] BuildStationary(uint handle, Vector3 position)
		{
			var body = new byte[BodyLength];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), BodyLength - 4);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), SubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), Ack);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), handle);
			// body[16..18] stay 0x00 0x00 (the prefix)
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(18, 4), position.X);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(22, 4), position.Y);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(26, 4), position.Z);
			body[30] = 0x06; // stationary terminator (vs 0x04 = a movement segment follows)
			return body;
		}
	}
}
