using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Packets.ChatServer
{
	// Frames the chat wire format - [u16 opcode][u16 bodyLength][body] - out of a TCP byte stream.
	//
	// Only a PARTIAL frame is ever buffered: complete frames are cut straight out of the recv window, so a recv that
	// carries n frames allocates exactly those n frames and nothing else. (It used to re-concatenate the whole buffer
	// on every push and then copy the remainder after every frame, which is quadratic in the number of frames a recv
	// carries.)
	public sealed class ChatServerMessageFramer : IMessageFramer
	{
		private const int HeaderSize = sizeof(ushort) * 2;

		// The trailing partial frame carried into the next recv. Grows to at most one frame (bodyLength is a u16, so
		// 4 + 0xFFFF bytes) and is reused from then on.
		private byte[] _pending = [];
		private int _pendingLength;

		public IReadOnlyList<byte[]> PushBytes(ReadOnlySpan<byte> bytes)
		{
			List<byte[]>? packets = null;

			// 1. Finish the partial frame carried over from the last recv: top it up until it completes (or this recv
			//    runs out too, in which case it stays pending).
			while (_pendingLength > 0 && !bytes.IsEmpty)
			{
				var needed = _pendingLength < HeaderSize
					? HeaderSize - _pendingLength // the header itself is still short, so the total isn't known yet
					: FrameLength(_pending) - _pendingLength;

				var taken = Math.Min(needed, bytes.Length);
				EnsurePending(_pendingLength + taken);
				bytes[..taken].CopyTo(_pending.AsSpan(_pendingLength));
				_pendingLength += taken;
				bytes = bytes[taken..];

				if (_pendingLength < HeaderSize || _pendingLength < FrameLength(_pending))
					continue;

				(packets ??= []).Add(_pending[.._pendingLength]);
				_pendingLength = 0;
			}

			// 2. Frame the rest straight out of the recv window - nothing is copied until a frame is complete.
			var offset = 0;
			while (bytes.Length - offset >= HeaderSize)
			{
				var frameLength = FrameLength(bytes[offset..]);
				if (bytes.Length - offset < frameLength)
					break;

				(packets ??= []).Add(bytes.Slice(offset, frameLength).ToArray());
				offset += frameLength;
			}

			// 3. Carry the trailing bytes that don't make a frame yet into the next recv.
			if (offset < bytes.Length)
			{
				var tail = bytes[offset..];
				EnsurePending(tail.Length);
				tail.CopyTo(_pending);
				_pendingLength = tail.Length;
			}

			return packets ?? [];
		}

		private void EnsurePending(int length)
		{
			if (_pending.Length < length)
				Array.Resize(ref _pending, length);
		}

		// The length field counts the BODY, so a frame is bodyLength + the 4-byte header (matches what the server itself
		// sends via ChatServerMessage.Build and the retail captures).
		private static int FrameLength(ReadOnlySpan<byte> frame)
		{
			return BinaryPrimitives.ReadUInt16BigEndian(frame[sizeof(ushort)..]) + HeaderSize;
		}
	}
}
