using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The PlayerAgent/PlayerInterface method constants live once, as the `PlayerAgentMethods` / `PlayerInterfaceMethods`
	// enums, and `PlayerAgentMethodIds` is the map from those to the opcodes retail was OBSERVED using (for naming: the
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

		// Every observed opcode is the tag byte plus its method's first varint group. This is what makes the opcode
		// enums on this pair a VIEW rather than a parallel catalog: each member's value is computable from the constant,
		// so a member that drifts fails here.
		[Fact]
		public void EveryObservedOpcode_IsItsMethodsFirstGroup()
		{
			Assert.NotEmpty(PlayerAgentMethodIds.MethodInstanceByOpcode);

			foreach (var (opcode, method) in PlayerAgentMethodIds.MethodInstanceByOpcode)
				Assert.Equal(opcode, (ushort)(0x2080 | (method & 0x7F)));
		}

		// The two opcodes that each name TWO methods (#608). They are in the map once - it is keyed by opcode - which is
		// exactly why an opcode cannot be the dispatch key on this pair.
		[Theory, InlineData(0x20EC, PlayerAgentMethods.ReceivedCharacterSettings, PlayerAgentMethods.AuthenticateLoggedIn),
		 InlineData(0x20D6, PlayerAgentMethods.VerifyLanguageSetting, PlayerAgentMethods.UpdateCharacterPreview)]
		public void TheCollidingPairs_ShareAnOpcode(int opcode, PlayerAgentMethods first, PlayerAgentMethods second)
		{
			Assert.Equal((ushort)opcode, (ushort)(0x2080 | ((uint)first & 0x7F)));
			Assert.Equal((ushort)opcode, (ushort)(0x2080 | ((uint)second & 0x7F)));
			Assert.NotEqual(first, second);
		}

		// The four login handoffs are named like any other method now. They used to share one invented opcode `0x1A07`,
		// which is why they were absent from this map entirely (the bytes RpcHeaderTests locks are the same four).
		[Theory, InlineData(PlayerInterfaceOpcodes.ChatServerConnectReady, PlayerInterfaceMethods.ChatServerConnectReady),
		 InlineData(PlayerInterfaceOpcodes.CSServerConnectReady, PlayerInterfaceMethods.CSServerConnectReady),
		 InlineData(PlayerInterfaceOpcodes.LoginConnectReady, PlayerInterfaceMethods.LoginConnectReady),
		 InlineData(PlayerInterfaceOpcodes.OverrideGameTweaks, PlayerInterfaceMethods.OverrideGameTweaks)]
		public void TheLoginHandoffs_AreOrdinaryMethods(PlayerInterfaceOpcodes opcode, PlayerInterfaceMethods method)
		{
			Assert.Equal((uint)method, PlayerAgentMethodIds.MethodInstanceByOpcode[(ushort)opcode]);
		}
	}
}
