using System;
using Faolan.Core.Constants;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// A serialized ProtoEndpoint{Type,Instance} must be byte-identical to [0x0D <type LE32> 0x10 <instance varint>],
	// the header encoding the client accepts. That equivalence is what lets the typed WriteHeader / Endpoints path
	// carry the endpoints retail sends; the literals below are those retail bytes.
	public class ProtoEndpointGoldenTests
	{
		[Fact]
		public void Endpoint_RoundTripsTypeAndInstance()
		{
			var bytes = Endpoints.EndpointBytes(RpcInterfaces.GameInterfaceType, 20016753);
			var decoded = bytes.FromProtobuf<ProtoEndpoint>();

			Assert.Equal(RpcInterfaces.GameInterfaceType, decoded.Type);
			Assert.Equal(20016753u, decoded.Instance);
		}

		[Theory, InlineData(RpcInterfaces.GameAgentType, 0x58u, "0D13CE71B11058"), InlineData(RpcInterfaces.GameInterfaceType, 0x021662C8u, "0D47C1676C10C8C5D910"),
		 InlineData(RpcInterfaces.GameInterfaceType, 0x02165EFFu, "0D47C1676C10FFBDD910"), InlineData(RpcInterfaces.GameAgentType, 0x59u, "0D13CE71B11059"),
		 InlineData(RpcInterfaces.GameCharInterfaceType, 0x021662C8u, "0D91F7878B10C8C5D910"), InlineData(RpcInterfaces.GameCharInterfaceProjectType, 0x021662C8u, "0D4876372810C8C5D910")]
		// world/zone sender (instance 0x58), from LoginWorldSender etc.
		// create-editor receiver: player object addressed to the temp instance 0x021662C8
		// stricken997 character receiver 0x02165EFF (RetailCreatePost205FReceiver)
		// post-205F sender (instance 0x59)
		// 0x201C spawn-handshake endpoint, editor instance 0x021662C8 (LoginWorld201CReceiver)
		// report-server-id receiver, editor instance 0x021662C8 (RetailCreateEditorPostReadyAck)
		public void Endpoint_SerializesToHandRolledHeaderBytes(uint type, uint instance, string expectedHex)
		{
			var bytes = Endpoints.Endpoint(type, instance).ToProtobuf();
			Assert.Equal(expectedHex, Convert.ToHexString(bytes));
		}
	}
}
