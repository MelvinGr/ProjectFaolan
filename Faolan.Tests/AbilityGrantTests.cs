using Faolan.Core.Database.Tables;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// Locks the 0x2052 ability-grant body to the retail wire format. The expected bytes are the bodies (everything
	// after the 16-bit opcode) of the strickenbla combat capture's ability grants - pkt#399 (Normal Attack Left,
	// 769094) and pkt#401 (Normal Attack Front, 769099) - and the Ability fields are the matching faolan.db rows.
	public class AbilityGrantTests
	{
		[Theory]
		[InlineData(769094u, 108u, 1263352118u, 3236668225u, 2153842419u, 314062142u, 3015939650u,
			"0000006c000bbc46000bbc4600000000000000014b4d3936c0eba341806106f312b8353eb3c39642c0eba341806106f312b8353eb3c39642b290805c29e627cab290805c29e627ca000000010000000001")]
		[InlineData(769099u, 112u, 1227967307u, 3443694845u, 1660108369u, 1296666148u, 2957995211u,
			"00000070000bbc4b000bbc4b000000000000000149314b4bcd429cfd62f33e514d498e24b04f6ccbcd429cfd62f33e514d498e24b04f6ccbb290805c29e627cab290805c29e627ca000000010000000001")]
		public void BuildAbilityGrantBody_MatchesCapture(uint id, uint slot, uint data0, uint data1, uint data2, uint data3, uint data4, string expectedHex)
		{
			var ability = new Ability { Id = id, Slot = slot, Data0 = data0, Data1 = data1, Data2 = data2, Data3 = data3, Data4 = data4 };

			var body = GameServerListener.BuildAbilityGrantBody(ability);

			Assert.Equal(expectedHex, ToHex(body));
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
