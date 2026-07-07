using System;
using Xunit;
using Faolan.Core.Network.Codecs;

namespace Faolan.Tests
{
	// Byte-exact construct of PetIIR_c (0x1F4CB0F7) — the pet owner-assign/remove — vs the captured records in
	// NecroL80_..._casting-with-chat-notes.pcapng (#307, docs/summon-pet-decode.md). owner=0x0802E5D4 throughout.
	public class PetIirCodecTests
	{
		[Fact]
		public void BuildAssign_Combat_MatchesCapture()
		{
			// frame 679: the necro Deathless Acolyte (combat pet 0x00023EDC), kind (0,2,2).
			var bytes = PetIirCodec.BuildAssign(0x0802E5D4, 0x00023EDC, PetIirCodec.CombatKind);
			Assert.Equal("1f4cb0f70000c3500802e5d400000000000000c35000023edc000000000000000200000002", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildAssign_Social_MatchesCapture()
		{
			// frame 470: the generic companion (social pet 0x00023EDB), kind (5,0,1).
			var bytes = PetIirCodec.BuildAssign(0x0802E5D4, 0x00023EDB, PetIirCodec.SocialKind);
			Assert.Equal("1f4cb0f70000c3500802e5d400000000000000c35000023edb000000050000000000000001", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildRemove_MatchesCapture()
		{
			// frame 1167: dismiss the Arcanist (0x00023EDC), mode 1.
			var bytes = PetIirCodec.BuildRemove(0x0802E5D4, 0x00023EDC);
			Assert.Equal("1f4cb0f70000c3500802e5d400000000010000c35000023edc", Convert.ToHexStringLower(bytes));
		}
	}
}
