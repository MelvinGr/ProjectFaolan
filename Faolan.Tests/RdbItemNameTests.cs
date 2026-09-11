using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// Guards RdbItemImporter's template-text reader: the string header is `[u32 tag][u16 nameLen][u16 descLen]`, and
	// reading the name length as a u32 folds the second word into its high bytes, so any name followed by a
	// description blows past the 1024 guard and is dropped - the shape that left 225 real items with blank names
	// while the ~900 with a zero second word worked.
	//
	// That second word is the description's length, not a flags word meaning "the string is escaped". Across the 3,587 decrypted templates that carry a clean name it is nonzero on 875, and
	// on all 875 the bytes after the name are printable for exactly that many bytes - no exceptions. The correlation
	// that suggested "escaping" does not hold either: 752 of the 875 have no escape in the name at all.
	//
	// The example this class was built on says it best. `Aspirant's Cap` carries 0x18 = 24, and the 24 bytes after the
	// name are the string `"No description available"`.
	public class RdbItemNameTests
	{
		[Fact]
		public void AssetMeshName_IsSkipped_RealNameTaken()
		{
			// Internal asset/mesh refs contain underscores and are skipped; the next clean string is the display name.
			var t = Template(StringField("NPC_Atlantis_silk_chest_(A2157)", ""), StringField("Mantle of Kuthchemes", "No description available"));
			Assert.Equal("Mantle of Kuthchemes", RdbItemImporter.ExtractName(t));
		}

		// A nonzero second word is what a u32 length read folds in, rejecting this name. It is the real record's own
		// pairing - `Aspirant\'s Cap` really is followed by a 24-byte "No description available" - and the
		// apostrophe is escaped in the template.
		[Fact]
		public void EscapedApostrophe_WithADescription_IsResolvedAndUnescaped()
		{
			var t = Template(StringField("Aspirant\\'s Cap", "No description available"));

			Assert.Equal("Aspirant's Cap", RdbItemImporter.ExtractName(t));
			Assert.Equal("No description available", RdbItemImporter.ExtractText(t).Description);
		}

		[Fact]
		public void OnlyAssetName_YieldsNull()
		{
			// NPC/TEST items carry only an asset name - no display name to extract.
			var t = Template(StringField("NPC_Pirate_sailor_head_noearring_(S0047/S0048)", ""));
			Assert.Null(RdbItemImporter.ExtractName(t));
		}

		[Fact]
		public void PlainName_FlagsZero_IsReturned()
		{
			// The case that always worked: no description, no escapes.
			var t = Template(StringField("Dusklight Tunic", ""));
			Assert.Equal("Dusklight Tunic", RdbItemImporter.ExtractName(t));
		}

		// Wraps text as a template string field: [u32 tag 0x21][u16 nameLen][u16 descLen][name][description].
		private static byte[] StringField(string text, string description)
		{
			var bytes = Encoding.ASCII.GetBytes(text);
			var descriptionBytes = Encoding.ASCII.GetBytes(description);
			var buf = new byte[8 + bytes.Length + descriptionBytes.Length];
			buf.WriteU32Le(0, 0x21);
			buf.WriteU16Le(4, (ushort)bytes.Length);
			buf.WriteU16Le(6, (ushort)descriptionBytes.Length);
			bytes.CopyTo(buf.AsSpan(8));
			descriptionBytes.CopyTo(buf.AsSpan(8 + bytes.Length));
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
