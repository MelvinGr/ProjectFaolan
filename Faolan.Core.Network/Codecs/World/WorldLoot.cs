using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// Loot-bag interaction packets, decoded from captures (see protocol_loot_interaction). A loot bag is its own
	// entity class - EntityClass.LootBag (0xC87A), not the EntityClass.Character (0xC350) that players and NPCs
	// carry - so every body here is addressed by that class word plus the bag's live handle. These build the 0x2000
	// IIR record bodies (the bytes after the u32 length prefix).
	public static class WorldLoot
	{
		public const float DefaultFadeSeconds = 3.0f;

		// The IIR types this codec constructs or parses (#317 convention; read by the coverage matrix).
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.LootRemoveItemsIIR, GameServer0X2000Opcodes.N3ToClientFadeIIR, GameServer0X2000Opcodes.NewAllowedLooterIIR, GameServer0X2000Opcodes.NewCurrentLooterIIR,
			GameServer0X2000Opcodes.SimpleDynelVisualChangeIIR, GameServer0X2000Opcodes.UpdateSimpleDynelIIR
		];

		// UpdateSimpleDynelIIR (0x30536411): the bag's visual update as it's consumed (sent right before the fade).
		// 24 B: [subop][lootTypetag][bagHandle][00000000][00000000][01][00 01 01].
		public static byte[] BuildBagConsumedVisualBody(uint bagHandle)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.UpdateSimpleDynelIIR)
				.WriteUInt32(EntityClass.LootBag)
				.WriteUInt32(bagHandle)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteByte(0x01)
				.WriteByte(0x00)
				.WriteByte(0x01)
				.WriteByte(0x01)
				.ToArray();
		}

		// The graceful fade-out despawn (the loot-consumed / loot-expired variant of n3ToClientQuit). 17 B:
		// [subop][typetag][handle][0x00 marker][float fadeSeconds, big-endian]. Marker 0x00 + a FLOAT distinguishes it
		// from n3ToClientQuit's 0x01 marker + u32 playfield token.
		public static byte[] BuildFadeBody(uint typeTag, uint handle, float fadeSeconds)
		{
			return new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.N3ToClientFadeIIR).WriteUInt32(typeTag).WriteUInt32(handle).WriteByte(0x00).WriteFloat(fadeSeconds).ToArray();
		}

		// LootRemoveItemsIIR (0x70010162): the per-take ACK that tells the client which display slot(s) to remove from
		// the open bag window; the client removes them and compacts the window. 25 B for the single-slot case:
		//   [subop][lootTypetag][bagHandle][u32 0][u8 0][ListFrame count][u32 removedPos ...]
		// The removed positions are a COUNT-FRAMED LIST, not a landmark plus a value. This used to be written as
		// `[u32 0x00000007][u8 0xE2]`, straddling the word boundary, and described as "the 0x000007E2 landmark is a
		// fixed constant here" - the exact mistake the project rule names: 0x7E2 is ListFrame.ForCount(1), every
		// capture we have takes ONE slot at a time, and a single-element list is indistinguishable from a constant.
		// Reading it aligned puts the u8 marker where its siblings carry one (BuildNewCurrentLooterBody /
		// BuildLooterResetBody both have a u8 after the handle) and leaves the list word 4-byte aligned. The bytes are
		// unchanged - WorldLootTakeTests pins both captured frames.
		//
		// Decoded from loot-drag-1538 (a drag-only capture): drag srcPos=1 -> element=1, drag srcPos=0 -> element=0,
		// single-item bags (srcPos 0) -> 0, loot-all -> 0. The OLD constant element=0 removed slot 0 on every take
		// (#226-2: "first slot disappears"); a remaining-COUNT value (e.g. 2) named no real slot, so the dragged item
		// was left in the bag.
		//
		// Whether a multi-slot take sends count>1 rather than N single-slot acks is UNVERIFIED - no capture takes two
		// slots in one message. Gitea #453. [V0]
		public static byte[] BuildLootRemoveItemsBody(uint bagHandle, params uint[] removedPositions)
		{
			var positions = removedPositions.Length == 0
				? [0u]
				: removedPositions;

			var s = new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.LootRemoveItemsIIR)
				.WriteUInt32(EntityClass.LootBag)
				.WriteUInt32(bagHandle)
				.WriteUInt32(0)
				.WriteByte(0x00)
				.WriteUInt32(ListFrame.ForCount(positions.Length));

			foreach (var pos in positions)
				s.WriteUInt32(pos);

			return s.ToArray();
		}

		// NewCurrentLooter / NewAllowedLooter RESET (looter -> none) once the bag is emptied. 21 B:
		// [subop][lootTypetag][bagHandle][00000000][00000000][00]. Distinct from BuildNewCurrentLooterBody (which
		// ASSIGNS a looter with [0x00][entityTypetag][playerHandle]); the reset clears it with two zero words.
		public static byte[] BuildLooterResetBody(uint subOpcode, uint bagHandle)
		{
			return new FaolanStream().WriteUInt32(subOpcode).WriteUInt32(EntityClass.LootBag).WriteUInt32(bagHandle).WriteUInt32(0).WriteUInt32(0).WriteByte(0x00).ToArray();
		}

		// "bag -> ALLOWED looter = this player" (NewAllowedLooterIIR), same shape as the current-looter body but the
		// other sub-opcode. Retail sends this immediately AFTER the LootBagFullUpdateIIR at the bag's mid-session spawn
		// (capture fr1358: 1e2e0139 0000c87a <bag> 00 0000c350 <player>) - it is what makes the client INSTANTIATE +
		// render a freshly-introduced loot bag mid-session (#263); the bag's FullUpdate alone only renders on a
		// playfield (re)load.
		public static byte[] BuildNewAllowedLooterBody(uint bagHandle, uint playerHandle)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.NewAllowedLooterIIR)
				.WriteUInt32(EntityClass.LootBag)
				.WriteUInt32(bagHandle)
				.WriteByte(0x00)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.ToArray();
		}

		// "bag -> current looter = this player" (also used for NewAllowedLooter, same shape). 21 B:
		// [subop][lootTypetag][bagHandle][0x00][entityTypetag][playerHandle].
		public static byte[] BuildNewCurrentLooterBody(uint bagHandle, uint playerHandle)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.NewCurrentLooterIIR)
				.WriteUInt32(EntityClass.LootBag)
				.WriteUInt32(bagHandle)
				.WriteByte(0x00)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.ToArray();
		}

		// Opens the loot WINDOW on the object (SimpleDynelVisualChange carrying the state string "Open"). 19 + len B:
		// [subop][typetag][handle][00000000][0x00][u16 strLen][ascii]. The bag stays in the world; this just opens its UI.
		public static byte[] BuildOpenWindowBody(uint typeTag, uint handle, string state = "Open")
		{
			var text = Encoding.Latin1.GetBytes(state);
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.SimpleDynelVisualChangeIIR)
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
