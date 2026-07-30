using System;
using System.Numerics;
using Faolan.Core.Extensions;
using Xunit;
using Xunit.Sdk;

namespace Faolan.Tests
{
	// SpanReader is the sequential reader every codec's Parse is being moved onto (Gitea #530, wave 0), so it carries
	// two properties the whole refactor rests on and neither is self-evident:
	//
	//   1. Bounds are the READER's job. A short body throws CodecParseException naming the offset and the width, so a
	//      codec never writes `if (body.Length < 26) return false;` again - and cannot silently succeed on a
	//      truncated tail.
	//   2. EnsureAtEnd() makes a grammar TOTAL. A codec that models three fields of an 84-byte body fails here
	//      instead of looking correct, which is the failure mode the const-offset codecs had.
	//
	// The endianness pairs are tested against hand-written bytes rather than round-tripped through a writer: a
	// round-trip passes even if BOTH sides are byte-swapped, which is exactly the bug worth catching on a protocol
	// that is big-endian except where it isn't.
	public class SpanReaderTests
	{
		// SpanReader is a ref struct, so a local of it cannot be captured by a lambda (CS8175) and
		// Assert.Throws<T>(() => r.Read...) does not compile. This is the ref-parameter equivalent.
		private delegate void ReaderOp(ref SpanReader r);

		private static CodecParseException AssertThrows(ReadOnlySpan<byte> data, string? codec, ReaderOp op)
		{
			var r = new SpanReader(data, codec);
			try
			{
				op(ref r);
			}
			catch (CodecParseException e)
			{
				return e;
			}

			throw new XunitException("expected a CodecParseException, but the read succeeded");
		}

		[Fact]
		public void EnsureAtEnd_AfterAPartialDecode_ThrowsAndNamesTheLeftoverBytes()
		{
			// The whole point of rule 3: reading the fields you know about is NOT a successful parse.
			var ex = AssertThrows([0x00, 0x00, 0x00, 0x01, 0xDE, 0xAD, 0xBE, 0xEF], "MagicBuffCodec", (ref SpanReader r) =>
			{
				Assert.Equal(1u, r.ReadU32Be());
				r.EnsureAtEnd();
			});

			Assert.Equal("MagicBuffCodec", ex.Codec);
			Assert.Equal(4, ex.Offset);
			Assert.Contains("4 unread byte(s)", ex.Message);
			Assert.Contains("DEADBEEF", ex.Message);
		}

		[Fact]
		public void EnsureAtEnd_WhenFullyConsumed_Passes()
		{
			var r = new SpanReader([0x01, 0x02]);
			r.ReadU8();
			r.ReadU8();

			Assert.True(r.AtEnd);
			r.EnsureAtEnd();
		}

		[Fact]
		public void ExpectU32Be_OnAWrongTypeWord_ThrowsWithBothValues()
		{
			var ex = AssertThrows([0x6B, 0x87, 0xE3, 0x16], "MagicBuffCodec", (ref SpanReader r) => r.ExpectU32Be(0x0D155695, "opcode"));

			Assert.Contains("0x0D155695", ex.Message);
			Assert.Contains("0x6B87E316", ex.Message);
			Assert.Equal(0, ex.Offset);
		}

		[Fact]
		public void ExpectU32Be_OnTheRightWord_ConsumesIt()
		{
			var r = new SpanReader([0x6B, 0x87, 0xE3, 0x16]);
			Assert.Equal(0x6B87E316u, r.ExpectU32Be(0x6B87E316));
			Assert.True(r.AtEnd);
		}

		// Both spellings of every width, against bytes chosen so a swap is visible. Conan is big-endian EXCEPT
		// protobuf fixed32 floats and the client's RDB files - so the reader has to offer both and must not confuse
		// them.
		[Fact]
		public void FixedWidthPrimitives_ReadBothEndiannesses()
		{
			var r = new SpanReader([0x12, 0x34, 0x12, 0x34, 0x01, 0x02, 0x03, 0x04, 0x01, 0x02, 0x03, 0x04]);
			Assert.Equal(0x1234, r.ReadU16Be());
			Assert.Equal(0x3412, r.ReadU16Le());
			Assert.Equal(0x01020304u, r.ReadU32Be());
			Assert.Equal(0x04030201u, r.ReadU32Le());

			var w = new SpanReader([0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3F]);
			Assert.Equal(1.0f, w.ReadF32Be());
			Assert.Equal(1.0f, w.ReadF32Le());

			var q = new SpanReader([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
			Assert.Equal(Vector3.Zero, q.ReadVector3Be());
			Assert.Equal(0u, q.ReadU64Be());
			Assert.Equal(1, q.ReadI64Be());
			Assert.True(q.AtEnd);
		}

		[Fact]
		public void Peek_DoesNotAdvance_AndIsWhatAFamilyCodecDiscriminatesOn()
		{
			var r = new SpanReader([0x6B, 0x87, 0xE3, 0x16, 0xFF]);
			Assert.Equal(0x6B87E316u, r.PeekU32Be());
			Assert.Equal(0, r.Position);
			Assert.Equal(5, r.Remaining);

			Assert.Equal(0x6B87E316u, r.ReadU32Be());
			Assert.Equal(4, r.Position);
		}

		[Fact]
		public void PositionAndRemaining_TrackTheReads()
		{
			var r = new SpanReader(new byte[10]);
			Assert.Equal(10, r.Length);
			Assert.Equal(0, r.Position);

			r.Skip(3);
			Assert.Equal(3, r.Position);
			Assert.Equal(7, r.Remaining);

			Assert.Equal(7, r.ReadRemaining().Length);
			Assert.True(r.AtEnd);
		}

		// Length-prefixed blocks read their own count, so a codec never hand-slices one.
		[Fact]
		public void ReadBytes_LengthPrefixedForms_ConsumePrefixAndPayload()
		{
			var r = new SpanReader([0x02, 0xAA, 0xBB, 0x00, 0x01, 0xCC, 0x00, 0x00, 0x00, 0x01, 0xDD]);
			Assert.Equal([0xAA, 0xBB], r.ReadBytesU8Length().ToArray());
			Assert.Equal([0xCC], r.ReadBytesU16Length().ToArray());
			Assert.Equal([0xDD], r.ReadBytesU32Length().ToArray());
			Assert.True(r.AtEnd);
		}

		// Wire strings are raw bytes: Latin1, never ASCII/UTF-8. 0xE9 must survive as 'é' - ASCII would give '?'
		// and UTF-8 would throw or substitute. See FaolanStream.ReadString for why the client is the oracle here.
		[Fact]
		public void ReadString_IsLatin1_SoAHighByteRoundTrips()
		{
			var r = new SpanReader([0x00, 0x03, 0x41, 0xE9, 0x42]);
			Assert.Equal("AéB", r.ReadStringU16Length());
			Assert.True(r.AtEnd);
		}

		// The count-framed list word, read WITH its offset. 0x3F1 is an empty list and 0x7E2 a one-element one -
		// both have shipped as "magic terminators", so the reader divides rather than compares, exactly like the
		// client's own readers.
		[Fact]
		public void ReadListCount_DividesTheFramingWord()
		{
			var r = new SpanReader([0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x07, 0xE2, 0x00, 0x00, 0x0B, 0xD3]);
			Assert.Equal(0, r.ReadListCount());
			Assert.Equal(1, r.ReadListCount());
			Assert.Equal(2, r.ReadListCount());
			Assert.True(r.AtEnd);
		}

		[Fact]
		public void ReadListCount_OnANonFramingWord_ThrowsWithTheOffset()
		{
			// The whole reason ListFrame moved next to the reader: CountOf alone cannot say WHERE.
			var ex = AssertThrows([0xAA, 0xBB, 0xCC, 0xDD, 0x00, 0x00, 0x12, 0x34], "GuildRankings", (ref SpanReader r) =>
			{
				r.ReadU32Be();
				r.ReadListCount();
			});

			Assert.Equal(4, ex.Offset);
			Assert.Contains("0x00001234", ex.Message);
			Assert.Contains("0x3F1", ex.Message);
		}

		[Fact]
		public void ReadVarint_DecodesMultiByteValues()
		{
			var r = new SpanReader([0x00, 0xAC, 0x02]);
			Assert.Equal(0ul, r.ReadVarint());
			Assert.Equal(300ul, r.ReadVarint());
			Assert.True(r.AtEnd);
		}

		// The check that stops being every codec's job.
		[Fact]
		public void ShortBody_ThrowsNamingTheOffsetAndTheWidth()
		{
			var ex = AssertThrows([0x00, 0x00], "LookAtCodec", (ref SpanReader r) => r.ReadU32Be());

			Assert.Equal("LookAtCodec", ex.Codec);
			Assert.Equal(0, ex.Offset);
			Assert.Contains("needed 4", ex.Message);
			Assert.Contains("only 2 remain", ex.Message);
		}

		[Fact]
		public void ShortBody_MidStream_ReportsThePositionItFailedAt()
		{
			var ex = AssertThrows([0x01, 0x02, 0x03], null, (ref SpanReader r) =>
			{
				r.ReadU16Be();
				r.ReadU32Be();
			});

			Assert.Equal(2, ex.Offset);
		}
	}
}
