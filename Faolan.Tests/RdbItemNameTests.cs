using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// Guards RdbItemImporter.ExtractName against the #202 regression: the in-template string length is a u16 followed
	// by a u16 flags word (nonzero when the string is escaped). Reading the length as a u32 folds the flags into the
	// high bytes, so any name carrying an escaped apostrophe (e.g. "Aspirant's Cap") blew past the 1024 guard and was
	// dropped - which is why 225 real items had blank names while the ~900 with flags==0 worked.
	public class RdbItemNameTests
	{
		[Fact]
		public void AssetMeshName_IsSkipped_RealNameTaken()
		{
			// Internal asset/mesh refs contain underscores and are skipped; the next clean string is the display name.
			var t = Template(StringField("NPC_Atlantis_silk_chest_(A2157)", 0), StringField("Mantle of Kuthchemes", 0x18));
			Assert.Equal("Mantle of Kuthchemes", RdbItemImporter.ExtractName(t));
		}

		[Fact]
		public void EscapedApostrophe_NonzeroFlags_IsResolvedAndUnescaped()
		{
			// The #202 fix: nonzero flags (0x18) made the old u32 length read reject this name; the apostrophe is escaped.
			var t = Template(StringField("Aspirant\\'s Cap", 0x18));
			Assert.Equal("Aspirant's Cap", RdbItemImporter.ExtractName(t));
		}

		[Fact]
		public void OnlyAssetName_YieldsNull()
		{
			// NPC/TEST items carry only an asset name - no display name to extract.
			var t = Template(StringField("NPC_Pirate_sailor_head_noearring_(S0047/S0048)", 0));
			Assert.Null(RdbItemImporter.ExtractName(t));
		}

		[Fact]
		public void PlainName_FlagsZero_IsReturned()
		{
			// The case that always worked: flags == 0, no escapes.
			var t = Template(StringField("Dusklight Tunic", 0));
			Assert.Equal("Dusklight Tunic", RdbItemImporter.ExtractName(t));
		}

		// Wraps text as a template string field: [u32 tag 0x21][u16 length][u16 flags][length ASCII bytes].
		private static byte[] StringField(string text, ushort flags)
		{
			var bytes = Encoding.ASCII.GetBytes(text);
			var buf = new byte[8 + bytes.Length];
			BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0x21);
			BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), (ushort)bytes.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), flags);
			bytes.CopyTo(buf.AsSpan(8));
			return buf;
		}

		// 12-byte RDB-Id header + the given fields concatenated.
		private static byte[] Template(params byte[][] fields)
		{
			var body = new List<byte>(new byte[12]);
			foreach (var f in fields)
				body.AddRange(f);
			return body.ToArray();
		}
	}
}
