using System;
using System.Buffers.Binary;

namespace Faolan.GameServer
{
	// The new-character playfield-access-window packet body (a 0x737A6DF9 IIR, DispatchPassBlob): the static
	// world-access catalog the client shows on first world entry - 172 records across 49 types (hubs, cities, solo
	// dungeons, group dungeons, raids). It is the same for every new character, so it lives as an embedded template
	// (GameServer/NewCharacterAccessWindow.bin, lifted out of an 8 KB inline byte-array in GameServerListener.WorldEntry).
	// Body layout: [u32 payloadLen][u32 iir 0x737A6DF9][u32 class 0x0000C350][u32 clientInstance][catalog payload].
	// NOT yet decoded field-by-field - only the client instance (offset 12) is stamped per character; the instance
	// varints inside the catalog payload are still the captured character's (harmless, but a decode follow-up).
	internal static class NewCharacterAccessWindow
	{
		private static readonly byte[] Template = Program.LoadEmbeddedResource("Faolan.NewCharacterAccessWindow.bin");

		// Offset of the client-instance u32 in the body ([payloadLen][iir][class] = 12 bytes precede it).
		private const int InstanceOffset = 12;

		// The packet body with the character's client instance stamped in. Returns a fresh copy - the template is shared.
		public static byte[] Build(uint clientInstance)
		{
			var body = (byte[])Template.Clone();
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(InstanceOffset, 4), clientInstance);
			return body;
		}
	}
}
