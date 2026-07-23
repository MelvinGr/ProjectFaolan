using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Packets.ChatServer;
using Xunit;

namespace Faolan.Tests
{
	// The chat framer cuts [u16 opcode][u16 bodyLength][body] frames out of a TCP stream, where a recv boundary falls
	// ANYWHERE - mid-body, mid-header, or between frames. It buffers only the trailing partial frame, so these pin the
	// cases that buffering has to survive.
	public class ChatServerMessageFramerTests
	{
		[Fact]
		public void ManyFramesInOneRecv_AreAllFramed()
		{
			var frames = Enumerable.Range(0, 50).Select(i => ChatServerMessage.Build(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteString($"message {i}"))).ToList();

			var framed = new ChatServerMessageFramer().PushBytes(Concat(frames));

			Assert.Equal(frames.Count, framed.Count);
			Assert.All(frames.Zip(framed), pair => Assert.Equal(pair.First, pair.Second));
		}

		// A frame split at every possible offset: the framer must emit it exactly once, and only once both halves have
		// arrived - covering a split inside the 4-byte header as well as inside the body.
		[Theory]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		[InlineData(5)]
		[InlineData(9)]
		public void FrameSplitAcrossRecvs_IsEmittedOnceComplete(int splitAt)
		{
			var frame = ChatServerMessage.Build(ChatServerInterfaceOpcodes.ChatSenderInfo, s => s.WriteUInt32(0x01313438).WriteString("Conan"));

			var framer = new ChatServerMessageFramer();
			Assert.Empty(framer.PushBytes(frame.AsSpan(0, splitAt)));

			var completed = framer.PushBytes(frame.AsSpan(splitAt));
			Assert.Equal(frame, Assert.Single(completed));
		}

		// The realistic shape: a recv ends mid-frame with whole frames in front of it, and the next recv carries the
		// remainder plus more frames. Nothing may be lost, duplicated or reordered across the boundary.
		[Fact]
		public void PartialTail_IsCarriedIntoTheNextRecv()
		{
			var first = ChatServerMessage.Build(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteString("first"));
			var split = ChatServerMessage.Build(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteString("split across the boundary"));
			var last = ChatServerMessage.Build(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteString("last"));

			var framer = new ChatServerMessageFramer();
			var head = framer.PushBytes(Concat([first, split[..6]]));
			var tail = framer.PushBytes(Concat([split[6..], last]));

			Assert.Equal(first, Assert.Single(head));
			Assert.Equal([split, last], tail);
		}

		// An empty body frames as the bare 4-byte header (the unnamed 0x0005 chat-connect beat has this shape), and must
		// not stall the frames behind it.
		[Fact]
		public void EmptyBodiedFrame_DoesNotStallTheStream()
		{
			var beat = ChatServerMessage.Build(0x0005);
			var next = ChatServerMessage.Build(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteString("after the beat"));

			Assert.Equal([beat, next], new ChatServerMessageFramer().PushBytes(Concat([beat, next])));
		}

		private static byte[] Concat(IReadOnlyCollection<byte[]> frames)
		{
			return frames.SelectMany(f => f).ToArray();
		}
	}
}
