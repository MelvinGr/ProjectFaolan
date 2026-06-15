using System;
using System.Buffers.Binary;
using System.Text;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// Loot-bag interaction packets, decoded from captures (see protocol_loot_interaction). Loot bags use their own
	// typetag 0xC87A (not the 0xC350 the entity family uses). These build the 0x2000 IIR record bodies (the bytes after
	// the u32 length prefix).
	public static class WorldLoot
	{
		public const float DefaultFadeSeconds = 3.0f;
		public const uint EntityTypeTag = 0x0000C350; // the entity-frame typetag carried inside NewCurrentLooter
		public const uint FadeSubOpcode = (uint)GameServer0X2000RespondsOpcodes.N3ToClientFadeIIR; // n3ToClientFadeIIR_t - graceful fade-out despawn
		public const uint LootTypeTag = 0x0000C87A; // the loot-bag class typetag
		public const uint NewCurrentLooterSubOpcode = (uint)GameServer0X2000RespondsOpcodes.NewCurrentLooterIIR; // NewCurrentLooterIIR_c
		public const uint VisualChangeSubOpcode = (uint)GameServer0X2000RespondsOpcodes.SimpleDynelVisualChangeIIR; // SimpleDynelVisualChangeIIR_c - "Open" opens the loot window

		// The graceful fade-out despawn (the loot-consumed / loot-expired variant of n3ToClientQuit). 17 B:
		// [subop][typetag][handle][0x00 marker][float fadeSeconds, big-endian]. Marker 0x00 + a FLOAT distinguishes it
		// from n3ToClientQuit's 0x01 marker + u32 playfield token.
		public static byte[] BuildFadeBody(uint typeTag, uint handle, float fadeSeconds)
		{
			var body = new byte[17];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), FadeSubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), typeTag);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), handle);
			body[12] = 0x00;
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(13, 4), fadeSeconds);
			return body;
		}

		// "bag -> current looter = this player" (also used for NewAllowedLooter, same shape). 21 B:
		// [subop][lootTypetag][bagHandle][0x00][entityTypetag][playerHandle].
		public static byte[] BuildNewCurrentLooterBody(uint bagHandle, uint playerHandle)
		{
			var body = new byte[21];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), NewCurrentLooterSubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), LootTypeTag);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), bagHandle);
			body[12] = 0x00;
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(13, 4), EntityTypeTag);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(17, 4), playerHandle);
			return body;
		}

		// Opens the loot WINDOW on the object (SimpleDynelVisualChange carrying the state string "Open"). 19 + len B:
		// [subop][typetag][handle][00000000][0x00][u16 strLen][ascii]. The bag stays in the world; this just opens its UI.
		public static byte[] BuildOpenWindowBody(uint typeTag, uint handle, string state = "Open")
		{
			var text = Encoding.ASCII.GetBytes(state);
			var body = new byte[19 + text.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), VisualChangeSubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), typeTag);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), handle);
			// body[12..16] stays 0x00000000
			body[16] = 0x00;
			BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(17, 2), (ushort)text.Length);
			text.CopyTo(body, 19);
			return body;
		}
	}
}
