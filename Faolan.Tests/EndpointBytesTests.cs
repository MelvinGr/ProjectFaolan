using System;
using Faolan.Core.Constants;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// Every agent's sender endpoint is built through Endpoints.EndpointBytes. These pin the typed build against the
	// ProtoEndpoint bytes the login/handoff framing must carry.
	public class EndpointBytesTests
	{
		[Theory, InlineData(RpcInterfaces.UniverseAgentType, 0x01u, "0da0db4d601001"), InlineData(RpcInterfaces.PlayerAgentType, 0x02u, "0d8404f2821002"),
		 InlineData(RpcInterfaces.PlayerAgentServiceConnectionType, 0x08u, "0d1691351d1008"), InlineData(RpcInterfaces.GameAgentType, 0x59u, "0d13ce71b11059")]
		// (type, instance, the encoded endpoint the agent sends)
		public void EndpointBytes_ReproducesTheHandEncodedSenders(uint type, uint instance, string expected)
		{
			Assert.Equal(expected, Convert.ToHexStringLower(Endpoints.EndpointBytes(type, instance)));
		}
	}
}
