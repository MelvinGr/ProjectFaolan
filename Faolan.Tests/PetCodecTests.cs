using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of PetIIR (0x1F4CB0F7) - the pet owner-assign/remove - vs the captured records in
	// NecroL80_..._casting-with-chat-notes.pcapng (#307, docs/subsystems/summon-pet-decode.md). owner=0x0802E5D4 throughout.
	public class PetCodecTests
	{
		[Fact]
		public void Assign_Combat_MatchesCapture()
		{
			// frame 679: the necro Deathless Acolyte (combat pet 0x00023EDC), kind (0,2,2).
			// ⭐ The 2 in the middle is the pet's POINT COST, not a constant (#823) - a Deathless Acolyte costs 2, which
			// is exactly why this golden survived a builder that sent 2 for every pet in the game.
			var bytes = PetCodec.Build(PetBindings.Assign(0x0802E5D4, 0x00023EDC, PetBindings.CombatKind(2)));
			Assert.Equal("1f4cb0f70000c3500802e5d400000000000000c35000023edc000000000000000200000002", Convert.ToHexStringLower(bytes));
		}

		// ⭐ The three cost classes the corpus holds, and what retail assigns for each (#823). Measured by
		// `--report pet-cost` over 62 owner-assigns resolved to a costed creature: `f2` equals the cost on 61 of them,
		// and the shapes are (0,1,1) for the cost-1 pets, (0,2,2) for the cost-2 and (0,4,2) for the cost-4.
		//
		// ⚠️ `f3` is the fitted half and this pins only what was observed: 1 at cost 1, 2 at costs 2 AND 4. A cost-3
		// pet is what would separate "a coarse tier" from "a second copy of the cost", and the corpus has none.
		[Theory]
		[InlineData(1u, 0u, 1u, 1u)]
		[InlineData(2u, 0u, 2u, 2u)]
		[InlineData(4u, 0u, 4u, 2u)]
		public void CombatKind_CarriesThePointCost(uint cost, uint f1, uint f2, uint f3)
		{
			Assert.Equal((f1, f2, f3), PetBindings.CombatKind(cost));
		}

		[Fact]
		public void Assign_Social_MatchesCapture()
		{
			// frame 470: the generic companion (social pet 0x00023EDB), kind (5,0,1).
			var bytes = PetCodec.Build(PetBindings.Assign(0x0802E5D4, 0x00023EDB, PetBindings.SocialKind));
			Assert.Equal("1f4cb0f70000c3500802e5d400000000000000c35000023edb000000050000000000000001", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void Remove_MatchesCapture()
		{
			// frame 1167: dismiss the Arcanist (0x00023EDC), mode 1.
			var bytes = PetCodec.Build(PetBindings.Remove(0x0802E5D4, 0x00023EDC));
			Assert.Equal("1f4cb0f70000c3500802e5d400000000010000c35000023edc", Convert.ToHexStringLower(bytes));
		}

		// Parse is gated on the CAPTURED bytes above, not on a round-trip of our own builder: a build->parse->build
		// loop agrees with itself even when both halves read the layout wrong, which is the whole failure mode these
		// golden tests exist to catch.
		[Theory]
		// frame 679 assign (combat), frame 470 assign (social), frame 1167 remove.
		[InlineData("1f4cb0f70000c3500802e5d400000000000000c35000023edc000000000000000200000002", 0u, 0x00023EDCu)]
		[InlineData("1f4cb0f70000c3500802e5d400000000000000c35000023edb000000050000000000000001", 0u, 0x00023EDBu)]
		[InlineData("1f4cb0f70000c3500802e5d400000000010000c35000023edc", 1u, 0x00023EDCu)]
		public void Parse_ReadsOwnerBindingFromCapturedBytes(string hex, uint mode, uint pet)
		{
			var parsed = PetCodec.Parse(Convert.FromHexString(hex));
			Assert.Equal(mode, parsed.Mode);
			Assert.Equal(0x0802E5D4u, parsed.Owner);
			Assert.Equal(pet, parsed.Pet);
			Assert.Equal(Convert.FromHexString(hex), PetCodec.Build(parsed));
		}

		[Theory]
		[InlineData("")]                                        // empty
		[InlineData("1f4cb0f70000c3500802e5d4000000000100")]     // truncated before the pet handle
		[InlineData("fec4158a0000c3500802e5d400000000010000c35000023edc")] // a DamageResultIIR, not a PetIIR
		[InlineData("1f4cb0f70000c3500802e5d400000000020000c35000023edc")] // a mode the client's own reader rejects
		public void Parse_RejectsWhatIsNotAnOwnerBinding(string hex)
		{
			var body = Convert.FromHexString(hex);
			Assert.Throws<CodecParseException>(() => PetCodec.Parse(body));
		}
	}
}
