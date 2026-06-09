using Faolan.Core.Data;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.Core.Network.AgentServer
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
