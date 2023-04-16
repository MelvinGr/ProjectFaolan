using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.AgentServer
{
	public class AgentServerPacket : Packet
	{
		public AgentServerOpcodes Opcode { get; }

		public override bool IsValid { get; }

		public AgentServerPacket(byte[] bytes)
			: base(bytes)
        {
            Opcode = Stream.ReadUInt16<AgentServerOpcodes>();
			Length = Stream.ReadUInt16();

			if (Length == 0 || Stream.Length < Length)
			{
				IsValid = false;
				return;
			}

			Data = new ConanStream(Stream.ReadArray(Length - sizeof(ushort) * 2));
		}
	}
}
