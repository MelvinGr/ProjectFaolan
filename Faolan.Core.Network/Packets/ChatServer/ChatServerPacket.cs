using Faolan.Core.Enums;

namespace Faolan.Core.Network.Packets.ChatServer
{
	public class ChatServerPacket : Packet
	{
		public ChatServerOpcodes Opcode { get; }

		public ChatServerPacket(byte[] bytes) : base(bytes)
		{
			using var stream = new FaolanStream(bytes);
			Opcode = stream.ReadUInt16<ChatServerOpcodes>();
			Length = stream.ReadUInt16(); // body length (the bytes AFTER this 4-byte opcode+length header)

			var available = stream.Length - stream.Position;
			if (Length == 0 || Length > available)
			{
				IsValid = false;
				return;
			}

			Body = new FaolanStream(stream.ReadArray(Length));
			IsValid = true;
		}
	}
}
