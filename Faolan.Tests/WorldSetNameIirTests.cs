using System;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// Locks the in-world SetNameIIR (0xADCE0CDA) record grammar recovered byte-exact from the client:
	// n3InfoItemRemote::Construct (FUN_005b9d30) reads [u32 type][u32 handle][u8 isToBePassed], then
	// SetNameIIR_t::ReadSubClass (FUN_007ae1d0) reads [u32 nameType][u8][u8][u32 nameLen][name], and Activate
	// (FUN_007ae3e0 -> FUN_006ed100) applies it only when nameType is 1..4 (3 also refreshes the OverheadText).
	// This is the dev spawn-area nameplate override (.togglenpcgroup) - distinct from the create-editor `SetNames.Accepted` form.
	public class WorldSetNameIirTests
	{
		[Fact]
		public void BuildWorldSetNameIir_CapsNameAt31Bytes()
		{
			// The client rejects nameLen >= 0x20 (32), so a longer label is trimmed to exactly 31 bytes.
			var record = SetNameCodec.Build(SetNames.Nameplate(1, new string('x', 40)));
			// layout: 4 (subop) + 4+4+1 (type/handle/isToBePassed) + 4 (nameType) + 1 + 1 + 4 (nameLen) + 31 (name) = 54
			Assert.Equal(54, record.Length);
			Assert.Equal(31u, record.ReadU32Be(19));
			Assert.Equal(new string('x', 31), Encoding.ASCII.GetString(record, 23, 31));
		}

		[Fact]
		public void BuildWorldSetNameIir_DefaultTypeIsCharacter()
		{
			var record = SetNameCodec.Build(SetNames.Nameplate(0x11223344, "z"));

			Assert.Equal(EntityClass.Character, record.ReadU32Be(4));
		}

		[Fact]
		public void BuildWorldSetNameIir_MatchesRecoveredGrammar()
		{
			// [u32 subop=ADCE0CDA][u32 type=C350][u32 handle=0009003C][u8 isToBePassed=00][u32 nameType=00000003][u8 01][u8 01]
			//   [u32 nameLen=00000005]["Ape2A"]
			var record = SetNameCodec.Build(SetNames.Nameplate(0x0009003C, "Ape2A"));
			Assert.Equal("adce0cda0000c3500009003c00000000030101000000054170653241", Convert.ToHexString(record).ToLowerInvariant());
		}
	}
}
