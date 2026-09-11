using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Packets.ChatServer
{
	/// <summary>
	///     Frames the chat wire format - <c>[u16 opcode][u16 bodyLength][body]</c> - out of a TCP byte stream.
	/// </summary>
	/// <remarks>
	///     Only a partial frame is ever buffered: complete frames are cut straight out of the recv window, so a recv
	///     carrying n frames allocates exactly those n frames and nothing else.
	/// </remarks>
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

		// The length field counts the body, so a frame is bodyLength plus the 4-byte header - what the server itself
		// serializes and what the retail captures show.
		private static int FrameLength(ReadOnlySpan<byte> frame)
		{
			return frame.ReadU16Be(sizeof(ushort)) + HeaderSize;
		}
	}
}
