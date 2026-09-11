using System;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The player stances: retail's answer to a `PerformSpecialAction` press is a player-addressed
	// `BehaviorControlCenterIIR`, and the client waits for it before the stance visibly takes. These are byte-exact
	// goldens against the records the corpus contains, re-derivable with
	// `--report player-actions --report-arg <selector>`.
	//
	// Two characters of two classes appear, which is the point: the animation action id is the character's and
	// everything else about the record is shared, so a golden on one class alone would not show which half varies.
	public class PlayerStanceCodecTests
	{
		private const uint Necro = 0x0802E5D4; // NecroL80, necromancer
		private const uint Hox = 0x1101B4F5; // Hakotep, Herald of Xotli

		// Search (selector 0x064) - the cleanest pairing in the corpus: four presses, four answers, identical apart
		// from the actor and the action id. flags 8, activation 159 and the five-byte zero tail are the same on both.
		[Theory]
		[InlineData("642CD3D60000C3500802E5D40004000000200000000D089DC802180628083000609F010000000000", Necro, 42013u, 6u)]
		[InlineData("642CD3D60000C3501101B4F50004000000200000000D08E39E03180528083000609F010000000000", Hox, 53091u, 5u)]
		public void SearchStance_MatchesTheCapturedRecord(string hex, uint actor, uint animAction, uint phase)
		{
			Assert.Equal(Bytes(hex), BehaviorControlCenterCodec.Build(BehaviorAnimations.SearchStance(actor, animAction, phase)));
		}

		// The one capture whose search tail leads with 01 rather than 00. It is passed rather than pinned: a single
		// differing sighting is a datum, not a constant, and a builder that could not produce it would be asserting
		// the zero is part of the record's identity.
		[Fact]
		public void SearchStance_CarriesTheLeadingTailByte()
		{
			Assert.Equal(Bytes("642CD3D60000C3500802E5D40004000000200000000D089DC802180628083000609F010100000000"), BehaviorControlCenterCodec.Build(BehaviorAnimations.SearchStance(Necro, 42013, 6, 1)));
		}

		// Hide, entering (selector 0x068). Unlike every other stance here the flags word is 10, the client's command
		// id is echoed back, and the four-byte tail is the shadow coverage the client itself reported a moment
		// earlier - 100 in both captures.
		[Theory]
		[InlineData("642CD3D60000C3500802E5D400040000001E0000000C089EC80210031805280A300000000064", Necro, 42014u, 5u, 3u)]
		[InlineData("642CD3D60000C3501101B4F500040000001E0000000C0881B60B10031803280A300000000064", Hox, 187137u, 3u, 3u)]
		public void SneakHideEnter_MatchesTheCapturedRecord(string hex, uint actor, uint animAction, uint phase, uint clientCommandId)
		{
			Assert.Equal(Bytes(hex), BehaviorControlCenterCodec.Build(BehaviorAnimations.SneakHideEnter(actor, animAction, phase, clientCommandId, 100)));
		}

		// Hide, leaving. The action id is not class-specific - both captured characters exit with the same one, which
		// is why it is a constant rather than a third per-class map.
		[Fact]
		public void SneakHideExit_MatchesTheCapturedRecord_AndIsNotClassSpecific()
		{
			Assert.Equal(Bytes("642CD3D60000C3500802E5D400040000001E0000000A08AAD6021807280830000000000000000000"),
				BehaviorControlCenterCodec.Build(BehaviorAnimations.SneakHideExit(Necro, StartingData.SneakHideExitAction, 7)));

			// Same id on the Herald, from a different capture and a different character - the asymmetry this test
			// exists for: the enter animation is the character's own sneak pose, the exit is the shared stand-up.
			Assert.Equal(43818u, StartingData.SneakHideExitAction);
		}

		// The per-class maps hold only the classes a capture shows, deliberately - the same call
		// StartingData.SpellweaveToggleAction makes. An unmapped class must be absent so the handler logs and does
		// nothing, rather than borrowing another class's animation and reading as working.
		[Fact]
		public void TheStanceActionMaps_HoldOnlyTheCapturedClasses()
		{
			Assert.Equal([ConanClass.Necromancer, ConanClass.HeraldOfXotli], StartingData.SearchStanceAction.Keys.Order());
			Assert.Equal([ConanClass.Necromancer, ConanClass.HeraldOfXotli], StartingData.SneakHideEnterAction.Keys.Order());
			Assert.DoesNotContain(ConanClass.Demonologist, StartingData.SearchStanceAction.Keys);
		}

		// Rest (selector 0x06A). Not a stance record at all - a self-cast of the `Resting` spell (3164147, "Recovering
		// health, mana, energy, and stamina"), which is why it rides slot 4 MagicAttack. The action id is the generic
		// cast animation shared with the Path of Asura recall, so unlike search and hide this one is neither class-
		// nor spell-specific: two classes, two captures, one id.
		[Theory]
		[InlineData("642CD3D60000C3500802E5D40004000000040000001808C0E80A180828083000520908D0860310D4CB8B4060821000000001003047F30100000000000000000000080200", Necro, 8u)]
		[InlineData("642CD3D60000C35001316E710004000000040000001808C0E80A181028083000520908D0860310F1DCC50960821000000001003047F30100000000000000000000080200", 0x01316E71u, 16u)]
		public void RestCast_MatchesTheCapturedRecord(string hex, uint actor, uint phase)
		{
			Assert.Equal(Bytes(hex), BehaviorControlCenterCodec.Build(BehaviorAnimations.RestCast(actor, phase)));
		}

		private static byte[] Bytes(string hex)
		{
			return Convert.FromHexString(hex);
		}
	}
}
