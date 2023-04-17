using System;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Faolan.PacketAnalyzer.Tests
{
	public class PacketAdvertisedEndpointParserTests
	{
		[Fact]
		public void TryExtractEndpointPort_ExtractsPort_FromBinaryPayload()
		{
			var payload = new byte[sizeof(uint) + sizeof(ushort)];
			payload[0] = 37;
			payload[1] = 18;
			payload[2] = 192;
			payload[3] = 43;
			BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(sizeof(uint), sizeof(ushort)), 7012);

			var result = PacketAdvertisedEndpointParser.TryExtractEndpointPort(payload, out var port);

			Assert.True(result);
			Assert.Equal((ushort)7012, port);
		}

		[Fact]
		public void TryExtractEndpointPort_ExtractsPort_FromLengthPrefixedStringPayload()
		{
			var endpoint = "37.18.192.43:7018";
			var endpointBytes = Encoding.ASCII.GetBytes(endpoint);
			var payload = new byte[sizeof(ushort) + endpointBytes.Length];
			BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, sizeof(ushort)), (ushort)endpointBytes.Length);
			endpointBytes.CopyTo(payload, sizeof(ushort));

			var result = PacketAdvertisedEndpointParser.TryExtractEndpointPort(payload, out var port);

			Assert.True(result);
			Assert.Equal((ushort)7018, port);
		}

		[Fact]
		public void TryExtractEndpointPort_ReturnsFalse_WhenPayloadHasNoEndpoint()
		{
			var payload = Array.Empty<byte>();

			var result = PacketAdvertisedEndpointParser.TryExtractEndpointPort(payload, out var port);

			Assert.False(result);
			Assert.Equal((ushort)0, port);
		}

		[Fact]
		public void TryFindHostPort_ExtractsAdvertisedPort_FromAsciiPayload()
		{
			var bytes = Encoding.ASCII.GetBytes("realm=test.ageofconan.com:7011;other=ignored");

			var result = PacketAdvertisedEndpointParser.TryFindHostPort(bytes, out var port);

			Assert.True(result);
			Assert.Equal((ushort)7011, port);
		}
	}
}
