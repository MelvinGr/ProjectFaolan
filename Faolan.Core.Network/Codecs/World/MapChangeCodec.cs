using System;
using System.Buffers.Binary;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// PlayfieldFIIR (MapChange, 0x5D85BFC7) - tells the client which playfield/instance it is entering. The body is
	// built by CreateEditorRecordBuilder.BuildPlayfieldFiir (the create-flow builds the SAME body); this codec owns
	// the IIR subop + carrier. See GameServerListener.BuildMapChange / CreateFlowService (#189/#308).
	public static class MapChangeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR;

		// Field offsets WITHIN the PlayfieldFiir body (the inverse of the CreateEditorRecordBuilder.BuildPlayfieldFiir
		// writes): mapId is the 7th word (after the 8-byte zero prefix, the 0x00 byte, the 3 reference floats, the 0x62
		// byte and the 0xC79C word); PlayfieldFlags is the final word.
		private const int MapIdBodyOffset = 26;
		private const int FlagsBodyOffset = 99;

		// What the importer harvests from a MapChange. Flags is null when the body is too short to carry it (a shorter
		// mid-session PlayfieldF that only re-announces the map).
		public sealed record Parsed(uint MapId, uint? Flags);

		// Prepend the IIR subop to a PlayfieldFiir body (from CreateEditorRecordBuilder.BuildPlayfieldFiir).
		public static byte[] BuildIir(byte[] playfieldFiirBody)
		{
			return new FaolanStream().WriteUInt32(Opcode).WriteArray(playfieldFiirBody).ToArray();
		}

		// Parses the IIR content ([u32 subop][PlayfieldFiir body], i.e. the output of BuildIir): validates the subop
		// and reads the mapId + optional PlayfieldFlags. The single place that knows these offsets, so the importer's
		// three MapChange call sites (CaptureMapImporter / CaptureCharacterImporter / AreaNameExtractor) stop copying
		// them. Returns false on a non-MapChange or too-short body.
		public static bool TryParse(ReadOnlySpan<byte> iirContent, out Parsed parsed)
		{
			parsed = null;
			if (iirContent.Length < 4 + MapIdBodyOffset + 4)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(iirContent[..4]) != Opcode)
				return false;

			var mapId = BinaryPrimitives.ReadUInt32BigEndian(iirContent.Slice(4 + MapIdBodyOffset, 4));
			uint? flags = iirContent.Length >= 4 + FlagsBodyOffset + 4
				? BinaryPrimitives.ReadUInt32BigEndian(iirContent.Slice(4 + FlagsBodyOffset, 4))
				: null;
			parsed = new Parsed(mapId, flags);
			return true;
		}
	}
}
