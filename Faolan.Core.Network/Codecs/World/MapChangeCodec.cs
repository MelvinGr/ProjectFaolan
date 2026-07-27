using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.World
{
	// PlayfieldFIIR (MapChange, 0x5D85BFC7) - tells the client which playfield/instance it is entering. The body is
	// built by CreateEditorRecordBuilder.BuildPlayfieldFiir (the create-flow builds the SAME body); this codec owns
	// the IIR subop + carrier. See GameServerListener.BuildMapChange / CreateFlowService (#189/#308).
	public static class MapChangeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR;

		// Field offsets WITHIN the PlayfieldFiir body (the inverse of the CreateEditorRecordBuilder.BuildPlayfieldFiir
		// writes). mapId and FieldA are the PlayfieldProxy's u32[1] and u32[2] (the proxy starts at the 0x62 'b'
		// version byte; see docs/subsystems/teleport-changemap.md); FieldC and the flags are the last two of the four
		// trailing words PlayfieldFIIR's own reader takes (FUN_008f36e0 -> obj+0x88 and obj+0x8c).
		private const int MapIdBodyOffset = 26;
		private const int FieldABodyOffset = 30;
		private const int FieldCBodyOffset = 95;
		private const int FlagsBodyOffset = 99;

		// Prepend the IIR subop to a PlayfieldFiir body (from CreateEditorRecordBuilder.BuildPlayfieldFiir).
		public static byte[] BuildIir(byte[] playfieldFiirBody)
		{
			return new FaolanStream().WriteUInt32(Opcode).WriteArray(playfieldFiirBody).ToArray();
		}

		// Parses the IIR content ([u32 subop][PlayfieldFiir body], i.e. the output of BuildIir): validates the subop
		// and reads the mapId plus the three per-map rule words. The single place that knows these offsets, so the
		// importer's three MapChange call sites (CaptureMapImporter / CaptureCharacterImporter / AreaNameExtractor)
		// stop copying them. Returns false on a non-MapChange or too-short body.
		public static bool TryParse(ReadOnlySpan<byte> iirContent, [NotNullWhen(true)] out Parsed? parsed)
		{
			parsed = null;
			if (iirContent.Length < 4 + MapIdBodyOffset + 4)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(iirContent[..4]) != Opcode)
				return false;

			parsed = new Parsed(Word(iirContent, MapIdBodyOffset)!.Value, Word(iirContent, FlagsBodyOffset),
				Word(iirContent, FieldABodyOffset), Word(iirContent, FieldCBodyOffset));
			return true;
		}

		// A body word, or null when the body is too short to carry it - a shorter mid-session PlayfieldF that only
		// re-announces the map has no trailer, and reporting that as a zero would import it as a real value.
		private static uint? Word(ReadOnlySpan<byte> iirContent, int bodyOffset)
		{
			return iirContent.Length >= 4 + bodyOffset + 4
				? BinaryPrimitives.ReadUInt32BigEndian(iirContent.Slice(4 + bodyOffset, 4))
				: null;
		}

		// What the importer harvests from a MapChange. Every field but the map id is null when the body is too short
		// to carry it. FieldA/FieldC are per-MAP words that were hardcoded until #451.
		public sealed record Parsed(uint MapId, uint? Flags, uint? FieldA = null, uint? FieldC = null);
	}
}
