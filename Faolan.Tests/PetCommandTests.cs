using System;
using System.Linq;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The pet command bar's twelve codes. These gate the derivation, not just the numbers - the numbers are only
	// as good as the argument that placed them, so it is the argument that has to keep holding.
	public class PetCommandTests
	{
		// The client's contiguous `PetCommand_*` string block, in binary order. That order is the enum's order, and it
		// is the whole basis for the codes below.
		private static readonly string[] ClientOrder =
		[
			"Assist", "Follow", "Defend", "AttackTag", "CeaseFire",
			"AllAssist", "AllFollow", "AllDefend", "AllAttackTag",
			"DismissFollower", "Dismiss", "AllDismiss"
		];

		[Fact]
		public void TheEnum_IsTheClientsBlock_Contiguous_FromTheDerivedBase()
		{
			var values = ClientOrder.Select(n => (uint)Enum.Parse<PetCommand>(n)).ToArray();

			Assert.Equal(0x1EFu, values[0]);
			for (var i = 1; i < values.Length; i++)
				Assert.True(values[i] == values[i - 1] + 1, $"PetCommand_{ClientOrder[i]} must sit one after PetCommand_{ClientOrder[i - 1]} - the block is contiguous in the client");

			Assert.Equal(ClientOrder.Length, Enum.GetValues<PetCommand>().Length); // nothing invented alongside it
		}

		// The three codes the annotated captures attribute, and the only reason the base is 0x1EF rather than a guess.
		// If any of these three stops matching its capture note, the alignment has to be re-derived from scratch.
		[Theory]
		[InlineData(0x1F4u, PetCommand.AllAssist)] // "set app pets to assist"
		[InlineData(0x1F6u, PetCommand.AllDefend)] // "set app pets to defend", x3
		[InlineData(0x1F8u, PetCommand.DismissFollower)] // "dismissing arcanist" + the server despawn
		public void TheCaptureAnchors_LandOnSemanticallyMatchingNames(uint code, PetCommand expected)
		{
			Assert.Equal(expected, (PetCommand)code);
		}

		// The uniqueness argument, executable: slide the client's block over every plausible base and require an
		// assist-like at 0x1F4, a defend-like at 0x1F6 and a dismiss-like at 0x1F8. Exactly one base satisfies all
		// three - which is what makes this a derivation rather than a fit. The rival base 0x1F4 is the one to beat: it
		// matches assist and defend but puts CeaseFire where a pet was captured being despawned.
		[Fact]
		public void ExactlyOneBase_SatisfiesAllThreeAnchors()
		{
			static string? Kind(string n) => n.Contains("Assist", StringComparison.Ordinal)
				? "assist"
				: n.Contains("Defend", StringComparison.Ordinal)
					? "defend"
					: n.Contains("Dismiss", StringComparison.Ordinal)
						? "dismiss"
						: null;

			string? At(int b, int code)
			{
				var i = code - b;
				return i >= 0 && i < ClientOrder.Length ? ClientOrder[i] : null;
			}

			var fitting = Enumerable.Range(0x1E0, 0x20)
				.Where(b => Kind(At(b, 0x1F4) ?? "") == "assist" && Kind(At(b, 0x1F6) ?? "") == "defend" && Kind(At(b, 0x1F8) ?? "") == "dismiss")
				.ToList();

			Assert.Equal([0x1EF], fitting);
		}

		// The independent second derivation: the bar's ability slots map to the all-pets codes in descending order.
		// It is a descending relation - easy to miss if only an ascending one is looked for - and it agrees with the
		// string block on exactly the pair that block leaves unattributed, so the two unknowns are settled twice over.
		[Theory]
		[InlineData(264u, PetCommand.AllAssist)] // Ability "Pet Assist"
		[InlineData(263u, PetCommand.AllFollow)] // Ability "Pet Follow"
		[InlineData(262u, PetCommand.AllDefend)] // Ability "Pet Defend"
		[InlineData(261u, PetCommand.AllAttackTag)] // Ability "Pet Attack"
		public void TheBarsAbilitySlots_MapToTheAllPetsCodes_Descending(uint abilitySlot, PetCommand expected)
		{
			Assert.Equal((uint)expected, 764 - abilitySlot);
		}
	}
}
