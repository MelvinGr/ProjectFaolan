using Faolan.Core.Constants;
using System;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The agents' sender endpoints used to be hand-encoded ProtoEndpoint bytes in each Packets.cs. They are now
	// built through Endpoints.EndpointBytes; these pin that the typed build reproduces the original literals
	// byte-for-byte, so the login/handoff framing is unchanged.
	public class EndpointBytesTests
	{
		[Theory, InlineData(RpcInterfaces.UniverseAgentType, 0x01u, "0da0db4d601001"), InlineData(RpcInterfaces.PlayerAgentType, 0x02u, "0d8404f2821002"),
		 InlineData(RpcInterfaces.PlayerAgentServiceConnectionType, 0x08u, "0d1691351d1008"), InlineData(RpcInterfaces.GameAgentType, 0x59u, "0d13ce71b11059")]
		// (type, instance, the literal the agent used to carry)
		public void EndpointBytes_ReproducesTheHandEncodedSenders(uint type, uint instance, string expected)
		{
			Assert.Equal(expected, Convert.ToHexStringLower(Endpoints.EndpointBytes(type, instance)));
		}
	}
}
