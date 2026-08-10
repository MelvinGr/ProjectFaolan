using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #768: WHEN retail sends a DispatchLockTemplate after a cast, and WHAT it puts in it. The ticket's premise -
	// "retail is selective, we send one on every cast" - came from counting LockTemplate bodies in a 45-packet window
	// without attributing each one to a cast; taking that apart leaves a rule with two exemptions and no residue.
	//
	// The spells below are the corpus' own, and each assertion names the measurement behind it.
	public class SpellCooldownPolicyTests
	{
		private const uint FiresOfGehennaRank1 = 1148653; // recast 1.0 s - 93 casts, ZERO locks
		private const uint HellfireBreathRank1 = 3147519; // recast 2.0 s over a 2.8 s cast - 32 casts, 32 locks
		private const uint FleshToWormsRank1 = 1206011; // recast 6.0 s over a 1.2 s cast - locks read 6.00
		private const uint UndeadMinionMutilator = 1212125; // a pet-POINT summon - 28 casts of the family, zero locks
		private const uint AncientSoul = 4076431; // summons too, but costs no points - 7 casts, 7 locks

		// The boundary. Nothing in the corpus has a recast strictly between 1.0 s and 2.0 s, so this is where the
		// line falls; the spell below it was cast 93 times and never locked.
		[Fact]
		public async Task ARecastNoLongerThanTheGlobalCooldown_DoesNotLock()
		{
			var spells = await Spells();

			Assert.Equal(100u, spells[FiresOfGehennaRank1].RecastCentiseconds);
			Assert.False(SpellCooldownPolicy.Locks(spells[FiresOfGehennaRank1]));

			// ...and the shortest recast retail WAS seen to lock does.
			Assert.Equal(200u, spells[HellfireBreathRank1].RecastCentiseconds);
			Assert.True(SpellCooldownPolicy.Locks(spells[HellfireBreathRank1]));
		}

		// ⚠️ The case the old "send what is LEFT of the recast, measured from the cast begin" arithmetic got wrong in
		// both directions: a 2.0 s recast on a 2.8 s cast has nothing left at completion, so we sent NOTHING where
		// retail sends a full 2.00.
		[Fact]
		public async Task ARecastShorterThanItsOwnCast_StillLocks_ForItsWholeLength()
		{
			var spells = await Spells();
			var breath = spells[HellfireBreathRank1];

			Assert.True(breath.CastTime > breath.RecastCentiseconds * 10); // 2.5 s cast, 2.0 s recast
			Assert.True(SpellCooldownPolicy.Locks(breath));
			Assert.Equal(2.0f, SpellCooldownPolicy.LockSeconds(breath));
		}

		// The value is the WHOLE recast, cast time included - 40 of the 43 spells the corpus locks carry exactly it.
		[Fact]
		public async Task TheLockCarriesTheWholeRecast_NotWhatIsLeftOfIt()
		{
			var spells = await Spells();
			var worms = spells[FleshToWormsRank1];

			Assert.Equal(600u, worms.RecastCentiseconds);
			Assert.Equal(6.0f, SpellCooldownPolicy.LockSeconds(worms));
		}

		// The second exemption, and the discriminator is the POINT COST rather than "is a summon": the eleven
		// pet-point summons never lock across 28 captured casts, while Ancient Soul - a summon that costs no
		// points - locks on 7 of 7.
		[Fact]
		public async Task APetPointSummonDoesNotLock_ThoughAFreeSummonDoes()
		{
			var spells = await Spells();

			Assert.True(spells[UndeadMinionMutilator].SummonPointCost > 0);
			Assert.False(SpellCooldownPolicy.Locks(spells[UndeadMinionMutilator]));

			Assert.Equal(0u, spells[AncientSoul].SummonPointCost ?? 0);
			Assert.True(SpellCooldownPolicy.Locks(spells[AncientSoul]));
			Assert.Equal(60.0f, SpellCooldownPolicy.LockSeconds(spells[AncientSoul]));
		}

		// A spell with no recast at all never locks - true on both sides before and after #768, stated so the
		// boundary constant cannot be lowered past it by accident.
		[Fact]
		public async Task ASpellWithNoRecast_NeverLocks()
		{
			var spells = await Spells();

			Assert.All(spells.Values.Where(s => s.RecastCentiseconds == 0).Take(50), s => Assert.False(SpellCooldownPolicy.Locks(s)));
		}

		private static async Task<Dictionary<uint, Spell>> Spells()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			return await db.Spells.AsNoTracking().ToDictionaryAsync(s => s.Id, TestContext.Current.CancellationToken);
		}
	}
}
