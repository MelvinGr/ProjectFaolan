using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// MagicBuffIIR (0x6B87E316) - SPELL APPLICATION on an entity. `docs/tooling/capture-events.md` calls it
	// "ApplySpell (cast broadcast)", and it is the only message that names the spell involved, which makes it the
	// one that turns a combat capture from handles-and-numbers into "Flesh to Worms (Rank 1)".
	//
	// Two body shapes share one prefix, and that is what pins the layout: the SHORT form (30 B) is
	// [subop][class][caster][u32 spellId][u8][u8][class][target], and the LONG form (84 B) opens identically and
	// then ECHOES the same spell id further in among its duration/stack words. A spell id read at offset 17 is
	// therefore confirmed twice over in the long form, and all 155 MagicBuffIIR bodies in the necromancer Tortage
	// capture resolve against the Spells catalog on that reading.
	//
	// ⚠️ The message does NOT say which of cast / refresh / login-replay it is. The whole standing buff set is
	// re-sent at world entry through this same message, so anything that calls it "casts" is inventing a
	// distinction the bytes do not carry - hence `Parse` returns the spell and the endpoints, and nothing about
	// intent. The flag byte at 21 is the obvious candidate for that distinction and is NOT decoded.
	//
	// [V2: xxx_mage-necromancer_login_play2_tortage_city_first-quests.tshark.json - 155 bodies, 76 short / 79 long,
	// every spell id resolving to a Spells row]
	public static class MagicBuffCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.MagicBuffIIR;

		// Where the spell id sits in BOTH shapes, after [u32 subop][u32 class][u32 caster][u8]. Offsets are into the
		// IIR CONTENT (the u32 length prefix already stripped, as every codec here takes it).
		private const int SpellIdOffset = 13;

		private const int TargetOffset = 22;

		private const int ShortBodyLength = 26;

		// The short (26 B content, 30 B on the wire) apply body. The long form carries duration/stack state this does not model; building is
		// only needed for the short one, and a round-trip test over it is what keeps the offset honest.
		public static byte[] Build(uint caster, uint spellId, uint target, byte flag = 0x01, byte pad = 0x00)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.MagicBuffIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(caster)
				.WriteByte(pad)
				.WriteUInt32(spellId)
				.WriteByte(flag)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(target)
				.ToArray();
		}

		// Recovers (caster, spellId, target) from either shape. `target` is 0 when the body is too short to carry
		// one - the long form's trailing region is not modelled, so only the short form reports a target.
		public static bool TryParse(byte[] body, out uint caster, out uint spellId, out uint target)
		{
			caster = spellId = target = 0;
			if (body is not { Length: >= SpellIdOffset + 4 })
				return false;
			if (body.ReadU32Be(0) != Opcode)
				return false;

			caster = body.ReadU32Be(8);
			spellId = body.ReadU32Be(SpellIdOffset);
			if (body.Length >= ShortBodyLength)
				target = body.ReadU32Be(TargetOffset);
			return true;
		}
	}
}
