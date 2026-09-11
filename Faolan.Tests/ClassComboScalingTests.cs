using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Importer.Modules;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The client's own combo scaling table (RDB 1000513 `RDBComboLookup_c`) as we shipped it.
	//
	// Two halves, because the risk is in two places. The parse gate holds the record layout - the client's parser
	// reads six u32 per row, discards the third and scales two of them, and getting the stride wrong still produces
	// plausible-looking numbers, so the parser has to reject a blob whose length does not divide. The seed gate holds
	// the shipped rows to the shape the client demands of them: it looks a row up by (class, level) for every class
	// and level and logs "MISSING combo lookup data" when one is absent, so a partial import is a real defect.
	public class ClassComboScalingTests
	{
		private const uint Type = ClassComboScalingModule.ComboLookupType;

		// The 12 classes the shipped table carries, which is every ConanClass - the client's own check constraint on
		// this column would be the same list.
		private static readonly ConanClass[] AllClasses = Enum.GetValues<ConanClass>();

		// The four classes whose 80 rows are entirely zero: they carry no combo scaling data at all and the client
		// always falls back to 1.0 for them. Not a gap in the import - it is what the table ships.
		private static readonly ConanClass[] NoDpsClasses =
			[ConanClass.PriestOfMitra, ConanClass.TempestOfSet, ConanClass.Necromancer, ConanClass.Demonologist];

		[Fact]
		public async Task ShippedTable_CoversEveryClassAndLevel()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.ClassComboScalings.ToListAsync(TestContext.Current.CancellationToken);

			Assert.Equal(AllClasses.Length * (int)ClassComboScalingModule.MaxLevel, rows.Count);
			foreach (var cls in AllClasses)
			{
				var levels = rows.Where(r => r.Class == cls).Select(r => r.Level).OrderBy(l => l);
				Assert.Equal(Enumerable.Range(1, (int)ClassComboScalingModule.MaxLevel).Select(l => (uint)l), levels);
			}
		}

		// The stamina divisor is the one column the client uses as read (it divides the combo's stamina cost by it),
		// and it follows a flat rule: 32 at levels 1-9 dropping one per decade to 24 at 80 for the eight combo
		// classes, a flat 20 for the four without. A reimport that shifted the columns would break this before it
		// broke anything visible.
		[Fact]
		public async Task StaminaDivisor_FollowsThePerDecadeCurve()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.ClassComboScalings.ToListAsync(TestContext.Current.CancellationToken);

			foreach (var r in rows)
			{
				var expected = NoDpsClasses.Contains(r.Class)
					? 20u
					: 32 - r.Level / 10;
				Assert.True(r.StaminaDivisor == expected,
					$"{r.Class} level {r.Level}: stamina divisor {r.StaminaDivisor}, expected {expected}");
			}
		}

		// The DPS modifier rises strictly with level for every class that has one, and is flat zero for the four that
		// do not. Strictness is the useful assertion: a column mix-up would still be monotone-ish but would not be
		// strictly increasing 80 times over.
		[Fact]
		public async Task DpsModifier_RisesWithLevel_OrIsAbsentEntirely()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.ClassComboScalings.ToListAsync(TestContext.Current.CancellationToken);

			foreach (var cls in AllClasses)
			{
				var byLevel = rows.Where(r => r.Class == cls).OrderBy(r => r.Level).Select(r => r.DpsModifier).ToList();
				if (NoDpsClasses.Contains(cls))
				{
					Assert.All(byLevel, v => Assert.Equal(0u, v));
					continue;
				}

				Assert.Equal(byLevel.OrderBy(v => v), byLevel);
				Assert.Equal(byLevel.Distinct().Count(), byLevel.Count);
			}
		}

		// The secondary column is non-zero for exactly the three soldier classes, and exactly linear in level there
		// (Guardian 500/level, Conqueror and Dark Templar 475). Nothing in the client reads it back, so this test is
		// the only thing standing between it and a silent change.
		[Fact]
		public async Task SecondaryModifier_IsTheSoldiersLinearColumn()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.ClassComboScalings.ToListAsync(TestContext.Current.CancellationToken);

			var perLevel = new Dictionary<ConanClass, uint>
			{
				[ConanClass.Guardian] = 500,
				[ConanClass.Conqueror] = 475,
				[ConanClass.DarkTemplar] = 475
			};

			foreach (var r in rows)
			{
				var expected = perLevel.TryGetValue(r.Class, out var step)
					? step * r.Level
					: 0u;
				Assert.True(r.SecondaryModifier == expected,
					$"{r.Class} level {r.Level}: secondary modifier {r.SecondaryModifier}, expected {expected}");
			}
		}

		[Fact]
		public void Parse_ReadsTheSixWordRow()
		{
			var rows = ClassComboScalingModule.Parse(Blob([
				[(uint)ConanClass.Barbarian, 1, 0, 338, 0, 32],
				[(uint)ConanClass.Guardian, 80, 0, 12742, 40000, 24]
			]));

			Assert.Equal(2, rows.Count);
			Assert.Equal((ConanClass.Barbarian, 1u, 338u, 0u, 32u),
				(rows[0].Class, rows[0].Level, rows[0].DpsModifier, rows[0].SecondaryModifier, rows[0].StaminaDivisor));
			Assert.Equal((ConanClass.Guardian, 80u, 12742u, 40000u, 24u),
				(rows[1].Class, rows[1].Level, rows[1].DpsModifier, rows[1].SecondaryModifier, rows[1].StaminaDivisor));
		}

		// A trailing byte means the stride assumption is wrong, and a stride that is wrong by a word still yields rows
		// that parse - so the length check, not the field values, is what catches it.
		[Fact]
		public void Parse_RejectsABlobThatDoesNotDivide()
		{
			var truncated = Blob([[(uint)ConanClass.Barbarian, 1, 0, 338, 0, 32]])[..^4];
			var ex = Assert.Throws<InvalidOperationException>(() => ClassComboScalingModule.Parse(truncated));
			Assert.Contains("the stride is wrong", ex.Message);
		}

		// The third word is the one the client reads and throws away. It is zero in all 960 shipped rows, which is
		// what lets us claim it is padding rather than a column we failed to decode - so a non-zero one has to fail
		// loudly instead of being dropped.
		[Fact]
		public void Parse_RejectsANonZeroDiscardedWord()
		{
			var ex = Assert.Throws<InvalidOperationException>(
				() => ClassComboScalingModule.Parse(Blob([[(uint)ConanClass.Barbarian, 1, 7, 338, 0, 32]])));
			Assert.Contains("the client discards", ex.Message);
		}

		// The 16-byte `[type][id][version][count]` header plus the rows, little-endian - the on-disk form of a
		// decompressed 1000513 entry.
		private static byte[] Blob(uint[][] rows)
		{
			var words = new List<uint> { Type, 0, 1, (uint)rows.Length };
			foreach (var r in rows)
				words.AddRange(r);
			return words.SelectMany(BitConverter.GetBytes).ToArray();
		}
	}
}
