using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Xunit;

namespace Faolan.Tests
{
	// A 16-bit opcode is a method INDEX, so the same number names a different method on each RPC class - the receiver's
	// Type hash disambiguates. ProtocolCatalog is the one place that (Receiver.Type, method) -> name/enum mapping lives;
	// it drives BOTH the inbound (UL) trace (agent classes) and the outbound (DL) fall-back naming (interface classes).
	// (Replaces the retired GameServerMethodNames + its test - the resolver is now global across every tier.)
	public class ProtocolCatalogTests
	{
		// The DL fall-back path (MethodEnumFor) still returns the interface enum for a defined method, and null otherwise
		// so the caller drops back to its listener-default enum.
		[Fact]
		public void MethodEnumFor_ResolvesInterfaceEnum_ElseNull()
		{
			Assert.Equal(typeof(GameInterfaceMethods), ProtocolCatalog.MethodEnumFor(Endpoints.GameInterfaceType, (uint)GameInterfaceMethods.DispatchGameServerStats));
			Assert.Null(ProtocolCatalog.MethodEnumFor(Endpoints.GameInterfaceType, 0xFF));
			Assert.Null(ProtocolCatalog.MethodEnumFor(0xDEADBEEF, 0x00));
		}

		// Same method, different receiver class -> different recovered name, formatted "Name (0xHEX)". Method 0 spans
		// four distinct classes (three agents + is a valid method on others); 0x02/0x03 are the multiplex collisions.
		[Theory, InlineData(Endpoints.UniverseAgentType, (uint)UniverseAgentMethods.InitiateAuthentication, "InitiateAuthentication (0x00000000)"),
		 InlineData(Endpoints.CSPlayerAgentType, (uint)CSPlayerAgentMethods.Authenticate, "Authenticate (0x00000000)"),
		 InlineData(Endpoints.GameAgentType, (uint)GameAgentMethods.Authenticate2, "Authenticate2 (0x00000003)"),
		 InlineData(Endpoints.GameAgentType, (uint)GameAgentMethods.ChatVicinity, "ChatVicinity (0x00000002)"),
		 InlineData(Endpoints.GameCharAgentType, (uint)GameCharAgentMethods.RequestBindLocation, "RequestBindLocation (0x00000002)"),
		 InlineData(Endpoints.GameCharAgentProjectType, (uint)GameCharAgentProjectMethods.AcceptApprenticeTeacher, "AcceptApprenticeTeacher (0x00000002)"),
		 InlineData(Endpoints.TradepostAgentType, (uint)TradepostAgentMethods.ItemSearch, "ItemSearch (0x0000000D)")]
		public void ResolveName_NamesTheMethodPerClass(uint receiverType, uint method, string expected)
		{
			Assert.Equal(expected, ProtocolCatalog.ResolveName(receiverType, method));
		}

		// The hash-registered pair has NO opcode (#613), so a frame there is named from its method constant - the same
		// lookup as every other class since #615 folded the two maps into one. Three cases, and the third is the one
		// that used to be impossible to get right: an unknown method must render as a constant, never as the 7-bit
		// opcode view that names two methods at once.
		[Fact]
		public void ResolveName_NamesTheHashRegisteredPair_FromItsMethod()
		{
			var agent = Endpoints.Endpoint(Endpoints.PlayerAgentType, 0x02);
			var player = Endpoints.Endpoint(Endpoints.PlayerInterfaceType, 0x01);

			Assert.Equal("AuthenticateAck (0x5DC18991)", ProtocolCatalog.ResolveName(new Rpc(agent, player, PlayerInterfaceMethods.AuthenticateAck, ReadOnlyMemory<byte>.Empty)));

			// A login handoff addresses the ServiceConnection, which no receiver-keyed map covers - only the listener's
			// fall-back method enum can name it. Before #613 this path reached Rpc.Opcode, which #615 deleted.
			var handoff = new Rpc(Endpoints.Endpoint(Endpoints.PlayerAgentServiceConnectionType, 0x08), Endpoints.Endpoint(Endpoints.PlayerAgentServiceConnectionType, 0x01),
				PlayerInterfaceMethods.LoginConnectReady, ReadOnlyMemory<byte>.Empty);
			Assert.Equal("LoginConnectReady (0xD8B66C79)", ProtocolCatalog.ResolveName(in handoff, typeof(PlayerInterfaceMethods)));

			Assert.Equal("Unknown (0xDEADBEEF)", ProtocolCatalog.ResolveName(new Rpc(agent, player, 0xDEADBEEFu, ReadOnlyMemory<byte>.Empty)));
		}

		// A method the mapped class doesn't carry, or a receiver type not in the catalog, both render "Unknown (0xHEX)".
		[Theory, InlineData(Endpoints.GameCharAgentType, 0xFFFu, "Unknown (0x00000FFF)"), InlineData(0xDEADBEEF, 0x00u, "Unknown (0x00000000)")]
		public void ResolveName_UnknownPairs_RenderUnknown(uint receiverType, uint method, string expected)
		{
			Assert.Equal(expected, ProtocolCatalog.ResolveName(receiverType, method));
		}
	}
}
