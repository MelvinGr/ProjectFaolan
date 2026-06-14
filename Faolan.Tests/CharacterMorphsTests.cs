using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;
using Xunit;

namespace Faolan.Tests
{
	// Locks the editor-slider -> spawn-morph mapping (zigzag-decoded value / 1000, face slot = wire sliderId,
	// fixed body slider ids, retail entry order) against the Hoxxling all-sliders create capture: feeding the
	// raw SetMorphValue stream through TryApply and generating entries must reproduce his 28 captured morph
	// entries bit-exact (tags AND float bits).
	public class CharacterMorphsTests
	{
		// Hoxxling's SetMorphValue stream, verbatim from
		// account-faolan-character-hoxxling-create-modified_all_sliders-...json (names as sent, raw varints).
		private static readonly (string Morph, uint Raw)[] HoxxlingStream =
		[
			("02_BrowLow", 88), ("04_CheekForward", 278), ("06_CheekLow", 246), ("08_CheekWide", 224), ("09_ChinShort", 196), ("12_ChinWide", 288), ("13_EarsAngle", 222), ("14_EyeAngle1", 122), ("16_EyesHigh", 98),
			("19_EyesNarrow", 166), ("20_EyesBack", 166), ("22_JawNarrow", 236), ("24_FaceLenght", 222), ("26_MouthLow", 38), ("28_MouthNarrow", 240), ("30_NoseAngleDown", 270), ("31_NoseAngle1", 444),
			("33_NoseCrookedRight", 24), ("35_NoseLenght", 134), ("36_NoseWide", 122), ("arm", 200), ("arse", 31), ("bosom", 22), ("chest", 200), ("ears", 11), ("leg", 180), ("stomach", 99), ("thigh", 172)
		];

		// His spawn's 28 morph scaffold entries (idx 7..34), verbatim from the same capture.
		private static readonly (ushort Tag, uint Bits)[] HoxxlingWire =
		[
			(0x0002, 0x3D343958), (0x0004, 0x3E0E5604), (0x0006, 0x3DFBE76D), (0x0008, 0x3DE56042), (0x0009, 0x3DC8B439), (0x000C, 0x3E1374BC), (0x000D, 0x3DE353F8), (0x000E, 0x3D79DB23), (0x0010, 0x3D48B439),
			(0x0013, 0x3DA9FBE7), (0x0014, 0x3DA9FBE7), (0x0016, 0x3DF1A9FC), (0x0018, 0x3DE353F8), (0x001A, 0x3C9BA5E3), (0x001C, 0x3DF5C28F), (0x001E, 0x3E0A3D71), (0x001F, 0x3E6353F8), (0x0021, 0x3C449BA6),
			(0x0023, 0x3D89374C), (0x0024, 0x3D79DB23), (0x0100, 0x3DCCCCCD), (0x0104, 0xBC83126F), (0x0102, 0x3C343958), (0x0101, 0x3DCCCCCD), (0x00FF, 0xBBC49BA6), (0x0106, 0x3DB851EC), (0x0103, 0xBD4CCCCD),
			(0x0105, 0x3DB020C5)
		];

		[Fact]
		public void BuildEntries_SkipsDefaultSlidersAndNullBody()
		{
			Assert.Empty(CharacterMorphs.BuildEntries(null, 7));
			Assert.Empty(CharacterMorphs.BuildEntries(new CharacterBody(), 7));

			var body = new CharacterBody { Thigh = -30 };
			var only = Assert.Single(CharacterMorphs.BuildEntries(body, 7));
			Assert.Equal((ushort)0x0105, only.Tag);
			Assert.Equal(0xBCF5C28Fu, only.Value); // -0.03f
		}

		[Fact]
		public void HoxxlingEditorStream_GeneratesHisCapturedMorphEntriesBitExact()
		{
			var body = new CharacterBody();
			foreach (var (morph, raw) in HoxxlingStream)
				Assert.True(CharacterMorphs.TryApply(body, morph, raw), morph);

			var entries = CharacterMorphs.BuildEntries(body, 7);

			Assert.Equal(HoxxlingWire.Length, entries.Count);
			for (var i = 0; i < entries.Count; i++)
			{
				Assert.Equal(7u + (uint)i, entries[i].Index);
				Assert.Equal(HoxxlingWire[i].Tag, entries[i].Tag);
				Assert.Equal(HoxxlingWire[i].Bits, entries[i].Value);
			}
		}

		[Fact]
		public void TryApply_DecodesZigzag()
		{
			var body = new CharacterBody();
			Assert.True(CharacterMorphs.TryApply(body, "arse", 31)); // zigzag 31 = -16
			Assert.Equal(-16f, body.Arse);
			Assert.True(CharacterMorphs.TryApply(body, "arm", 200)); // zigzag 200 = +100
			Assert.Equal(100f, body.Arm);
		}

		[Fact]
		public void TryApply_RejectsResourceRefsAndUnknownSliders()
		{
			var body = new CharacterBody();
			Assert.False(CharacterMorphs.TryApply(body, "*texscript_1136498", 2000));
			Assert.False(CharacterMorphs.TryApply(body, "99_NotASlider", 10));
		}
	}
}
