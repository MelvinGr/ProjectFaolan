using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Server.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// The GameServer multiplexes several RPC classes over one connection, so the same 0x20xx opcode names different
	// methods per class - GameServerMethodNames.Resolve disambiguates by the receiver class id for diagnostic
	// logging (the listeners' default branch). Locks the class->catalog routing.
	public class GameServerMethodNamesTests
	{
		// Same opcode 0x2002, three different classes -> three different recovered names (the (ushort) casts keep the
		// enum-named value in the parameter's underlying type; opcodes live on different enums, so there's no single
		// enum parameter type).
		[Theory, InlineData(Endpoints.GameAgentType, (ushort)GameAgentOpcodes.ChatVicinity, "ChatVicinity"),
		 InlineData(Endpoints.GameCharAgentType, (ushort)GameCharAgentOpcodes.RequestBindLocation, "RequestBindLocation"),
		 InlineData(Endpoints.GameCharAgentProjectType, (ushort)GameCharAgentProjectOpcodes.AcceptApprenticeTeacher, "AcceptApprenticeTeacher"),
		 InlineData(Endpoints.GameCharAgentType, (ushort)GameCharAgentOpcodes.RequestAggroStatus, "RequestAggroStatus"),
		 InlineData(Endpoints.GameAgentType, (ushort)GameAgentOpcodes.Authenticate2, "Authenticate2")]
		public void Resolve_NamesTheMethodPerClass(uint receiverClass, ushort opcode, string expected)
		{
			Assert.Equal(expected, GameServerMethodNames.Resolve(receiverClass, opcode));
		}

		// 0x2FFF is not in any catalog (opcode out of range); RequestAggroStatus is a real opcode but 0xDEADBEEF is
		// not a class served here (class not multiplexed).
		[Theory, InlineData(Endpoints.GameCharAgentType, (ushort)0x2FFF), InlineData(0xDEADBEEF, (ushort)GameCharAgentOpcodes.RequestAggroStatus)]
		public void Resolve_UnknownPairs_ReturnUnknown(uint receiverClass, ushort opcode)
		{
			Assert.Equal("Unknown", GameServerMethodNames.Resolve(receiverClass, opcode));
		}
	}
}
