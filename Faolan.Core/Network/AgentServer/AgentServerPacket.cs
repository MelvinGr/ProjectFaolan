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
			Length = Stream.ReadUInt16();

			if (Length == 0 || Stream.Length < Length)
			{
				IsValid = false;
				return;
			}

			Body = new FaolanStream(Stream.ReadArray(Length - sizeof(ushort) * 2));
			IsValid = true;
		}
	}
}
