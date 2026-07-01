using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// Locks the equivalence between the generated ProtoEndpoint and the hand-rolled header byte[] literals the
	// codebase has long used. A serialized ProtoEndpoint{Type,Instance} must be byte-identical to
	// [0x0D <type LE32> 0x10 <instance varint>] so the typed WriteHeader / ConanEndpoints path stays a drop-in
	// replacement for the verbatim retail-replay bytes (the client is proven to accept those exact bytes).
	public class ProtoEndpointGoldenTests
	{
		[Fact]
		public void Endpoint_RoundTripsTypeAndInstance()
		{
			var bytes = ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, 20016753);
			var decoded = ((ReadOnlySpan<byte>)bytes).FromProtobuf<ProtoEndpoint>();

			Assert.Equal(ConanEndpoints.GameInterfaceType, decoded.Type);
			Assert.Equal(20016753u, decoded.Instance);
		}

		[Theory, InlineData(ConanEndpoints.GameAgentType, 0x58u, "0D13CE71B11058"), InlineData(ConanEndpoints.GameInterfaceType, 0x021662C8u, "0D47C1676C10C8C5D910"),
		 InlineData(ConanEndpoints.GameInterfaceType, 0x02165EFFu, "0D47C1676C10FFBDD910"), InlineData(ConanEndpoints.GameAgentType, 0x59u, "0D13CE71B11059"),
		 InlineData(ConanEndpoints.GameCharInterfaceType, 0x021662C8u, "0D91F7878B10C8C5D910"), InlineData(ConanEndpoints.GameCharInterfaceProjectType, 0x021662C8u, "0D4876372810C8C5D910")]
		// world/zone sender (instance 0x58), from LoginWorldSender etc.
		// create-editor receiver: player object addressed to the temp instance 0x021662C8
		// stricken997 character receiver 0x02165EFF (RetailCreatePost205FReceiver)
		// post-205F sender (instance 0x59)
		// 0x201C spawn-handshake endpoint, editor instance 0x021662C8 (LoginWorld201CReceiver)
		// report-server-id receiver, editor instance 0x021662C8 (RetailCreateEditorPostReadyAck)
		public void Endpoint_SerializesToHandRolledHeaderBytes(uint type, uint instance, string expectedHex)
		{
			var bytes = ConanEndpoints.Endpoint(type, instance).ToProtobufBytes();
			Assert.Equal(expectedHex, Convert.ToHexString(bytes));
		}
	}
}
