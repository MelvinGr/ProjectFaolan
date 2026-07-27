using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The ability/spell grant bodies extracted from the GameServerListener.GrantAbilities partial: the
	// DispatchAddSpecialAbility (0x2052) ability grant + DispatchLearnSpell (0x200A) spell grant. Pure body
	// construction/parse over an Ability row (no live client); the listener still owns the packet framing + the live
	// send. Byte-exact against the create/combat captures (AddSpecialAbilityTests / LearnSpellTests).
	public static class AbilityGrantBody
	{
		public const int AddSpecialAbilityBodyLength = 81; // 20 u32 fields + the announce byte

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
				.WriteUInt32(ability.Key.Key1)
				.WriteUInt32(ability.Key.Key2)
				.WriteUInt32(ability.Key.Key3)
				.WriteUInt32(ability.Key.Key4)
				.WriteUInt32(ability.Key.Key1)
				.WriteUInt32(ability.Key.Key2)
				.WriteUInt32(ability.Key.Key3)
				.WriteUInt32(ability.Key.Key4)
				// Two unset identity slots - see NullIdentity. They follow the repeated Key1..Key4 above, which is
				// what shows they are a PAIR of identities rather than one 16-byte constant.
				.WriteUInt32(NullIdentity.High)
				.WriteUInt32(NullIdentity.Low)
				.WriteUInt32(NullIdentity.High)
				.WriteUInt32(NullIdentity.Low)
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
			if (f[14] != NullIdentity.High || f[15] != NullIdentity.Low || f[16] != NullIdentity.High || f[17] != NullIdentity.Low)
				return false;
			// f[18]/f[19] are a VARIABLE per-grant trailer (kind + value), NOT constants: the server's own grants carry
			// (1, 0) so Build writes that, but captured retail grants also carry (1, 0x0B) and (2, 0x37F412)/(2, 0x37F298)
			// - the f[19] value looks like a linked ability id. Requiring (1, 0) wrongly rejected 3 of 27 valid 81-byte
			// grants when the importer switched to this parser; the repeated B290805C/29E627CA pair + duplicated Id/Data
			// above are the real discriminator, so the trailer is not validated. (Modelling it as a field is #393-adjacent
			// follow-up; the importer harvests only Slot/Id/Data.)

			var announceByte = body[80];
			if (announceByte > 1)
				return false;

			ability = new Ability
			{
				Slot = slot,
				Id = id,
				Data0 = data0,
				Key = new ProtoDecryptionKey
				{
					Key1 = data1,
					Key2 = data2,
					Key3 = data3,
					Key4 = data4
				}
			};
			announce = announceByte == 1;
			return true;
		}
	}
}
