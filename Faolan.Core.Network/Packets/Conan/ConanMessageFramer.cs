using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Packets.Conan
{
	public sealed class ConanMessageFramer : IMessageFramer
	{
		private const int CompressionMarkerLength = 9;
		private const int HeaderSize = sizeof(int);
		private const int MaxPacketSize = 16 * 1024 * 1024;

		// The compression-stream marker: a 9-byte control frame that is just an ordinary [u32 length][u32 crc32][body]
		// packet - length 0x80000005 (high bit = control-frame marker, low bits 5 = crc(4)+body(1)), then crc32(body),
		// then a 1-byte flag body (0 = open a zlib stream). The "4 trailing bytes" before the flag are the standard
		// CRC32 field = crc32(body), decoded #318 (tools/capture/decode_compression_frame.py), not a stream param.
		// Retail only ever sends it SERVER->CLIENT to open the download compression stream; our server does not
		// compress, and our client emits a standalone 9-byte marker with NO compressed payload (plaintext packets
		// surround it). We do not decompress inbound, so we SKIP the whole marker and keep parsing. This is deliberate:
		// the old code decoded/decompressed on this tag (and the NetworkClient read loop dropped the whole recv buffer
		// on it), which - when TCP coalesced the marker with the packets behind it - silently ate them, giving the
		// non-deterministic loss of the post-ready 0x205f grant-arm packet (abilities/spellbar intermittently empty).
		private const uint CompressionMarkerTag = 0x80000005u;

		private readonly byte[] _lengthBuffer = new byte[HeaderSize];

		// The frame currently being filled: [HeaderSize big-endian length][payload], allocated once when the length is
		// known so a completed packet is emitted with no extra copy. Null while the 4-byte length header is being read.
		private byte[]? _frame;
		private int _lengthBytesRead;
		private int _markerSkipRemaining;
		private int _payloadBytesRead;

		public IEnumerable<byte[]> PushBytes(byte[] bytes, int length)
		{
			ArgumentNullException.ThrowIfNull(bytes);

			if (length < 0 || length > bytes.Length)
				throw new ArgumentOutOfRangeException(nameof(length));

			var packets = new List<byte[]>();
			var offset = 0;

			while (offset < length)
			{
				var bytesAvailable = length - offset;

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
					var payloadLength = _frame.Length - HeaderSize;
					var bytesTransferred = Math.Min(payloadLength - _payloadBytesRead, bytesAvailable);

					Array.Copy(bytes, offset, _frame, HeaderSize + _payloadBytesRead, bytesTransferred);
					offset += bytesTransferred;
					_payloadBytesRead += bytesTransferred;

					if (_payloadBytesRead == payloadLength)
					{
						packets.Add(_frame);
						_frame = null;
						_payloadBytesRead = 0;
					}

					continue;
				}

				// Accumulate the 4-byte big-endian length header (may straddle recv boundaries).
				var headerTransferred = Math.Min(HeaderSize - _lengthBytesRead, bytesAvailable);
				Array.Copy(bytes, offset, _lengthBuffer, _lengthBytesRead, headerTransferred);
				offset += headerTransferred;
				_lengthBytesRead += headerTransferred;

				if (_lengthBytesRead < HeaderSize)
					continue;

				_lengthBytesRead = 0;
				var raw = BinaryPrimitives.ReadUInt32BigEndian(_lengthBuffer);

				// Compression-stream marker: skip its remaining 5 bytes and keep parsing (see CompressionMarkerTag).
				// Handled BEFORE the length-range guard so it is never mistaken for a hostile length, and so any
				// packets coalesced behind it in the same recv still get framed.
				if (raw == CompressionMarkerTag)
				{
					_markerSkipRemaining = CompressionMarkerLength - HeaderSize;
					continue;
				}

				if (raw > MaxPacketSize)
				{
					_frame = null; // hostile length - drop the rest of this recv
					break;
				}

				if (raw == 0)
					continue;

				// Allocate the whole frame ([length][payload]) once and stamp the header, so the completed packet is
				// emitted directly - no per-packet Concat/copy.
				_frame = new byte[HeaderSize + (int)raw];
				BinaryPrimitives.WriteUInt32BigEndian(_frame, raw);
				_payloadBytesRead = 0;
			}

			return packets;
		}
	}
}
