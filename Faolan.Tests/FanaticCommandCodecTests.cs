using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// FanaticCommandIIR_t (0xEE4AA85C) - what the client sends when a GM runs one of its commands (#448/#449). The
	// grammar comes from the client's own reader AND writer, which agree; these pin the framing, and in particular the
	// one that was wrong: the command text's length prefix is a u32, not the u16 our wire strings normally use.
	public class FanaticCommandCodecTests
	{
		// A body as the inbound path sees it: the IIR carrier header then the record.
		private static byte[] Body(uint word0, uint targetA, uint targetB, string text, int lengthPrefixBytes = 4)
		{
			var bytes = new List<byte>();
			void U32(uint v) => bytes.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

			U32(0); // record length
			U32(FanaticCommandCodec.Opcode);
			U32(0x4E); // entity class
			U32(0x0131_6D2B); // handle
			U32(word0);
			U32(targetA);
			U32(targetB);

			var raw = Encoding.Latin1.GetBytes(text);
			if (lengthPrefixBytes == 4)
				U32((uint)raw.Length);
			else
				bytes.AddRange([(byte)(raw.Length >> 8), (byte)raw.Length]);

			bytes.AddRange(raw);
			return [.. bytes];
		}

		[Fact]
		public void Parses_TheRecordAndItsU32LengthPrefixedText()
		{
			var parsed = FanaticCommandCodec.TryParse(Body(7, 0x1000, 0x2000, "/whereis NecroL80"));

			Assert.NotNull(parsed);
			Assert.Equal(0x0131_6D2Bu, parsed!.Value.Handle);
			Assert.Equal(7u, parsed.Value.Word0);
			Assert.Equal(0x1000u, parsed.Value.TargetA);
			Assert.Equal(0x2000u, parsed.Value.TargetB);
			Assert.Equal("/whereis NecroL80", parsed.Value.Text);
		}

		// The bug this codec shipped with: reading the length as a u16 takes the HIGH half of the u32, which is 0 for
		// every real command - so the text parsed as empty and the server reported a GM command with no command in it.
		[Fact]
		public void AU16LengthPrefix_WouldHaveEatenTheText()
		{
			var correct = FanaticCommandCodec.TryParse(Body(0, 0, 0, "/cloak on"));
			Assert.Equal("/cloak on", correct!.Value.Text);

			// The same record framed the wrong way round is not silently accepted as a shorter string either.
			var wrong = FanaticCommandCodec.TryParse(Body(0, 0, 0, "/cloak on", lengthPrefixBytes: 2));
			Assert.NotEqual("/cloak on", wrong!.Value.Text);
		}

		// An empty command text is legitimate (`/gm` on its own), and must not be confused with a parse failure.
		[Fact]
		public void EmptyText_ParsesAsEmptyNotNull()
		{
			var parsed = FanaticCommandCodec.TryParse(Body(0, 0, 0, ""));

			Assert.NotNull(parsed);
			Assert.Equal("", parsed!.Value.Text);
		}

		// This is an undecoded inbound path: a short or lying body must degrade, never throw into the inbound loop.
		[Fact]
		public void MalformedBodies_Degrade_RatherThanThrow()
		{
			Assert.Null(FanaticCommandCodec.TryParse([1, 2, 3]));
			Assert.Null(FanaticCommandCodec.TryParse(new byte[16])); // header only, no record

			// A length field larger than the body: the text is dropped, the rest of the record still reads.
			var lying = Body(9, 1, 2, "abc");
			lying[^4] = 0x7F; // blow up the declared text length
			var parsed = FanaticCommandCodec.TryParse(lying);
			Assert.NotNull(parsed);
			Assert.Equal(9u, parsed!.Value.Word0);
			Assert.Equal("", parsed.Value.Text);
		}
	}
}
