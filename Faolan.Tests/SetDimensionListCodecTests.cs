using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.PlayerAgent;
using Xunit;

namespace Faolan.Tests
{
	// The realm-select list. Every gate here is about WHICH SLOT a value lands in, because that is the only thing
	// about this message that can be got wrong silently.
	//
	// ⭐ #931 is why. The client renders the load level from Dimension_t+0x30, and we sent a `== Full` BOOL there
	// with the real 0..4 enum in the next slot, which nothing reads. Nothing caught it: the byte COUNT is identical
	// either way, so no length check fires; every realm we serve is Recommended, so both spellings are 0 and no
	// capture of our own server differs; and there is no Funcom-era capture of this message to compare against. A
	// layout that only a disassembly can check needs a test that states the layout.
	public class SetDimensionListCodecTests
	{
		private static readonly DimensionEntry Sample = new DimensionEntry(
			7, RealmOnlineStatus.Online, "Fury", RealmLoadStatus.VeryHeavy, false, RealmType.PvPByFaction, RealmLanguage.German);

		[Fact]
		// ⭐ THE #931 GATE. The load level occupies the fourth u32 after the name, and it is the ENUM - a value the
		// client compares against 0, 1, 2, 3 and 4. VeryHeavy (3) is chosen deliberately: it is a value no boolean
		// can express, so this fails on any regression back to a flag.
		public void TheLoadLevel_IsTheEnumAndSitsAtOffset0x30()
		{
			Assert.Equal((uint)RealmLoadStatus.VeryHeavy, Field(Sample, 0x30));
		}

		[Fact]
		// ...and the flag derived from it stays in the NEXT slot, which has no consumer. Asserted so the pair cannot
		// quietly swap back: a test naming only 0x30 would still pass if both slots held the enum.
		public void TheFullFlag_SitsAtOffset0x34_AndIsNotTheLevel()
		{
			Assert.Equal(0u, Field(Sample, 0x34));
			Assert.Equal(1u, Field(Sample with { IsFull = true }, 0x34));
		}

		[Theory]
		// The rest of the record, pinned by offset. These are the fields the client reads (+0x04, +0x24, +0x38) plus
		// the ones it does not, because a slot moving is what breaks the ones that matter.
		//
		// ⚠️ German is deliberately not 0 here. The client counts realms whose +0x40 is NON-ZERO to decide whether to
		// show its language-flag button row, so a value that happened to be 0 would make this gate agree with a codec
		// that dropped the field entirely.
		[InlineData(0x00, 7u)]                                  // Id
		[InlineData(0x04, (uint)RealmOnlineStatus.Online)]      // READ: the renderer's `== 2` gate
		[InlineData(0x24, 1u)]                                  // READ: the language-flag bitfield; 1 = the English bit
		[InlineData(0x28, 0u)]
		[InlineData(0x2c, 0u)]
		[InlineData(0x38, (uint)RealmType.PvPByFaction >> 8)]   // READ: Type's HIGH byte, compared against 7
		[InlineData(0x40, (uint)RealmLanguage.German)]          // READ, but only for being non-zero (the FlagButtons row)
		public void EveryOtherField_SitsAtItsClientOffset(int offset, uint expected)
		{
			Assert.Equal(expected, Field(Sample, offset));
		}

		[Fact]
		// Type spans the u32 at +0x38 and the bool at +0x3c, high byte first - a split that is easy to "tidy" into
		// one u32 and thereby move every field after it.
		public void Type_SpansTheU32AndTheBoolAfterIt()
		{
			var body = Body(Sample);

			Assert.Equal((uint)RealmType.PvPByFaction >> 8, body.ReadU32Be(BodyOffset(0x38, Sample.Name)));
			Assert.Equal((uint)RealmType.PvPByFaction & 0xFF, body[BodyOffset(0x3c, Sample.Name)]);
		}

		[Fact]
		// One entry is a fixed 30 bytes past the name, and the name is the only variable-width field. Pinning the
		// total is what catches a field ADDED or DROPPED, which the per-offset gates above cannot see.
		public void AnEntry_IsExactlyTheClientsRecordLength()
		{
			// u32 count + u32 id + u32 status + u16 length + the name + the fixed tail.
			Assert.Equal(4 + 4 + 4 + 2 + "Fury".Length + TailBytes, SetDimensionListCodec.Build(List(Sample)).Length);
		}

		[Fact]
		public void TheCount_IsAPlainU32_NotAListFrame()
		{
			Assert.Equal(2u, SetDimensionListCodec.Build(List(Sample, Sample with { Id = 8, Name = "Crom" })).ReadU32Be(0));
		}

		[Fact]
		public void ItRoundTrips()
		{
			var realms = List(Sample, Sample with { Id = 8, Name = "Crom", LoadStatus = RealmLoadStatus.Full, IsFull = true, Type = RealmType.PvE });

			// ⚠️ Compared member-wise, not with Assert.Equal on the records: DimensionList holds an ARRAY, and a
			// record compares its members with EqualityComparer<T>.Default, which for an array is REFERENCE equality.
			Assert.Equal(realms.Realms, SetDimensionListCodec.Parse(SetDimensionListCodec.Build(realms)).Realms);
		}

		[Fact]
		public void AShortBody_Throws()
		{
			Assert.ThrowsAny<System.Exception>(() => SetDimensionListCodec.Parse(SetDimensionListCodec.Build(List(Sample))[..^1]));
		}

		// The record's fixed tail, in WIRE bytes: five u32s (+0x24..+0x34), then five bytes carrying +0x38's u32 and
		// +0x3c's bool, then +0x40's u32 and +0x44's bool.
		private const int TailBytes = 5 * 4 + 5 + 4 + 1;

		private static DimensionList List(params DimensionEntry[] realms)
		{
			return new DimensionList(realms);
		}

		private static byte[] Body(DimensionEntry realm)
		{
			return SetDimensionListCodec.Build(List(realm));
		}

		// ⚠️ A client offset does NOT map linearly onto the wire, which is the whole reason this is a TABLE rather
		// than arithmetic. Two things break the correspondence, and both are places a field could be misplaced:
		//   - the name is a std::string spanning +0x08..+0x23 in the struct (0x1C bytes) but a u16 length plus its
		//     characters on the wire;
		//   - the struct pads its two bools to four bytes each, so +0x3c and +0x44 are 4 apart from their
		//     neighbours in the struct and 1 byte wide on the wire.
		// Written out per field so this file states the layout instead of re-deriving it.
		private static readonly Dictionary<int, int> WireOffsets = new Dictionary<int, int>
		{
			[0x00] = 0, [0x04] = 4,                                              // before the name
			[0x24] = 0, [0x28] = 4, [0x2c] = 8, [0x30] = 12, [0x34] = 16,        // after it, from the tail's start
			[0x38] = 20, [0x3c] = 24, [0x40] = 25, [0x44] = 29
		};

		private static int BodyOffset(int dimensionOffset, string name)
		{
			const int Count = 4;
			var relative = WireOffsets[dimensionOffset];

			return dimensionOffset < 0x24
				? Count + relative
				: Count + 4 + 4 + 2 + name.Length + relative;
		}

		// The u32 the client would read at `offset` in its Dimension_t, taken out of the bytes we actually build.
		private static uint Field(DimensionEntry realm, int offset)
		{
			return Body(realm).ReadU32Be(BodyOffset(offset, realm.Name));
		}
	}
}
