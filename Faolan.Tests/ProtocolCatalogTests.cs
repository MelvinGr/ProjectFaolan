using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Xunit;

namespace Faolan.Tests
{
	// A 16-bit opcode is a method INDEX, so the same number names a different method on each RPC class - the receiver's
	// Type hash disambiguates. ProtocolCatalog is the one place that (Receiver.Type, opcode) -> name/enum mapping lives;
	// it drives BOTH the inbound (UL) trace (agent classes) and the outbound (DL) fall-back naming (interface classes).
	// (Replaces the retired GameServerMethodNames + its test - the resolver is now global across every tier.)
	public class ProtocolCatalogTests
	{
		// The DL fall-back path (OpcodeEnumFor) still returns the interface enum for a defined opcode, and null otherwise
		// so the caller drops back to its listener-default enum.
		[Fact]
		public void OpcodeEnumFor_ResolvesInterfaceEnum_ElseNull()
		{
			Assert.Equal(typeof(GameInterfaceOpcodes), ProtocolCatalog.OpcodeEnumFor(Endpoints.GameInterfaceType, (ushort)GameInterfaceOpcodes.DispatchGameServerStats));
			Assert.Null(ProtocolCatalog.OpcodeEnumFor(Endpoints.GameInterfaceType, 0x2FFF));
			Assert.Null(ProtocolCatalog.OpcodeEnumFor(0xDEADBEEF, 0x2000));
		}

		// Same opcode, different receiver class -> different recovered name, formatted "Name (0xHEX)". 0x2000 spans four
		// distinct classes (three agents + is also a valid method on others); 0x2002/0x2003 are the multiplex collisions.
		[Theory, InlineData(Endpoints.UniverseAgentType, (ushort)UniverseAgentOpcodes.InitiateAuthentication, "InitiateAuthentication (0x2000)"),
		 InlineData(Endpoints.CSPlayerAgentType, (ushort)CSPlayerAgentOpcodes.Authenticate, "Authenticate (0x2000)"),
		 InlineData(Endpoints.GameAgentType, (ushort)GameAgentOpcodes.Authenticate2, "Authenticate2 (0x2003)"),
		 InlineData(Endpoints.GameAgentType, (ushort)GameAgentOpcodes.ChatVicinity, "ChatVicinity (0x2002)"),
		 InlineData(Endpoints.GameCharAgentType, (ushort)GameCharAgentOpcodes.RequestBindLocation, "RequestBindLocation (0x2002)"),
		 InlineData(Endpoints.GameCharAgentProjectType, (ushort)GameCharAgentProjectOpcodes.AcceptApprenticeTeacher, "AcceptApprenticeTeacher (0x2002)"),
		 InlineData(Endpoints.TradepostAgentType, (ushort)TradepostAgentOpcodes.ItemSearch, "ItemSearch (0x200D)")]
		public void ResolveName_NamesTheMethodPerClass(uint receiverType, ushort opcode, string expected)
		{
			Assert.Equal(expected, ProtocolCatalog.ResolveName(receiverType, opcode));
		}

		// The hash-registered pair has NO opcode (#613), so a frame there is named from its method constant. Three cases,
		// and the third is the one that used to be impossible to get right: an unknown method must render as a
		// constant, never as the 7-bit opcode view that names two methods at once.
		[Fact]
		public void ResolveName_NamesTheHashRegisteredPair_FromItsMethod()
		{
			var agent = Endpoints.Endpoint(Endpoints.PlayerAgentType, 0x02);
			var player = Endpoints.Endpoint(Endpoints.PlayerInterfaceType, 0x01);

			Assert.Equal("AuthenticateAck (0x5DC18991)", ProtocolCatalog.ResolveName(new Rpc(agent, player, PlayerInterfaceMethods.AuthenticateAck, ReadOnlyMemory<byte>.Empty)));

			// A login handoff addresses the ServiceConnection, which no receiver-keyed map covers - only the listener's
			// fall-back method enum can name it. Before #613 this path reached Rpc.Opcode, which now throws.
			var handoff = new Rpc(Endpoints.Endpoint(Endpoints.PlayerAgentServiceConnectionType, 0x08), Endpoints.Endpoint(Endpoints.PlayerAgentServiceConnectionType, 0x01),
				PlayerInterfaceMethods.LoginConnectReady, ReadOnlyMemory<byte>.Empty);
			Assert.Equal("LoginConnectReady (0xD8B66C79)", ProtocolCatalog.ResolveName(in handoff, typeof(PlayerInterfaceMethods)));

			Assert.Equal("Unknown (0xDEADBEEF)", ProtocolCatalog.ResolveName(new Rpc(agent, player, 0xDEADBEEFu, ReadOnlyMemory<byte>.Empty)));
		}

		// An opcode the mapped class doesn't carry, or a receiver type not in the catalog, both render "Unknown (0xHEX)".
		[Theory, InlineData(Endpoints.GameCharAgentType, (ushort)0x2FFF, "Unknown (0x2FFF)"), InlineData(0xDEADBEEF, (ushort)0x2000, "Unknown (0x2000)")]
		public void ResolveName_UnknownPairs_RenderUnknown(uint receiverType, ushort opcode, string expected)
		{
			Assert.Equal(expected, ProtocolCatalog.ResolveName(receiverType, opcode));
		}
	}
}
