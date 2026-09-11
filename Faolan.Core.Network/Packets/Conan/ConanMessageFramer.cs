using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Packets.Conan
{
	public sealed class ConanMessageFramer : IMessageFramer
	{
		private const int CompressionMarkerLength = 9;

		// The u32 big-endian length prefix every frame opens with - not the Conan header, which is 12 bytes
		// ([u32 length][u32 crc32][u32 headerLen]) and is none of this class's business. The prefix counts everything
		// after itself, so a whole frame is this plus that count.
		private const int LengthPrefixSize = sizeof(uint);
		private const int MaxPacketSize = 16 * 1024 * 1024;

		// The compression-stream marker: a 9-byte control frame that is an ordinary [u32 length][u32 crc32][body]
		// packet - length 0x80000005 (high bit marks a control frame, low bits 5 = crc(4) + body(1)), then crc32(body),
		// then a 1-byte flag body (0 opens a zlib stream). The four bytes before the flag are the standard CRC32 field,
		// not a stream parameter (tools/capture/decode_compression_frame.py).
		//
		// Retail sends it server-to-client only, to open the download compression stream. This server does not
		// compress, and the client emits a standalone marker with no compressed payload, surrounded by plaintext
		// packets. Inbound is never decompressed, so the whole marker is skipped and parsing continues. Dropping the
		// rest of the recv buffer on this tag instead would silently eat any packets TCP coalesced behind it.
		private const uint CompressionMarkerTag = 0x80000005u;

		private readonly byte[] _lengthBuffer = new byte[LengthPrefixSize];

		// The frame currently being filled: [u32 length prefix][payload], allocated once when the length is known so a
		// completed packet is emitted with no extra copy. Null while the length prefix itself is being read.
		private byte[]? _frame;
		private int _lengthBytesRead;
		private int _markerSkipRemaining;
		private int _payloadBytesRead;

		public IReadOnlyList<byte[]> PushBytes(ReadOnlySpan<byte> bytes)
		{
			// Allocated only if this recv actually completes a frame - a big packet spans many recvs that complete none.
			List<byte[]>? packets = null;
			var offset = 0;

			while (offset < bytes.Length)
			{
				var bytesAvailable = bytes.Length - offset;

				// Consume any outstanding compression-marker bytes first (the 9-byte marker can straddle recv
				// boundaries, so the remaining count is carried across pushes).
				if (_markerSkipRemaining > 0)
				{
					var skip = Math.Min(_markerSkipRemaining, bytesAvailable);
					offset += skip;
					_markerSkipRemaining -= skip;
					continue;
				}

				if (_frame != null)
				{
					// Read the payload straight into the frame, after its 4-byte length header.
					var payloadLength = _frame.Length - LengthPrefixSize;
					var bytesTransferred = Math.Min(payloadLength - _payloadBytesRead, bytesAvailable);

					bytes.Slice(offset, bytesTransferred).CopyTo(_frame.AsSpan(LengthPrefixSize + _payloadBytesRead));
					offset += bytesTransferred;
					_payloadBytesRead += bytesTransferred;

					if (_payloadBytesRead == payloadLength)
					{
						(packets ??= []).Add(_frame);
						_frame = null;
						_payloadBytesRead = 0;
					}

					continue;
				}

				// Accumulate the 4-byte big-endian length prefix (may straddle recv boundaries).
				var prefixTransferred = Math.Min(LengthPrefixSize - _lengthBytesRead, bytesAvailable);
				bytes.Slice(offset, prefixTransferred).CopyTo(_lengthBuffer.AsSpan(_lengthBytesRead));
				offset += prefixTransferred;
				_lengthBytesRead += prefixTransferred;

				if (_lengthBytesRead < LengthPrefixSize)
					continue;

				_lengthBytesRead = 0;
				var raw = BinaryPrimitives.ReadUInt32BigEndian(_lengthBuffer);

				// Compression-stream marker: skip its remaining 5 bytes and keep parsing (see CompressionMarkerTag).
				// Handled BEFORE the length-range guard so it is never mistaken for a hostile length, and so any
				// packets coalesced behind it in the same recv still get framed.
				if (raw == CompressionMarkerTag)
				{
					_markerSkipRemaining = CompressionMarkerLength - LengthPrefixSize;
					continue;
				}

				// A length no legitimate frame can have. The stream is desynced at this point - whatever follows is
				// being read at the wrong offset - so the connection is finished, not just this recv: dropping the rest
				// of the buffer and carrying on would keep framing garbage off a cursor that is already lost. The
				// receive loop turns this into a logged disconnect.
				if (raw > MaxPacketSize)
					throw new InvalidDataException($"Frame length {raw} exceeds the {MaxPacketSize}-byte maximum; the stream is desynced.");

				if (raw == 0)
					continue;

				// Allocate the whole frame ([length][payload]) once and stamp the header, so the completed packet is
				// emitted directly with no per-packet copy.
				_frame = new byte[LengthPrefixSize + (int)raw];
				_frame.WriteU32Be(0, raw);
				_payloadBytesRead = 0;
			}

			return packets ?? [];
		}
	}
}
