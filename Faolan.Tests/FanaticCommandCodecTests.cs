using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// FanaticCommandIIR_t (0xEE4AA85C) - what the client sends when a GM runs one of its commands (#448/#449).
	//
	// ⚠️ These tests previously built their fixture in the SAME shape the parser read, so they passed while both were
	// wrong: the codec was parsing inbound with the s2c layout (an extra target pair before the length), and every real
	// command arrived with an empty text. A builder that mirrors the parser proves only that the file agrees with
	// itself. The layout below comes from the client's WriteSubClass (FUN_00663ae0) - the send path, hence the inbound
	// one - whose base write (FUN_00663450) emits exactly one u32 before the length-prefixed text.
	public class FanaticCommandCodecTests
	{
		// A c2s body as the inbound path sees it: the IIR carrier header, then [u32 word0][u32 len][text].
		private static byte[] Body(uint word0, string text, int lengthPrefixBytes = 4)
		{
			var bytes = new List<byte>();
			void U32(uint v) => bytes.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

			U32(0); // record length
			U32(FanaticCommandCodec.Opcode);
			U32(0x4E); // entity class
			U32(0x0131_6D2B); // handle
			U32(word0);

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
			var parsed = FanaticCommandCodec.TryParse(Body(7, "/whereis NecroL80"));

			Assert.NotNull(parsed);
			Assert.Equal(0x0131_6D2Bu, parsed!.Value.Handle);
			Assert.Equal(7u, parsed.Value.Word0);
			Assert.Equal("/whereis NecroL80", parsed.Value.Text);
		}

		// The regression that made every GM command look empty: two extra u32s were consumed before the length, so the
		// length was read out of the middle of the command string. `/cloak` and `/teleport` both reported
		// `word0 0, targets 33554432/0, text ''` in-client - 33554432 == 0x02000000, the misread length word.
		[Fact]
		public void TheS2cLayout_WouldEatTheText()
		{
			Assert.Equal("/cloak on", FanaticCommandCodec.TryParse(Body(0, "/cloak on"))!.Value.Text);

			// The same record with a target pair spliced in - the s2c shape - must NOT yield the command text.
			var s2c = new List<byte>(Body(0, "/cloak on"));
			s2c.InsertRange(20, new byte[8]); // two u32 targets after word0
			Assert.NotEqual("/cloak on", FanaticCommandCodec.TryParse([.. s2c])!.Value.Text);
		}

		// The other framing bug this codec shipped with: the text's length prefix is a u32, not the u16 our wire
		// strings normally use. Reading it as a u16 takes the HIGH half, which is 0 for every real command.
		[Fact]
		public void AU16LengthPrefix_WouldHaveEatenTheText()
		{
			var wrong = FanaticCommandCodec.TryParse(Body(0, "/cloak on", lengthPrefixBytes: 2));
			Assert.NotEqual("/cloak on", wrong!.Value.Text);
		}

		// An empty command text is legitimate (`/gm` on its own), and must not be confused with a parse failure.
		[Fact]
		public void EmptyText_ParsesAsEmptyNotNull()
		{
			var parsed = FanaticCommandCodec.TryParse(Body(0, ""));

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
			var lying = Body(9, "abc");
			lying[^4] = 0x7F; // blow up the declared text length
			var parsed = FanaticCommandCodec.TryParse(lying);
			Assert.NotNull(parsed);
			Assert.Equal(9u, parsed!.Value.Word0);
			Assert.Equal("", parsed.Value.Text);
		}
	}
}
