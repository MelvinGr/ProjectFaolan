using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// LookAtIIR (0x0D155695) - TARGET SELECTION. The client sends it when the player picks a target and the server
	// echoes it; NPCs and pets send it too, which is how a pet's chosen target is visible on the wire.
	//
	// A zero target is not a missing field - it is the player CLEARING their selection, and it is 26 of the 112
	// LookAtIIR events in the necromancer Tortage capture. Anything that treats 0 as "unknown" loses the deselect.
	//
	// ⚠️ It does NOT share DamageResultIIR's layout even though both name two entities: the target endpoint sits at
	// 13/17 here against 17/21 there. Reading one at the other's offsets yields a plausible-looking handle
	// (0x500003b2 - half a class word and half a handle), which is exactly the kind of confident nonsense a fixed
	// offset copied between message types produces.
	//
	// [V2: xxx_mage-necromancer_login_play2_tortage_city_first-quests.tshark.json - 112 events, 29-byte bodies,
	// c2s selections matching the conversations and fights that follow them]
	public static class LookAtCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.LookAtIIR;

		// Offsets into the IIR CONTENT (u32 length prefix stripped).
		private const int TargetOffset = 17;

		private const int BodyLength = 29;

		// `target` 0 = clear the selection.
		public static byte[] Build(uint subject, uint target, uint trailer = 2)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.LookAtIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(subject)
				.WriteByte(0x00)
				.WriteUInt32(target == 0 ? 0 : EntityClass.Character)
				.WriteUInt32(target)
				.WriteUInt32(0)
				.WriteUInt32(trailer)
				.ToArray();
		}

		public static bool TryParse(byte[] body, out uint subject, out uint target)
		{
			subject = target = 0;
			if (body is not { Length: BodyLength })
				return false;
			if (body.ReadU32Be(0) != Opcode)
				return false;

			subject = body.ReadU32Be(8);
			target = body.ReadU32Be(TargetOffset);
			return true;
		}
	}
}
