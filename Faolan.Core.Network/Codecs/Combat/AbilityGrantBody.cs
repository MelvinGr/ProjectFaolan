using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The ability/spell grant bodies extracted from the GameServerListener.GrantAbilities partial: the
	// DispatchAddSpecialAbility (0x2052) ability grant + DispatchLearnSpell (0x200A) spell grant. Pure body
	// construction/parse over an Ability row (no live client); the listener still owns the packet framing + the live
	// send. Byte-exact against the create/combat captures (AddSpecialAbilityTests / LearnSpellTests).
	public static class AbilityGrantBody
	{
		public const int AddSpecialAbilityBodyLength = 81; // 20 u32 fields + the announce byte

		// The 0x2052 ability-grant body carries this same 16-byte pair twice, identical for every ability and across
		// every grant burst in the strickenbla combat capture - a fixed category/owner word, not ability-specific.
		private const uint AddSpecialAbilityConstantA = 0xB290805C;
		private const uint AddSpecialAbilityConstantB = 0x29E627CA;

		// The 0x2052 body (everything after the opcode), byte-for-byte as retail. The final byte is the announce flag:
		// 0x01 = "you learned a new ability" toast (first learn), 0x00 = silent re-grant (login) - the only byte that
		// differs between the create-flow and per-login grants for the same ability across the captures.
		public static byte[] BuildAddSpecialAbilityBody(Ability ability, bool announce = true)
		{
			return new FaolanStream()
				.WriteUInt32(ability.Slot)
				.WriteUInt32(ability.Id)
				.WriteUInt32(ability.Id)
				.WriteUInt32(0x00000000)
				.WriteUInt32(0x00000001)
				.WriteUInt32(ability.Data0)
				.WriteUInt32(ability.Data1)
				.WriteUInt32(ability.Data2)
				.WriteUInt32(ability.Data3)
				.WriteUInt32(ability.Data4)
				.WriteUInt32(ability.Data1)
				.WriteUInt32(ability.Data2)
				.WriteUInt32(ability.Data3)
				.WriteUInt32(ability.Data4)
				.WriteUInt32(AddSpecialAbilityConstantA)
				.WriteUInt32(AddSpecialAbilityConstantB)
				.WriteUInt32(AddSpecialAbilityConstantA)
				.WriteUInt32(AddSpecialAbilityConstantB)
				.WriteUInt32(0x00000001)
				.WriteUInt32(0x00000000)
				.WriteByte(Convert.ToByte(announce))
				.ToArray();
		}

		// The DispatchLearnSpell 0x200A body: just the big-endian u32 spell id (`i_nSpellID`). Byte-exact in LearnSpellTests.
		public static byte[] BuildLearnSpellBody(uint spellId)
		{
			return new FaolanStream().WriteUInt32(spellId).ToArray();
		}

		// The inverse of BuildAddSpecialAbilityBody: recovers the Ability (Slot/Id/Data0..4) and the announce flag from a
		// 0x2052 body. Validates the fixed words (the duplicated Id, the 0/1 constants, the B290805C/29E627CA pair, the
		// duplicated Data1..4) so a mis-shaped body is rejected rather than yielding garbage. Round-trip in AddSpecialAbilityTests.
		public static bool TryParseAddSpecialAbilityBody(ReadOnlySpan<byte> body, out Ability ability, out bool announce)
		{
			ability = new Ability();
			announce = false;
			if (body.Length != AddSpecialAbilityBodyLength)
				return false;

			Span<uint> f = stackalloc uint[20];
			for (var i = 0; i < 20; i++)
				f[i] = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(i * 4, 4));

			var slot = f[0];
			var id = f[1];
			var data0 = f[5];
			var data1 = f[6];
			var data2 = f[7];
			var data3 = f[8];
			var data4 = f[9];

			// Structural invariants: the duplicated Id, the 0/1 constants, the repeated Data1..4 and the constant pair.
			if (f[2] != id || f[3] != 0x00000000 || f[4] != 0x00000001)
				return false;
			if (f[10] != data1 || f[11] != data2 || f[12] != data3 || f[13] != data4)
				return false;
			if (f[14] != AddSpecialAbilityConstantA || f[15] != AddSpecialAbilityConstantB || f[16] != AddSpecialAbilityConstantA || f[17] != AddSpecialAbilityConstantB)
				return false;
			if (f[18] != 0x00000001 || f[19] != 0x00000000)
				return false;

			var announceByte = body[80];
			if (announceByte > 1)
				return false;

			ability = new Ability
			{
				Slot = slot,
				Id = id,
				Data0 = data0,
				Data1 = data1,
				Data2 = data2,
				Data3 = data3,
				Data4 = data4
			};
			announce = announceByte == 1;
			return true;
		}
	}
}
