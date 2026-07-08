using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Locks the 0x2052 ability-grant body to the retail wire format. The expected bytes are the bodies (everything
	// after the 16-bit opcode) of the strickenbla combat capture's ability grants - pkt#399 (Normal Attack Left,
	// 769094) and pkt#401 (Normal Attack Front, 769099) - and the Ability fields are the matching faolan.db rows.
	public class AddSpecialAbilityTests
	{
		[Theory,
		 InlineData(769094u,
			 108u,
			 1263352118u,
			 3236668225u,
			 2153842419u,
			 314062142u,
			 3015939650u,
			 "0000006c000bbc46000bbc4600000000000000014b4d3936c0eba341806106f312b8353eb3c39642c0eba341806106f312b8353eb3c39642b290805c29e627cab290805c29e627ca000000010000000001"),
		 InlineData(769099u,
			 112u,
			 1227967307u,
			 3443694845u,
			 1660108369u,
			 1296666148u,
			 2957995211u,
			 "00000070000bbc4b000bbc4b000000000000000149314b4bcd429cfd62f33e514d498e24b04f6ccbcd429cfd62f33e514d498e24b04f6ccbb290805c29e627cab290805c29e627ca000000010000000001"),
		 // The level-gated Rest unlock (#209/#73): the announced grant retail sends at the L1->L2 ding (Blablabla
		 // levelup capture, frame 9937) - the SAME grant grammar, proving the ding grant needs no new builder,
		 // only the level-gated hook + the Level=2 row.
		 InlineData(269272u,
			 106u,
			 1312109365u,
			 2023574725u,
			 3133773807u,
			 3870783406u,
			 1887832719u,
			 "0000006a00041bd800041bd800000000000000014e353335789d4cc5bac997efe6b777ae70860a8f789d4cc5bac997efe6b777ae70860a8fb290805c29e627cab290805c29e627ca000000010000000001")]
		public void BuildAddSpecialAbilityBody_MatchesCapture(uint id, uint slot, uint data0, uint data1, uint data2, uint data3, uint data4, string expectedHex)
		{
			var ability = new Ability
			{
				Id = id,
				Slot = slot,
				Data0 = data0,
				Data1 = data1,
				Data2 = data2,
				Data3 = data3,
				Data4 = data4
			};

			var body = AbilityGrantBody.BuildAddSpecialAbilityBody(ability);

			Assert.Equal(expectedHex, ToHex(body));
		}

		// The per-login SILENT re-grant clears the announce byte (the final 0x01 -> 0x00), so the client doesn't pop
		// "You have learned a new ability" for the whole starting kit on every login. The captures show this is the
		// ONLY byte that differs between a create-flow grant (announce) and a login re-grant for the same ability.
		[Fact]
		public void BuildAddSpecialAbilityBody_Silent_ClearsTheAnnounceByte()
		{
			var ability = new Ability
			{
				Id = 769094u,
				Slot = 108u,
				Data0 = 1263352118u,
				Data1 = 3236668225u,
				Data2 = 2153842419u,
				Data3 = 314062142u,
				Data4 = 3015939650u
			};

			var announced = ToHex(AbilityGrantBody.BuildAddSpecialAbilityBody(ability));
			var silent = ToHex(AbilityGrantBody.BuildAddSpecialAbilityBody(ability, false));

			Assert.EndsWith("0000000001", announced);
			Assert.EndsWith("0000000000", silent);
			// Only the final byte differs.
			Assert.Equal(announced[..^2], silent[..^2]);
		}

		[Fact]
		public void TryParseAddSpecialAbilityBody_RejectsMalformed()
		{
			var ability = new Ability
			{
				Id = 769094u,
				Slot = 108u,
				Data0 = 1u,
				Data1 = 2u,
				Data2 = 3u,
				Data3 = 4u,
				Data4 = 5u
			};
			var body = AbilityGrantBody.BuildAddSpecialAbilityBody(ability);

			Assert.False(AbilityGrantBody.TryParseAddSpecialAbilityBody(new byte[80], out _, out _)); // wrong length
			var corruptId = (byte[])body.Clone();
			corruptId[8] ^= 0xFF; // break the duplicated Id (field index 2)
			Assert.False(AbilityGrantBody.TryParseAddSpecialAbilityBody(corruptId, out _, out _));
			var corruptConst = (byte[])body.Clone();
			corruptConst[14 * 4] ^= 0xFF; // break the B290805C constant
			Assert.False(AbilityGrantBody.TryParseAddSpecialAbilityBody(corruptConst, out _, out _));
		}

		// Build -> Parse must recover the exact Ability fields and announce flag (the body duplicates Id and Data1..4,
		// so the parser has structural invariants to check). Same capture rows as the build test.
		[Theory, InlineData(769094u, 108u, 1263352118u, 3236668225u, 2153842419u, 314062142u, 3015939650u, true), InlineData(769099u, 112u, 1227967307u, 3443694845u, 1660108369u, 1296666148u, 2957995211u, false)]
		public void TryParseAddSpecialAbilityBody_RoundTripsBuild(uint id, uint slot, uint data0, uint data1, uint data2, uint data3, uint data4, bool announce)
		{
			var original = new Ability
			{
				Id = id,
				Slot = slot,
				Data0 = data0,
				Data1 = data1,
				Data2 = data2,
				Data3 = data3,
				Data4 = data4
			};
			var body = AbilityGrantBody.BuildAddSpecialAbilityBody(original, announce);

			Assert.True(AbilityGrantBody.TryParseAddSpecialAbilityBody(body, out var parsed, out var parsedAnnounce));
			Assert.Equal(announce, parsedAnnounce);
			Assert.Equal(original.Id, parsed.Id);
			Assert.Equal(original.Slot, parsed.Slot);
			Assert.Equal(original.Data0, parsed.Data0);
			Assert.Equal(original.Data1, parsed.Data1);
			Assert.Equal(original.Data2, parsed.Data2);
			Assert.Equal(original.Data3, parsed.Data3);
			Assert.Equal(original.Data4, parsed.Data4);
		}

		private static string ToHex(byte[] bytes)
		{
			var chars = new char[bytes.Length * 2];
			for (var i = 0; i < bytes.Length; i++)
			{
				const string hex = "0123456789abcdef";
				chars[i * 2] = hex[bytes[i] >> 4];
				chars[i * 2 + 1] = hex[bytes[i] & 0xF];
			}

			return new string(chars);
		}
	}
}
