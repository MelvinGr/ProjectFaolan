namespace Faolan.Core.Network.Packets
{
	// The parsed result of an inbound frame. The subclass ctor parses the raw bytes through a short-lived (disposed)
	// FaolanStream cursor into these init-only fields; the base holds no parse state of its own - only the results.
	public abstract class Packet
	{
		public readonly byte[] Bytes; // for serializing/testing
		public bool IsValid { get; protected init; }
		public FaolanStream Body { get; protected init; } = null!;

		public uint Length { get; protected init; }

		protected Packet(byte[] bytes)
		{
			Bytes = bytes;
		}
	}
}
