using System;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;

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

		public override void Send(INetworkClient client)
		{
			WriteLengthHash();
			base.Send(client);
		}

		public override Task SendAsync(INetworkClient client)
		{
			WriteLengthHash();
			return base.SendAsync(client);
		}

		public PacketStream WriteHeader<T>(ProtoMessage sender, ProtoMessage receiver, T opcode, byte[] headerData = null) where T : struct
		{
			WriteUInt32(0); // Write empty length
			WriteUInt32(0); // write empty crc32

			WriteArrayUInt32Length(s =>
			{
				s.WriteArray(sender.ToArray());
				s.WriteArray(receiver.ToArray());
				s.WriteUInt16(Convert.ToUInt16(opcode));

				if (headerData?.Length > 0)
					s.WriteArray(headerData);
			});

			return this;
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

		// Typed header: build the sender/receiver ProtoEndpoints (type + instance) instead of hand-rolled byte
		// arrays. Serializes each endpoint to its [0x0D <type> 0x10 <instance>] bytes and reuses the byte[] framing,
		// so the output is byte-identical to the literal-array overload (locked by ProtoEndpointGoldenTests).
		public PacketStream WriteHeader<T>(ProtoEndpoint sender, ProtoEndpoint receiver, T opcode, byte[] headerData = null) where T : struct
		{
			return WriteHeader(sender.ToProtobufBytes(), receiver.ToProtobufBytes(), opcode, headerData);
		}

		public PacketStream WriteHeaderProto<T>(byte[] sender, byte[] receiver, T opcode, byte[] headerData = null) where T : struct
		{
			WriteUInt32(0); // Write empty length
			WriteUInt32(0); // write empty crc32

			WriteArrayUInt32Length(s =>
			{
				s.WriteArray(sender);
				s.WriteArray(receiver);
				s.WriteUInt16(Convert.ToUInt16(opcode));

				if (headerData?.Length > 0)
					s.WriteArray(headerData);
			});

			return this;
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
}
