using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Protobuf;
using Faolan.Core.Extensions;
using Faolan.GameServer.Combat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #623 part 2: retail's NPCs swing COMBOS, and which one is decided by the weapon in their hands.
	//
	// The evidence is `--report combo-swings`: over the 484 captured NPC combo swings, every actor resolves to a
	// spawn in the same capture, and grouping the combo against that spawn's worn items separates cleanly - every
	// `combo_npc_generic_polearm` actor holds a polearm, every `_2hb` one a two-handed blunt, and one NPC type with
	// two loadouts (Bat Pict Mauler, template 8799 polearm vs 13960 club) swings the two different combos. So the
	// choice is the WEAPON's and not the creature's, which is what makes it derivable for every armed mob we spawn
	// rather than only for the 40 types some capture happened to record.
	//
	// What is locked here is that premise end to end: the item catalog really does state the weapon class, the
	// mapping covers the shipped spawn set, and the id reaches the wire in the field the client reads.
	public class NpcComboTests
	{
		// The complete `combo_npc_generic_*` set - nine rows, and `NpcWeapon.MeleeComboName` names all nine.
		private static readonly string[] GenericCombos =
		[
			"combo_npc_generic_1hb", "combo_npc_generic_1he", "combo_npc_generic_1he_shield", "combo_npc_generic_2hb",
			"combo_npc_generic_2he", "combo_npc_generic_dagger", "combo_npc_generic_dual_1h", "combo_npc_generic_dualdaggers",
			"combo_npc_generic_polearm"
		];

		// The premise everything else rests on: `Items.SearchCategory` IS the client's weapon class. The shipped
		// catalog separates the seven melee classes perfectly - every 30011 row's name carries the `1hEdge` token and
		// no other, and so on down - so reading the class off the category is a lookup, not a name match (#244).
		[Fact]
		public void TheItemCatalogsSearchCategory_IsTheWeaponClass()
		{
			var tokens = new Dictionary<uint, string>
			{
				[NpcWeapon.OneHandEdge] = "1hEdge",
				[NpcWeapon.TwoHandEdge] = "2hEdge",
				[NpcWeapon.OneHandBlunt] = "1hBlunt",
				[NpcWeapon.TwoHandBlunt] = "2hBlunt",
				[NpcWeapon.Dagger] = "dagger",
				[NpcWeapon.Polearm] = "polearm",
				[NpcWeapon.Shield] = "hield" // Shield/shield - the NPC rows capitalise it, the player ones do not
			};

			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var named = db.Items.Where(i => tokens.Keys.Contains(i.SearchCategory)).Select(i => new { i.SearchCategory, i.Name }).ToList();

			foreach (var (category, token) in tokens)
			{
				var rows = named.Where(i => i.Name.Contains(token, System.StringComparison.Ordinal)).ToList();
				Assert.NotEmpty(rows);
				Assert.All(rows, r => Assert.Equal(category, r.SearchCategory));
			}
		}

		// Every name the mapping can produce is a row we ship. A miss here means the combo import is short, and the
		// runtime would silently swing plain forever.
		[Fact]
		public async Task EveryGenericNpcCombo_ResolvesToACatalogRow()
		{
			var service = Service();

			foreach (var (main, off, expected) in Loadouts())
			{
				Assert.Equal(expected, NpcWeapon.MeleeComboName(Equip(main, off)));
				Assert.NotEqual(0u, await service.NpcMeleeComboId(Equip(main, off)));
			}

			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var shipped = db.Combos.Where(c => c.Name.StartsWith("combo_npc_generic_")).Select(c => c.Name).OrderBy(n => n).ToList();
			Assert.Equal(GenericCombos, shipped);
		}

		// The mapping itself, loadout by loadout. The off hand only ever refines the ONE-handed cases - a shield or a
		// second weapon splits `1he` three ways and `dagger` two - which is exactly the shape of the generic set.
		[Theory]
		[InlineData(NpcWeapon.OneHandEdge, NpcWeapon.Shield, "combo_npc_generic_1he_shield")]
		[InlineData(NpcWeapon.OneHandEdge, NpcWeapon.OneHandEdge, "combo_npc_generic_dual_1h")]
		[InlineData(NpcWeapon.OneHandEdge, 0u, "combo_npc_generic_1he")]
		[InlineData(NpcWeapon.OneHandEdge, 30028u, "combo_npc_generic_1he")] // a quiver in the off hand is not a weapon
		[InlineData(NpcWeapon.OneHandBlunt, NpcWeapon.Shield, "combo_npc_generic_1hb")] // no `_1hb_shield` row exists
		[InlineData(NpcWeapon.TwoHandEdge, 0u, "combo_npc_generic_2he")]
		[InlineData(NpcWeapon.TwoHandBlunt, 0u, "combo_npc_generic_2hb")]
		[InlineData(NpcWeapon.Dagger, NpcWeapon.Dagger, "combo_npc_generic_dualdaggers")]
		[InlineData(NpcWeapon.Dagger, 0u, "combo_npc_generic_dagger")]
		[InlineData(NpcWeapon.Polearm, 0u, "combo_npc_generic_polearm")]
		public void TheWeaponPicksTheCombo(uint main, uint off, string expected)
		{
			Assert.Equal(expected, NpcWeapon.MeleeComboName(Equip(main, off)));
		}

		// The cases that carry NO combo, and they are the ones retail leaves plain too: `Combat:Unarmed` is 0 of 769
		// captured acks. A bow gets none because there is no `combo_npc_generic_bow` row - retail's archers swing the
		// `combo_npc_ranger_bow_*` class family instead, which needs the archetype we do not hold (#675).
		[Theory]
		[InlineData(0u)] // unarmed - every animal
		[InlineData(30017u)] // bow
		[InlineData(30018u)] // crossbow
		[InlineData(30044u)] // staff
		public void AnNpcWithNoMeleeWeapon_CarriesNoCombo(uint main)
		{
			Assert.Null(NpcWeapon.MeleeComboName(Equip(main, 0)));
			Assert.Null(NpcWeapon.MeleeComboName(null));
		}

		// Coverage over the SHIPPED spawn set rather than over the 40 types a capture recorded - which is the whole
		// argument for driving this off the weapon. Every placement whose main hand holds one of the seven melee
		// classes resolves, and nothing else does.
		[Fact]
		public async Task EveryArmedMeleeSpawnInTheShippedSet_ResolvesToACombo()
		{
			var service = Service();
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var melee = new[] { NpcWeapon.OneHandEdge, NpcWeapon.TwoHandEdge, NpcWeapon.OneHandBlunt, NpcWeapon.TwoHandBlunt, NpcWeapon.Dagger, NpcWeapon.Polearm };
			var loadouts = db.NpcSpawnEquipment.Include(e => e.Item).Where(e => e.Pos == NpcWeapon.MainHandPos || e.Pos == NpcWeapon.OffHandPos)
				.GroupBy(e => e.NpcSpawnId).ToList();

			var armed = 0;
			foreach (var spawn in loadouts)
			{
				var main = spawn.FirstOrDefault(e => e.Pos == NpcWeapon.MainHandPos)?.Item.SearchCategory ?? 0;
				var id = await service.NpcMeleeComboId(spawn.Cast<INpcEquipment>().ToList());

				if (!melee.Contains(main))
				{
					Assert.Equal(0u, id);
					continue;
				}

				armed++;
				Assert.NotEqual(0u, id);
			}

			// The corpus recorded combos for 40 NPC types; the weapon route covers every armed placement we ship.
			Assert.True(armed >= 900, $"only {armed} melee-armed spawn placements resolved - the equipment lift looks short");
		}

		// The wire half: the id rides `combo_id` (CommandMessage field 7), the same field a PLAYER's combo swing
		// uses, and nothing else about the ack moves. A plain swing is byte-identical to what it was before the
		// combo existed, which is what keeps the captured frame-6472 golden meaningful.
		[Fact]
		public void TheAck_CarriesTheComboInTheClientsOwnField()
		{
			var plain = BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(0x00002C56, 0x01316D8A, 0x53, 49731, 3, 24, 67, 60, 0xCD));
			var withCombo = BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(0x00002C56, 0x01316D8A, 0x53, 49731, 3, 24, 67, 60, 0xCD, comboId: 3363561));

			Assert.Equal(plain, BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(0x00002C56, 0x01316D8A, 0x53, 49731, 3, 24, 67, 60, 0xCD, comboId: 0)));
			Assert.NotEqual(plain, withCombo);

			var args = BehaviorControlCenterCodec.Parse(withCombo).Args.FromProtobuf<CommandMessage>();
			Assert.True(args.ShouldSerializeComboId());
			Assert.Equal(3363561u, args.ComboId); // combo_npc_generic_polearm
			// An NPC combo is a single `action` marker with no key sequence, so retail never steps it - and protobuf
			// omits the implicit zero, which is why the captured acks carry combo_id alone.
			Assert.False(args.ShouldSerializeComboAttackNum());

			// Every other field is the plain ack's, unchanged.
			var plainArgs = BehaviorControlCenterCodec.Parse(plain).Args.FromProtobuf<CommandMessage>();
			Assert.Equal(plainArgs.BccActionId, args.BccActionId);
			Assert.Equal(plainArgs.CommandqueueServerId, args.CommandqueueServerId);
			Assert.Equal(plainArgs.Flags, args.Flags);
			Assert.Equal(plainArgs.RechargeTime, args.RechargeTime);
			Assert.Equal(plainArgs.ActivationTime, args.ActivationTime);
			Assert.Equal(plainArgs.Target.Instance, args.Target.Instance);
		}

		// The share is measured, not chosen: 427 of the 1,504 captured s2c `Combat:Melee` acks whose actor is a
		// named NPC carry a combo. A mob that combos every swing reads nothing like retail's.
		[Fact]
		public void TheComboShare_IsTheMeasuredCorpusRate()
		{
			Assert.InRange(NpcWeapon.MeleeComboShare, 0.28, 0.29);
			Assert.True(NpcWeapon.ClassComboShare is > 0.58 and < 0.59, $"{NpcWeapon.ClassComboShare}"); // 92 class / 65 generic over the two wired weapons
		}

		// #675: `Combos.Criteria` is the WEAPON CLASS a combo requires, and it maps one-to-one onto the item catalog's
		// own `SearchCategory`. That is what turns the class families from names into a lookup - the polearm rows are
		// the criteria-25 ones, not "the ones with `polearm` in the name".
		//
		// Checked from the CATALOG side over all 744 rows rather than from the nine generic ones: wherever a combo's
		// name carries a weapon token, its Criteria is that weapon's. Nothing in the shipped catalog contradicts it.
		[Theory]
		[InlineData(NpcWeapon.OneHandEdge, 23, "combo_npc_generic_1he")]
		[InlineData(NpcWeapon.Polearm, 25, "combo_npc_generic_polearm")]
		[InlineData(30017u, 26, "combo_npc_ranger_bow_1_ripper_volley")] // bow
		[InlineData(NpcWeapon.OneHandBlunt, 27, "combo_npc_generic_1hb")]
		[InlineData(NpcWeapon.TwoHandEdge, 28, "combo_npc_generic_2he")]
		[InlineData(NpcWeapon.TwoHandBlunt, 29, "combo_npc_generic_2hb")]
		[InlineData(NpcWeapon.Dagger, 30, "combo_npc_generic_dagger")]
		[InlineData(30018u, 32, "combo_npc_ranger_crossbow_1_ripper_barrage")] // crossbow
		public void CombosCriteria_IsTheWeaponClassTheRowRequires(uint searchCategory, int criteria, string witness)
		{
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var row = db.Combos.Single(c => c.Name == witness);
			Assert.Equal(criteria, row.Criteria);

			// ...and the item catalog really does hold weapons of that class, so the two numbering spaces are joined
			// by something we ship rather than by this table alone.
			Assert.True(db.Items.Any(i => i.SearchCategory == searchCategory), $"no item of class {searchCategory}");
		}

		// The two buckets the weapon DOES answer, and they are answered because each has exactly one class family.
		// Corpus, per SPAWN rather than per type (one handle in one capture holds one loadout): all 31 assassin-family
		// swings are by dagger-wielders and all 8 dagger-armed templates swing the family; likewise 61 swings and 5
		// templates for the polearm guardians.
		[Theory]
		[InlineData(NpcWeapon.Dagger, "combo_npc_assassin_")]
		[InlineData(NpcWeapon.Polearm, "combo_npc_guardian_polearm_")]
		public async Task AWeaponThatAnswersTheArchetype_ResolvesItsClassFamily(uint main, string family)
		{
			Assert.Equal(family, NpcWeapon.ClassComboFamily(Equip(main, 0)));

			var ids = await Service().NpcClassComboIds(Equip(main, 0), 80);
			Assert.NotEmpty(ids);

			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var names = db.Combos.Where(c => ids.Contains(c.Id)).Select(c => c.Name).ToList();
			Assert.All(names, n => Assert.StartsWith(family, n, System.StringComparison.Ordinal));
		}

		// ⚠️ The buckets that stay UNANSWERED, pinned so a later "tidy up the rest" pass has to face the evidence
		// rather than widening the switch:
		//
		//   shield    refuted, not merely unsampled - `combo_npc_guardian_shield_*` is swung by 3 of the 6 captured
		//             shield-bearing templates, the three Vanir ones swinging only generic
		//   2h/dual   `barbarian` / `conqueror` / `hox` all appear on two-handers (blunt AND edge) and no template
		//             swings two of them, so it is a per-NPC property with two or three candidates
		//   bow       ANSWERED (ranger is the only family at those criteria and there is no generic bow row) but not
		//             wired: the carrier is wrong, our ranged NPCs send the melee ack. See #676.
		[Theory]
		[InlineData(NpcWeapon.OneHandEdge, NpcWeapon.Shield)]
		[InlineData(NpcWeapon.TwoHandEdge, 0u)]
		[InlineData(NpcWeapon.TwoHandBlunt, 0u)]
		[InlineData(NpcWeapon.OneHandEdge, NpcWeapon.OneHandEdge)]
		[InlineData(NpcWeapon.OneHandBlunt, 0u)]
		[InlineData(30017u, 0u)] // bow - answered, but the ranged ack is #676
		[InlineData(0u, 0u)] // unarmed
		public async Task AWeaponThatDoesNotAnswerTheArchetype_SwingsGenericOnly(uint main, uint off)
		{
			Assert.Null(NpcWeapon.ClassComboFamily(Equip(main, off)));
			Assert.Empty(await Service().NpcClassComboIds(Equip(main, off), 80));
		}

		// The level gate is `Combos.Level` verbatim - the family name's trailing number. Across the 30 captured
		// class-family swings no actor ever swings a row above its own level, and a level-75 mob still swings the
		// rank-1 row, so this is a CEILING and not a bracket: a higher-level NPC's set contains the lower one's.
		[Fact]
		public async Task TheClassFamilyIsGatedByLevel_AsACeilingNotABracket()
		{
			var service = Service();
			var dagger = Equip(NpcWeapon.Dagger, 0);

			var atOne = await service.NpcClassComboIds(dagger, 1);
			var atThirty = await service.NpcClassComboIds(dagger, 30);
			var atEighty = await service.NpcClassComboIds(dagger, 80);

			Assert.NotEmpty(atOne);
			Assert.Subset(atThirty.ToHashSet(), atOne.ToHashSet());
			Assert.Subset(atEighty.ToHashSet(), atThirty.ToHashSet());
			Assert.True(atEighty.Length > atOne.Length, "the level-80 set should be strictly wider than the level-1 one");

			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			Assert.All(db.Combos.Where(c => atOne.Contains(c.Id)).ToList(), c => Assert.True(c.Level <= 1, $"{c.Name} is level {c.Level}"));
		}

		private static IEnumerable<(uint Main, uint Off, string Expected)> Loadouts()
		{
			yield return (NpcWeapon.OneHandEdge, NpcWeapon.Shield, "combo_npc_generic_1he_shield");
			yield return (NpcWeapon.OneHandEdge, NpcWeapon.OneHandEdge, "combo_npc_generic_dual_1h");
			yield return (NpcWeapon.OneHandEdge, 0, "combo_npc_generic_1he");
			yield return (NpcWeapon.OneHandBlunt, 0, "combo_npc_generic_1hb");
			yield return (NpcWeapon.TwoHandEdge, 0, "combo_npc_generic_2he");
			yield return (NpcWeapon.TwoHandBlunt, 0, "combo_npc_generic_2hb");
			yield return (NpcWeapon.Dagger, NpcWeapon.Dagger, "combo_npc_generic_dualdaggers");
			yield return (NpcWeapon.Dagger, 0, "combo_npc_generic_dagger");
			yield return (NpcWeapon.Polearm, 0, "combo_npc_generic_polearm");
		}

		// A main-hand/off-hand loadout as the equipment rows the runtime actually reads. Category 0 = empty slot.
		private static List<INpcEquipment> Equip(uint main, uint off)
		{
			var rows = new List<INpcEquipment>();
			if (main != 0)
				rows.Add(new NpcEquipment { Pos = NpcWeapon.MainHandPos, Item = new Item { SearchCategory = main } });
			if (off != 0)
				rows.Add(new NpcEquipment { Pos = NpcWeapon.OffHandPos, Item = new Item { SearchCategory = off } });

			return rows;
		}

		private static ComboService Service()
		{
			return new ComboService(new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase))), NullLogger.Instance);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
