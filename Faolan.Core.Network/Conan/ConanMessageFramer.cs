using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Conan
{
	public sealed class ConanMessageFramer : IMessageFramer
	{
		private const int MaxPacketSize = 16 * 1024 * 1024;

		// The compression-stream marker: a fixed 9-byte control frame = this 4-byte tag (0x80000005) + 5 trailing
		// bytes. Retail only ever sends it SERVER->CLIENT to open the download compression stream; our server does not
		// compress, and our client emits a standalone 9-byte marker with NO compressed payload (plaintext packets
		// surround it). We do not decompress inbound, so we SKIP the whole marker and keep parsing. This is deliberate:
		// the old code decoded/decompressed on this tag (and the NetworkClient read loop dropped the whole recv buffer
		// on it), which - when TCP coalesced the marker with the packets behind it - silently ate them, giving the
		// non-deterministic loss of the post-ready 0x205f grant-arm packet (abilities/spellbar intermittently empty).
		private const uint CompressionMarkerTag = 0x80000005u;
		private const int CompressionMarkerLength = 9;

		private readonly byte[] _packetLengthBuffer = new byte[sizeof(int)];
		private byte[] _packetBuffer;
		private int _packetBytesRead;
		private int _markerSkipRemaining;

		public IEnumerable<byte[]> PushBytes(byte[] bytes, int length)
		{
			if (bytes == null)
				throw new ArgumentNullException(nameof(bytes));

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

				if (_packetBuffer != null)
				{
					var bytesRequested = _packetBuffer.Length - _packetBytesRead;
					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);

					Array.Copy(bytes, offset, _packetBuffer, _packetBytesRead, bytesTransferred);
					offset += bytesTransferred;
					_packetBytesRead += bytesTransferred;

					if (_packetBytesRead == _packetBuffer.Length)
					{
						packets.Add(_packetLengthBuffer.Concat(_packetBuffer).ToArray());
						_packetBuffer = null;
						_packetBytesRead = 0;
					}
				}
				else
				{
					var bytesRequested = _packetLengthBuffer.Length - _packetBytesRead;
					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);

					Array.Copy(bytes, offset, _packetLengthBuffer, _packetBytesRead, bytesTransferred);
					offset += bytesTransferred;
					_packetBytesRead += bytesTransferred;

					if (_packetBytesRead == sizeof(int))
					{
						var raw = BinaryPrimitives.ReadUInt32BigEndian(_packetLengthBuffer);

						// Compression-stream marker: skip its remaining 5 bytes and keep parsing (see CompressionMarkerTag).
						// Handled BEFORE the length-range guard so it is never mistaken for a hostile length, and so any
						// packets coalesced behind it in the same recv still get framed.
						if (raw == CompressionMarkerTag)
						{
							_markerSkipRemaining = CompressionMarkerLength - sizeof(int);
							_packetBytesRead = 0;
							continue;
						}

						if (raw > MaxPacketSize)
						{
							ResetPacketState();
							break;
						}

						if (raw == 0)
						{
							_packetBytesRead = 0;
							continue;
						}

						_packetBuffer = new byte[raw];
						_packetBytesRead = 0;
					}
				}
			}

			return packets;
		}

		private void ResetPacketState()
		{
			_packetBuffer = null;
			_packetBytesRead = 0;
		}
	}
}
