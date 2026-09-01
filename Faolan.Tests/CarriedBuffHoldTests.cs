using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// #907: a live buff must survive a ZONE CHANGE, because the entity that held it does not.
	//
	// `PlayerRegistry` is keyed by CONNECTION and a map change tears the world connection down, so the re-entry
	// builds a brand-new `WorldEntity` and everything the old one held that is not persisted is dropped. For a
	// debuff that is not cosmetic: with no `ActiveBuff` entry left, `TickBuffExpiry` never expires it and never
	// sends its removal twin, and the client — which keeps its own icon list across a map change — shows it at 0s
	// for ever. Exactly one `Butcher` stuck that way in the reported session: the one live at the instant of the
	// teleport, with every earlier one having expired normally.
	public class CarriedBuffHoldTests
	{
		private const uint Character = 0x0100_0001;
		private const uint Butcher = 3232488;

		private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

		// The buff the ticket is named after: `Butcher`, the Tortage pirates' attack spell, 4 s off its cast
		// payload's duration field.
		private static KeyValuePair<uint, ActiveBuff> Buff(uint id, DateTimeOffset appliedAt, float? seconds = 4f)
		{
			return new KeyValuePair<uint, ActiveBuff>(id, new ActiveBuff(id, 0x0007_F45F, appliedAt, seconds));
		}

		[Fact]
		public void ALiveBuff_SurvivesTheHandoff()
		{
			var hold = new CarriedBuffHold();

			hold.Carry(Character, [Buff(Butcher, Now.AddSeconds(-1))], Now);

			var carried = Assert.Single(hold.Consume(Character));
			Assert.Equal(Butcher, carried.Key);
			Assert.Equal(0x0007_F45Fu, carried.Value.CasterHandle); // …with the NPC that cast it, so the twin mirrors the apply
		}

		// ⚠️ An ALREADY-EXPIRED buff is dropped rather than carried. The tick removes an expired buff and sends its
		// twin, so one still in the map at tear-down is one the tick has not reached yet — carrying a dead one would
		// re-arm an icon the client has already been told about.
		[Fact]
		public void AnExpiredBuff_IsNotCarried()
		{
			var hold = new CarriedBuffHold();

			hold.Carry(Character, [Buff(Butcher, Now.AddSeconds(-10))], Now);

			Assert.Empty(hold.Consume(Character));
		}

		// A buff with no duration lives until it is cancelled, so it is never "expired" and always carries.
		[Fact]
		public void ABuffWithNoDuration_Carries()
		{
			var hold = new CarriedBuffHold();

			hold.Carry(Character, [Buff(Butcher, Now.AddYears(-1), null)], Now);

			Assert.Single(hold.Consume(Character));
		}

		// ⭐ Consuming EMPTIES the hold. Without that a second world entry — a teleport straight back, a relog —
		// would re-arm buffs that have long since expired, which is the same stuck icon from the other direction.
		[Fact]
		public void ConsumingIsOneShot()
		{
			var hold = new CarriedBuffHold();
			hold.Carry(Character, [Buff(Butcher, Now)], Now);

			Assert.Single(hold.Consume(Character));
			Assert.Empty(hold.Consume(Character));
		}

		[Fact]
		public void ACharacterThatCarriedNothing_ConsumesNothing()
		{
			Assert.Empty(new CarriedBuffHold().Consume(Character));
		}

		// Carrying an all-expired set must CLEAR any earlier hold rather than leave it standing — otherwise a second
		// zone change would restore the first one's buffs.
		[Fact]
		public void CarryingNothingLive_ClearsWhatWasHeld()
		{
			var hold = new CarriedBuffHold();
			hold.Carry(Character, [Buff(Butcher, Now)], Now);

			hold.Carry(Character, [Buff(Butcher, Now.AddSeconds(-10))], Now);

			Assert.Empty(hold.Consume(Character));
		}

		// The hold is per CHARACTER — two players changing zone at once must not inherit each other's debuffs.
		[Fact]
		public void TheHoldIsPerCharacter()
		{
			var hold = new CarriedBuffHold();
			hold.Carry(Character, [Buff(Butcher, Now)], Now);
			hold.Carry(Character + 1, [Buff(Butcher + 1, Now)], Now);

			Assert.Equal(Butcher, hold.Consume(Character).Single().Key);
			Assert.Equal(Butcher + 1, hold.Consume(Character + 1).Single().Key);
		}
	}
}
