using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// FanaticCommandIIR_t (0xEE4AA85C) - what the client sends when a GM runs one of its commands (#448/#449).
	//
	// ⚠️ These tests used to build their fixture in the SAME shape the parser read, so they passed while both were
	// wrong and every real command arrived with an empty text. A builder that mirrors its parser proves only that the
	// file agrees with itself. The two records below are REAL BYTES off the wire from a live GM session
	// (2026-07-29) - they are the oracle, and the synthetic builder is checked against them.
	public class FanaticCommandCodecTests
	{
		// `/cloak` typed in-client.
		private const string CloakRecord =
			"00000023EE4AA85C0000C3500802E5D400000000020000000000000000000000062F636C6F616B";

		// `/teleport gm island` typed in-client.
		private const string TeleportRecord =
			"00000030EE4AA85C0000C3500802E5D400000000020000000000000000000000132F74656C65706F727420676D2069736C616E64";

		// The same `/cloak`, but with the sender targeting THEMSELVES - the only difference is the target pair.
		private const string CloakSelfTargetedRecord =
			"00000023EE4AA85C0000C3500802E5D400000000020000C3500802E5D4000000062F636C6F616B";

		// The target pair is a dynel reference (entityClass, handle), not two opaque words: untargeted sends 0/0,
		// self-targeted sends the Character class 0xC350 and the sender's own handle.
		[Fact]
		public void TargetPair_IsAnEntityClassAndHandle()
		{
			var none = FanaticCommandCodec.TryParse(Convert.FromHexString(CloakRecord))!.Value;
			var self = FanaticCommandCodec.TryParse(Convert.FromHexString(CloakSelfTargetedRecord))!.Value;

			Assert.Equal("/cloak", none.Text);
			Assert.Equal("/cloak", self.Text);

			Assert.Equal(0u, none.TargetA); // nothing targeted
			Assert.Equal(0u, none.TargetB);

			Assert.Equal(0xC350u, self.TargetA);       // entity class
			Assert.Equal(self.Handle, self.TargetB);   // ...and the sender's own handle: self-target
		}

		[Theory]
		[InlineData(CloakRecord, "/cloak")]
		[InlineData(TeleportRecord, "/teleport gm island")]
		public void Parses_TheRealRecordsOffTheWire(string hex, string expectedText)
		{
			var parsed = FanaticCommandCodec.TryParse(Convert.FromHexString(hex));

			Assert.NotNull(parsed);
			Assert.Equal(expectedText, parsed!.Value.Text);
			Assert.Equal(0x0802E5D4u, parsed.Value.Handle);
			Assert.Equal(0, parsed.Value.Flag);   // the carrier flag byte
			Assert.Equal(2u, parsed.Value.Word0); // constant across commands - NOT a command id
			Assert.Equal(0u, parsed.Value.TargetA); // 0/0 = "no explicit target"; the client substitutes its own pair
			Assert.Equal(0u, parsed.Value.TargetB);
		}

		// The regression: the carrier's flag byte belongs BEFORE the record, and dropping it slides every field by one.
		// The symptom was not a parse failure but plausible garbage - word0 0, targets 33554432/0, empty text - where
		// 33554432 is 0x02000000, i.e. word0's 2 read one byte early.
		[Fact]
		public void MissingTheCarrierFlagByte_ProducesPlausibleGarbage()
		{
			var body = Convert.FromHexString(CloakRecord);
			uint U32(int at) => body.ReadU32Be(at);

			// The old framing started the record at the handle+4, i.e. it skipped the flag byte, so each field was read
			// one byte early. Asserted straight off the captured bytes - no need to keep the broken parser around.
			Assert.Equal(0u, U32(16));          // "word0"  -> actually the flag byte plus word0's top three bytes
			Assert.Equal(0x02000000u, U32(20)); // "targetA" -> 33554432, the number that showed in-client
			Assert.Equal(0u, U32(24));          // "targetB"
			Assert.Equal(0u, U32(28));          // "textLength" -> zero, which is why the command vanished

			// Read one byte later - past the flag - and every field is the real one.
			Assert.Equal(2u, U32(17));  // word0
			Assert.Equal(0u, U32(21));  // targetA
			Assert.Equal(0u, U32(25));  // targetB
			Assert.Equal(6u, U32(29));  // textLength == "/cloak".Length
			Assert.Equal("/cloak", FanaticCommandCodec.TryParse(body)!.Value.Text);
		}

		// The synthetic builder must reproduce the real records byte-for-byte, or it is not modelling the wire.
		[Theory]
		[InlineData(CloakRecord, "/cloak")]
		[InlineData(TeleportRecord, "/teleport gm island")]
		public void Builder_ReproducesTheCapturedBytes(string hex, string text)
		{
			Assert.Equal(hex, Convert.ToHexString(Body(2, 0, 0, text)));
		}

		// An empty command text is legitimate (`/gm` on its own), and must not be confused with a parse failure.
		[Fact]
		public void EmptyText_ParsesAsEmptyNotNull()
		{
			var parsed = FanaticCommandCodec.TryParse(Body(2, 0, 0, ""));

			Assert.NotNull(parsed);
			Assert.Equal("", parsed!.Value.Text);
		}

		// This is an undecoded inbound path: a short or lying body must degrade, never throw into the inbound loop.
		[Fact]
		public void MalformedBodies_Degrade_RatherThanThrow()
		{
			Assert.Null(FanaticCommandCodec.TryParse([1, 2, 3]));
			Assert.Null(FanaticCommandCodec.TryParse(new byte[16])); // carrier only, no record

			// A length field larger than the body: the text is dropped, the rest of the record still reads.
			var lying = Body(9, 0, 0, "abc");
			lying[^4] = 0x7F; // blow up the declared text length
			var parsed = FanaticCommandCodec.TryParse(lying);
			Assert.NotNull(parsed);
			Assert.Equal(9u, parsed!.Value.Word0);
			Assert.Equal("", parsed.Value.Text);
		}

		// Builds a c2s body the way the client does: carrier, flag byte, three u32s, then the u32-length-prefixed text.
		private static byte[] Body(uint word0, uint targetA, uint targetB, string text)
		{
			var raw = Encoding.Latin1.GetBytes(text);
			var bytes = new List<byte>();
			void U32(uint v) => bytes.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

			// Record length counts everything AFTER itself: subop + class + handle + flag + 3 u32s + the u32 length,
			// i.e. 29 bytes, plus the text. (35 for "/cloak", 48 for "/teleport gm island" - both as captured.)
			U32((uint)(29 + raw.Length));
			U32(FanaticCommandCodec.Opcode);
			U32(0xC350); // entity class
			U32(0x0802E5D4); // handle
			bytes.Add(0x00); // carrier flag
			U32(word0);
			U32(targetA);
			U32(targetB);
			U32((uint)raw.Length);
			bytes.AddRange(raw);
			return [.. bytes];
		}
	}
}
