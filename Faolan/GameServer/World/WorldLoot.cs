using Faolan.Core.Network;
using System.Text;
using Faolan.Core.Data;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// Loot-bag interaction packets, decoded from captures (see protocol_loot_interaction). Loot bags use their own
	// typetag 0xC87A (not the 0xC350 the entity family uses). These build the 0x2000 IIR record bodies (the bytes after
	// the u32 length prefix).
	public static class WorldLoot
	{
		public const float DefaultFadeSeconds = 3.0f;
		public const uint EntityTypeTag = EntityClass.Character; // the entity-frame typetag carried inside NewCurrentLooter
		public const uint FadeSubOpcode = (uint)GameServer0X2000RespondsOpcodes.N3ToClientFadeIIR; // n3ToClientFadeIIR_t - graceful fade-out despawn
		public const uint LootTypeTag = 0x0000C87A; // the loot-bag class typetag
		public const uint NewCurrentLooterSubOpcode = (uint)GameServer0X2000RespondsOpcodes.NewCurrentLooterIIR; // NewCurrentLooterIIR_c
		public const uint VisualChangeSubOpcode = (uint)GameServer0X2000RespondsOpcodes.SimpleDynelVisualChangeIIR; // SimpleDynelVisualChangeIIR_c - "Open" opens the loot window

		// The graceful fade-out despawn (the loot-consumed / loot-expired variant of n3ToClientQuit). 17 B:
		// [subop][typetag][handle][0x00 marker][float fadeSeconds, big-endian]. Marker 0x00 + a FLOAT distinguishes it
		// from n3ToClientQuit's 0x01 marker + u32 playfield token.
		public static byte[] BuildFadeBody(uint typeTag, uint handle, float fadeSeconds)
		{
			return new FaolanStream()
				.WriteUInt32(FadeSubOpcode)
				.WriteUInt32(typeTag)
				.WriteUInt32(handle)
				.WriteByte(0x00)
				.WriteFloatBigEndian(fadeSeconds)
				.ToArray();
		}

		// "bag -> current looter = this player" (also used for NewAllowedLooter, same shape). 21 B:
		// [subop][lootTypetag][bagHandle][0x00][entityTypetag][playerHandle].
		public static byte[] BuildNewCurrentLooterBody(uint bagHandle, uint playerHandle)
		{
			return new FaolanStream()
				.WriteUInt32(NewCurrentLooterSubOpcode)
				.WriteUInt32(LootTypeTag)
				.WriteUInt32(bagHandle)
				.WriteByte(0x00)
				.WriteUInt32(EntityTypeTag)
				.WriteUInt32(playerHandle)
				.ToArray();
		}

		// Opens the loot WINDOW on the object (SimpleDynelVisualChange carrying the state string "Open"). 19 + len B:
		// [subop][typetag][handle][00000000][0x00][u16 strLen][ascii]. The bag stays in the world; this just opens its UI.
		public static byte[] BuildOpenWindowBody(uint typeTag, uint handle, string state = "Open")
		{
			var text = Encoding.ASCII.GetBytes(state);
			return new FaolanStream()
				.WriteUInt32(VisualChangeSubOpcode)
				.WriteUInt32(typeTag)
				.WriteUInt32(handle)
				.WriteUInt32(0) // [12..16] = 0
				.WriteByte(0x00) // [16]
				.WriteUInt16((ushort)text.Length)
				.WriteArray(text)
				.ToArray();
		}
	}
}
