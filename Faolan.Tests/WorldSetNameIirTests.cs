using System;
using System.Text;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// Locks the in-WORLD SetNameIIR (0xADCE0CDA) record grammar recovered byte-exact from the client (#363):
	// n3InfoItemRemote::Construct (FUN_005b9d30) reads [u32 type][u32 handle][u8 isToBePassed], then
	// SetNameIIR_t::ReadSubClass (FUN_007ae1d0) reads [u32 nameType][u8][u8][u32 nameLen][name], and Activate
	// (FUN_007ae3e0 -> FUN_006ed100) applies it only when nameType is 1..4 (3 also refreshes the OverheadText).
	// This is the dev spawn-area nameplate override (.togglenpcgroup) - distinct from the create-editor BuildSetNameIir.
	public class WorldSetNameIirTests
	{
		[Fact]
		public void BuildWorldSetNameIir_MatchesRecoveredGrammar()
		{
			// [u32 type=C350][u32 handle=0009003C][u8 isToBePassed=00][u32 nameType=00000003][u8 01][u8 01]
			//   [u32 nameLen=00000005]["Ape2A"]
			var body = CreateEditorRecordBuilder.BuildWorldSetNameIir(0x0009003C, "Ape2A");
			Assert.Equal("0000c3500009003c00000000030101000000054170653241", Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildWorldSetNameIir_CapsNameAt31Bytes()
		{
			// The client rejects nameLen >= 0x20 (32), so a longer label is trimmed to exactly 31 bytes.
			var body = CreateEditorRecordBuilder.BuildWorldSetNameIir(1, new string('x', 40));
			// layout: 4+4+1 (type/handle/isToBePassed) + 4 (nameType) + 1 + 1 + 4 (nameLen) + 31 (name) = 50
			Assert.Equal(50, body.Length);
			var declaredLen = (uint)((body[15] << 24) | (body[16] << 16) | (body[17] << 8) | body[18]);
			Assert.Equal(31u, declaredLen);
			Assert.Equal(new string('x', 31), Encoding.ASCII.GetString(body, 19, 31));
		}

		[Fact]
		public void BuildWorldSetNameIir_DefaultTypeIsCharacter()
		{
			var body = CreateEditorRecordBuilder.BuildWorldSetNameIir(0x11223344, "z");
			var type = (uint)((body[0] << 24) | (body[1] << 16) | (body[2] << 8) | body[3]);
			Assert.Equal(EntityClass.Character, type);
		}
	}
}
