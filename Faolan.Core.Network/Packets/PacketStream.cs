using System;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Packets
{
	public class PacketStream : FaolanStream
	{
		public PacketStream()
		{
		}

		public PacketStream(params byte[] data) : base(data)
		{
		}

		/*public void Send(INetworkClient client)
		{
			WriteLengthHash();
			client.Send(ToArray());
		}*/

		public Task SendAsync(INetworkClient client)
		{
			WriteLengthHash();
			return client.SendAsync(ToArray());
		}

		public PacketStream WriteHeader<T>(byte[] sender, byte[] receiver, T opcode, byte[] headerData = null) where T : struct
		{
			WriteUInt32(0); // Write empty length
			WriteUInt32(0); // write empty crc32

			//Write Headerlength
			WriteUInt32(sizeof(byte) * 2 + sender.Length + sizeof(byte) * 2 + receiver.Length + sizeof(ushort) + (headerData?.Length ?? 0));

			//Write sender
			WriteByte(0x0a);
			WriteArrayByteLength(sender);

			//Write receiver
			WriteByte(0x12);
			WriteArrayByteLength(receiver);

			//write opcode
			WriteUInt16(Convert.ToUInt16(opcode));

			//Write headerData
			if (headerData?.Length > 0)
				WriteArray(headerData);

			return this;
		}

		// PlayerInterface s2c: this interface uniquely carries a 32-bit method id in the header after the opcode.
		// This overload appends it automatically from PlayerAgentMethodIds (the single source), so senders never
		// hand-roll the 4-byte array. Byte-identical to WriteHeader(sender, receiver, opcode,
		// PlayerAgentMethodIds.HeaderId((ushort)opcode)). (SmallCharList / Ox1A07 carry a non-standard header-extra
		// and keep passing it explicitly via the generic overload.)
		public PacketStream WriteHeader(byte[] sender, byte[] receiver, PlayerAgentResponseOpcodes opcode)
		{
			return WriteHeader(sender, receiver, opcode, PlayerAgentMethodIds.HeaderId((ushort)opcode));
		}

		// Typed header: build the sender/receiver ProtoEndpoints (type + instance) instead of hand-rolled byte
		// arrays. Serializes each endpoint to its [0x0D <type> 0x10 <instance>] bytes and reuses the byte[] framing,
		// so the output is byte-identical to the literal-array overload (locked by ProtoEndpointGoldenTests).
		public PacketStream WriteHeader<T>(ProtoEndpoint sender, ProtoEndpoint receiver, T opcode, byte[] headerData = null) where T : struct
		{
			return WriteHeader(sender.ToProtobuf(), receiver.ToProtobuf(), opcode, headerData);
		}

		public PacketStream WriteLengthHash()
		{
			var oldPos = Position;
			Position = 0;
			WriteUInt32(Length - sizeof(uint));
			WriteUInt32(Crc32.Calculate(this));
			Position = oldPos;

			return this;
		}
	}

	public static class FaolanStreamExtensions
	{
		extension(FaolanStream stream)
		{
			/*public void Send(INetworkClient client)
			{
				if (stream is PacketStream s)
					s.Send(client);
				else
					client.Send(stream.ToArray());
			}*/

			public Task SendAsync(INetworkClient client)
			{
				return stream is PacketStream s
					? s.SendAsync(client)
					: client.SendAsync(stream.ToArray());
			}
		}
	}
}
