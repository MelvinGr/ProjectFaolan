using System.Text;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.GameServer.World
{
	// Loot-bag interaction packets, decoded from captures (see protocol_loot_interaction). Loot bags use their own
	// typetag 0xC87A (not the 0xC350 the entity family uses). These build the 0x2000 IIR record bodies (the bytes after
	// the u32 length prefix).
	public static class WorldLoot
	{
		public const float DefaultFadeSeconds = 3.0f;
		public const uint BagConsumedVisualSubOpcode = (uint)GameServer0X2000Opcodes.UpdateSimpleDynelIIR; // 0x30536411
		public const uint EntityTypeTag = EntityClass.Character; // the entity-frame typetag carried inside NewCurrentLooter
		public const uint FadeSubOpcode = (uint)GameServer0X2000Opcodes.N3ToClientFadeIIR; // n3ToClientFadeIIR_t - graceful fade-out despawn

		public const uint LootRemoveItemsSubOpcode = (uint)GameServer0X2000Opcodes.LootRemoveItemsIIR; // 0x70010162
		public const uint LootTypeTag = 0x0000C87A; // the loot-bag class typetag
		public const uint NewAllowedLooterSubOpcode = (uint)GameServer0X2000Opcodes.NewAllowedLooterIIR; // 0x1E2E0139
		public const uint NewCurrentLooterSubOpcode = (uint)GameServer0X2000Opcodes.NewCurrentLooterIIR; // NewCurrentLooterIIR_c
		public const uint VisualChangeSubOpcode = (uint)GameServer0X2000Opcodes.SimpleDynelVisualChangeIIR; // SimpleDynelVisualChangeIIR_c - "Open" opens the loot window

		// UpdateSimpleDynelIIR (0x30536411): the bag's visual update as it's consumed (sent right before the fade).
		// 24 B: [subop][lootTypetag][bagHandle][00000000][00000000][01][00 01 01].
		public static byte[] BuildBagConsumedVisualBody(uint bagHandle)
		{
			return new FaolanStream().WriteUInt32(BagConsumedVisualSubOpcode).WriteUInt32(LootTypeTag).WriteUInt32(bagHandle).WriteUInt32(0).WriteUInt32(0).WriteByte(0x01).WriteByte(0x00).WriteByte(0x01).WriteByte(0x01).ToArray();
		}

		// The graceful fade-out despawn (the loot-consumed / loot-expired variant of n3ToClientQuit). 17 B:
		// [subop][typetag][handle][0x00 marker][float fadeSeconds, big-endian]. Marker 0x00 + a FLOAT distinguishes it
		// from n3ToClientQuit's 0x01 marker + u32 playfield token.
		public static byte[] BuildFadeBody(uint typeTag, uint handle, float fadeSeconds)
		{
			return new FaolanStream().WriteUInt32(FadeSubOpcode).WriteUInt32(typeTag).WriteUInt32(handle).WriteByte(0x00).WriteFloatBigEndian(fadeSeconds).ToArray();
		}

		// NewCurrentLooter / NewAllowedLooter RESET (looter -> none) once the bag is emptied. 21 B:
		// [subop][lootTypetag][bagHandle][00000000][00000000][00]. Distinct from BuildNewCurrentLooterBody (which
		// ASSIGNS a looter with [0x00][entityTypetag][playerHandle]); the reset clears it with two zero words.
		public static byte[] BuildLooterResetBody(uint subOpcode, uint bagHandle)
		{
			return new FaolanStream().WriteUInt32(subOpcode).WriteUInt32(LootTypeTag).WriteUInt32(bagHandle).WriteUInt32(0).WriteUInt32(0).WriteByte(0x00).ToArray();
		}

		// LootRemoveItemsIIR (0x70010162): the per-take ACK that tells the client which display slot to remove from the
		// open bag window. The trailing u32 is the looted item's display Pos (the srcPos the client sent in TryLootItem
		// 0x2046); the client removes that slot and compacts the window. 25 B:
		// [subop][lootTypetag][bagHandle][00000000][000007e2 landmark][u32 removedPos].
		// Decoded from loot-drag-1538 (a drag-only capture): drag srcPos=1 -> trailing=1, drag srcPos=0 -> trailing=0,
		// single-item bags (srcPos 0) -> 0, loot-all -> 0. The OLD constant trailing=0 removed slot 0 on every take
		// (#226-2: "first slot disappears"); a remaining-COUNT value (e.g. 2) named no real slot, so the dragged item
		// was left in the bag. The 0x000007E2 landmark is a fixed constant here.
		public static byte[] BuildLootRemoveItemsBody(uint bagHandle, uint removedPos = 0)
		{
			return new FaolanStream().WriteUInt32(LootRemoveItemsSubOpcode).WriteUInt32(LootTypeTag).WriteUInt32(bagHandle).WriteUInt32(0).WriteUInt32(7).WriteByte(0xE2).WriteUInt32(removedPos).ToArray();
		}

		// "bag -> ALLOWED looter = this player" (NewAllowedLooterIIR), same shape as the current-looter body but the
		// other sub-opcode. Retail sends this immediately AFTER the LootBagFullUpdateIIR at the bag's mid-session spawn
		// (capture fr1358: 1e2e0139 0000c87a <bag> 00 0000c350 <player>) - it is what makes the client INSTANTIATE +
		// render a freshly-introduced loot bag mid-session (#263); the bag's FullUpdate alone only renders on a
		// playfield (re)load.
		public static byte[] BuildNewAllowedLooterBody(uint bagHandle, uint playerHandle)
		{
			return new FaolanStream().WriteUInt32(NewAllowedLooterSubOpcode).WriteUInt32(LootTypeTag).WriteUInt32(bagHandle).WriteByte(0x00).WriteUInt32(EntityTypeTag).WriteUInt32(playerHandle).ToArray();
		}

		// "bag -> current looter = this player" (also used for NewAllowedLooter, same shape). 21 B:
		// [subop][lootTypetag][bagHandle][0x00][entityTypetag][playerHandle].
		public static byte[] BuildNewCurrentLooterBody(uint bagHandle, uint playerHandle)
		{
			return new FaolanStream().WriteUInt32(NewCurrentLooterSubOpcode).WriteUInt32(LootTypeTag).WriteUInt32(bagHandle).WriteByte(0x00).WriteUInt32(EntityTypeTag).WriteUInt32(playerHandle).ToArray();
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
				.WriteUInt16(text.Length)
				.WriteArray(text)
				.ToArray();
		}
	}
}
