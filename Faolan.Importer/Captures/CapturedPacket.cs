using System;
using System.Buffers.Binary;

namespace Faolan.Importer.Extractors
{
	// A captured on-wire packet with the metadata the extractors need, uniform across the .json dumps and the
	// .pcapng analyzer output. Constructed by TsharkCaptureReader; consumed by every extractor and importer.
	//
	// It lived inside ConversationExtractor.cs, which is why `Body` did not: a shared type filed under one of its
	// consumers does not look like somewhere to put shared behaviour. So the framing walk was copy-pasted into six
	// extractors verbatim, three MORE reached across into `WorldObjectExtractor.GetBody` for it, and a seventh copy
	// in CaptureCharacterImporter had already drifted to a stricter guard - the drift the duplication predicts.
	public readonly record struct CapturedPacket(long Ticks, bool IsDownload, uint Opcode, uint SubOpcode, byte[] Bytes)
	{
		// The body within the full on-wire packet: [len4][crc4][headerLen4][header][body]. Empty when the packet is
		// too short to carry the fixed 12-byte prologue, or when its header length runs past the end.
		public byte[] Body
		{
			get
			{
				if (Bytes.Length < 12)
					return [];

				var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(8, 4));
				var bodyStart = 12 + headerLen;
				return bodyStart <= Bytes.Length
					? Bytes[bodyStart..]
					: [];
			}
		}
	}
}
