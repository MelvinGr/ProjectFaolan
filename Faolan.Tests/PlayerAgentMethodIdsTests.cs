using System;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The PlayerAgent/PlayerInterface method constants live once, as the `PlayerAgentMethods` / `PlayerInterfaceMethods`
	// enums, and `PlayerAgentMethodIds` is the map from those to the opcodes retail was observed using (for naming: the
	// packet trace, the dissector and the generated docs). What goes on the wire is the constant itself - `Rpc` writes
	// it whole as `method_instance` - so there is no encoding step left to test here; that is RpcHeaderTests' job.
	public class PlayerAgentMethodIdsTests
	{
		// The client's own registration tables, complete: 25 c2s + 26 s2c. Read off the registration idiom in
		// AgeOfConan.exe (DumpPlayerAgentCtor.java / StrXrefs.java), where the corpus had only ever shown 17 methods.
		[Fact]
		public void TheRegistrationTables_AreTheClientsOwn()
		{
			Assert.Equal(25, PlayerAgentMethodIds.PlayerAgentRegistration.Count);
			Assert.Equal(26, PlayerAgentMethodIds.PlayerInterfaceRegistration.Count);
			Assert.Equal(0x9CB2CB03u, PlayerAgentMethodIds.PlayerAgentRegistration["Authenticate"]);
			Assert.Equal(0x3C7C926Cu, PlayerAgentMethodIds.PlayerAgentRegistration["ReceivedCharacterSettings"]);
			Assert.Equal(0x5DC18991u, PlayerAgentMethodIds.PlayerInterfaceRegistration["AuthenticateAck"]);
		}

		// The observed methods are the ones the corpus shows, and every one of them reads as `0x2080 | (constant & 0x7F)`
		// in a hex dump - which is how you find one in a capture, and simultaneously why that value cannot be a catalog
		// key. `PlayerAgentOpcodes`/`PlayerInterfaceOpcodes` were deleted for exactly this reason.
		[Fact]
		public void EveryObservedMethod_ReadsAsItsFirstVarintGroup()
		{
			Assert.NotEmpty(PlayerAgentMethodIds.ObservedAgentMethods);
			Assert.NotEmpty(PlayerAgentMethodIds.ObservedInterfaceMethods);

			foreach (var method in PlayerAgentMethodIds.ObservedAgentMethods.Cast<Enum>().Concat(PlayerAgentMethodIds.ObservedInterfaceMethods.Cast<Enum>()))
			{
				var constant = Convert.ToUInt32(method);
				Assert.Equal((ushort)(0x2080 | (constant & 0x7F)), PlayerAgentMethodIds.ObservedOpcode(constant));
			}
		}

		// The two dump readings that each name two methods - the whole reason this pair has no opcode catalog.
		[Theory, InlineData(0x20EC, PlayerAgentMethods.ReceivedCharacterSettings, PlayerAgentMethods.AuthenticateLoggedIn),
		 InlineData(0x20D6, PlayerAgentMethods.VerifyLanguageSetting, PlayerAgentMethods.UpdateCharacterPreview)]
		public void TheCollidingPairs_ReadTheSameInADump(int reading, PlayerAgentMethods first, PlayerAgentMethods second)
		{
			Assert.Equal((ushort)reading, PlayerAgentMethodIds.ObservedOpcode((uint)first));
			Assert.Equal((ushort)reading, PlayerAgentMethodIds.ObservedOpcode((uint)second));
			Assert.NotEqual(first, second);
		}

		// The four login handoffs are ordinary methods, observed like the rest. A shared `0x1A07` is not an opcode at
		// all, but RpcHeader field 3's tag plus its length.
		[Theory, InlineData(PlayerInterfaceMethods.ChatServerConnectReady, 0x20FA), InlineData(PlayerInterfaceMethods.CSServerConnectReady, 0x20E0),
		 InlineData(PlayerInterfaceMethods.LoginConnectReady, 0x20F9), InlineData(PlayerInterfaceMethods.OverrideGameTweaks, 0x20E3)]
		public void TheLoginHandoffs_AreOrdinaryMethods(PlayerInterfaceMethods method, int reading)
		{
			Assert.Contains(method, PlayerAgentMethodIds.ObservedInterfaceMethods);
			Assert.Equal((ushort)reading, PlayerAgentMethodIds.ObservedOpcode((uint)method));
		}
	}
}
