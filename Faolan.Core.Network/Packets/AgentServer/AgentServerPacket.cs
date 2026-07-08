using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Packets.AgentServer
{
	public class AgentServerPacket : Packet
	{
		public AgentServerOpcodes Opcode { get; }

		public AgentServerPacket(byte[] bytes) : base(bytes)
		{
			Opcode = Stream.ReadUInt16<AgentServerOpcodes>();
			Length = Stream.ReadUInt16(); // body length (the bytes AFTER this 4-byte opcode+length header)

			var available = Stream.Length - Stream.Position;
			if (Length == 0 || Length > available)
			{
				IsValid = false;
				return;
			}

			Body = new FaolanStream(Stream.ReadArray(Length));
			IsValid = true;
		}
	}
}
