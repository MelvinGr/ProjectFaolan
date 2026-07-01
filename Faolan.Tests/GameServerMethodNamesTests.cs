using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets.Opcodes;
using Xunit;

namespace Faolan.Tests
{
	// The GameServer multiplexes several RPC classes over one connection, so the same 0x20xx opcode names different
	// methods per class - GameServerMethodNames.Resolve disambiguates by the receiver class id for diagnostic
	// logging (the listeners' default branch). Locks the class->catalog routing.
	public class GameServerMethodNamesTests
	{
		[Theory, InlineData(ConanEndpoints.GameAgentType, (ushort)0x2002, "ChatVicinity"), InlineData(ConanEndpoints.GameCharAgentType, (ushort)0x2002, "RequestBindLocation"),
		 InlineData(ConanEndpoints.GameCharAgentProjectType, (ushort)0x2002, "AcceptApprenticeTeacher"), InlineData(ConanEndpoints.GameCharAgentType, (ushort)0x2000, "RequestAggroStatus"),
		 InlineData(ConanEndpoints.GameAgentType, (ushort)0x2003, "Authenticate2")]
		// Same opcode 0x2002, three different classes -> three different recovered names.
		// GameAgent
		// GameCharAgent
		// GameCharAgentProject
		public void Resolve_NamesTheMethodPerClass(uint receiverClass, ushort opcode, string expected)
		{
			Assert.Equal(expected, GameServerMethodNames.Resolve(receiverClass, opcode));
		}

		[Theory, InlineData(ConanEndpoints.GameCharAgentType, (ushort)0x2FFF), InlineData(0xDEADBEEF, (ushort)0x2000)]
		// opcode out of the catalog
		// class not multiplexed here
		public void Resolve_UnknownPairs_ReturnUnknown(uint receiverClass, ushort opcode)
		{
			Assert.Equal("Unknown", GameServerMethodNames.Resolve(receiverClass, opcode));
		}
	}
}
