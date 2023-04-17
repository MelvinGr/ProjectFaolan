using Faolan.Core.Data;

namespace Faolan.Core.Network.Packets
{
	public abstract class Packet
	{
		public readonly byte[] Bytes; // for serializing/testing
		public bool IsValid { get; protected init; }
		public FaolanStream Body { get; protected init; }

		public uint Length { get; protected init; }
		protected PacketStream Stream { get; }

		protected Packet(byte[] bytes)
		{
			Bytes = bytes;
			Stream = new PacketStream(bytes);
		}
	}
}
