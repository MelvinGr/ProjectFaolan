using System;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Packets;

namespace Faolan.Core.Network.Conan
{
	public class ConanPacket : Packet
	{
		public readonly byte[] HeaderData; // Remaining data
		public readonly ProtoMessage ReceiverBytes;
		public readonly ProtoMessage SenderBytes;
		public readonly uint Crc32;
		public readonly ushort Opcode;

		public ConanPacket(byte[] bytes) : base(bytes)
		{
			Length = Stream.ReadUInt32();
			Crc32 = Stream.ReadUInt32();

			IsValid = Crc32 == Crypt.Crc32.Calculate(Stream);
			if (!IsValid)
				; //Console.WriteLine($"Invalid {GetType().Name}");

			using var header = new FaolanStream(Stream.ReadArrayUInt32Length());
			SenderBytes = ProtoMessage.Parse(header, 1);
			ReceiverBytes = ProtoMessage.Parse(header, 1);
			Opcode = header.ReadUInt16();
			HeaderData = header.ReadRemainingArray();

			Body = new FaolanStream(Stream.ReadRemainingArray());
		}

		public override string ToString()
		{
			return $"Opcode: {Opcode.ToHex()} Length: {Length}{(Body != null ? Environment.NewLine + Body.ToArray().ToHex() : "")}";
		}
	}
}
