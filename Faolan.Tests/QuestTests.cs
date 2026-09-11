using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.GameServer.Inventory;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Sdk;

namespace Faolan.Tests
{
	// Quest state packets (docs/re/quests-and-combat.md), byte-verified against the captured Casilda quest
	// (0x34A7 = 13479, player 0x01316E71).
	public class QuestTests
	{
		// The two committed slices these gates are pinned on. Both carry a QuestFullUpdate for quest 13479 - one from
		// the moment it is offered, one from a later session's journal - so which fixture a builder is compared
		// against is part of the assertion, not an accident of search order.
		private const string OfferFixture = "casilda-quest-offer.bin";
		private const string JournalFixture = "casilda-quest-packets.bin";

		[Fact]
		public void AllGoalsComplete_EmptyIsComplete_ZeroRequiredClampsToOne()
		{
			Assert.True(QuestProgress.AllGoalsComplete([], _ => 0)); // no goals -> complete
			var goals = new[] { GoalReq(100, 0) };
			Assert.False(QuestProgress.AllGoalsComplete(goals, _ => 0)); // RequiredCount 0 treated as 1
			Assert.True(QuestProgress.AllGoalsComplete(goals, _ => 1));
		}

		[Fact]
		public void AllGoalsComplete_RequiresEveryGoalAtRequired()
		{
			var goals = new[] { GoalReq(100, 1), GoalReq(200, 3) };
			var progress = new Dictionary<uint, uint>
			{
				[100] = 1,
				[200] = 2
			};
			Assert.False(QuestProgress.AllGoalsComplete(goals, g => progress[g.GoalId])); // goal 200 still 2/3
			progress[200] = 3;
			Assert.True(QuestProgress.AllGoalsComplete(goals, g => progress[g.GoalId])); // now every goal met
		}

		// A partial goal must not gate its quest. Such a row is recovered from a full-update's secondary objective
		// record, so it has no verb, target or count - nothing can ever advance it - and GoalType defaults to Kill(0)
		// with RequiredCount 0, which clamps to 1. Counting it would leave quests 12369 and 10287 permanently
		// incompletable the moment their real second goals were harvested.
		[Fact]
		public void AllGoalsComplete_IgnoresPartialGoals()
		{
			var real = GoalReq(100, 1);
			var partial = GoalReq(7134, 0);
			partial.Partial = true;

			Assert.True(QuestProgress.AllGoalsComplete([real, partial], g => g.GoalId == 100 ? 1u : 0u));

			// Same shape without the flag is exactly the trap: the quest can never complete.
			partial.Partial = false;
			Assert.False(QuestProgress.AllGoalsComplete([real, partial], g => g.GoalId == 100 ? 1u : 0u));
		}

		[Fact]
		public void BuildQuestFullUpdate_MatchesCasildaCapture()
		{
			// The fresh Casilda offer (player 0x0131720B), from Assets/casilda-quest-offer.bin.
			// Spelled out field by field rather than via QuestFullUpdates.ForQuest, so the golden pins the layout (a
			// QuestcRecord carrying one QuestObjectiveGoal) and not just the factory agreeing with itself.
			var goal = new QuestObjectiveGoal
			{
				K = 0x0C, Instance = 0x0131720B, QuestId = 0x34A7, GoalId = 0x1670, N = 1, GoalIdRepeat = 0x1670, RequiredCount = 1,
				// The goal's (70099, 4CC-tag) reserved pair: 0x000111D3 = 70099, 0x54375554 = "T7UT".
				Reserved = [0, 0, 0, 0, 0, 0x000111D3, 0x54375554],
				MapId = 4010, MarkerX = 0x147, MarkerY = 0x2A6,
				Objective = QuestTextTokens.RemoteFormat(5744, 50304, "M8Zw&quot;&quot;xo*V@DWHQ&amp;oPn1"),
				TextRecord = QuestTextTokens.Localized(11561, 51000, "m", "I!,pP8[uf?E5+a&#36;1uQ6O")
			};

			var record = new QuestcRecord
			{
				QuestId = 0x34A7, Level = 3, BandLevel = 3, QuestLineFlags = QuestFullUpdates.LiveQuestLineFlags,
				Title = QuestTextTokens.RemoteFormat(13479, 50301, "@MSeS6Le,s-7YMf0t1R6"),
				Summary = QuestTextTokens.RemoteFormat(13479, 50302, "iHLlH81PhqeCC&#36;cD0VwJ"),
				Area = QuestTextTokens.RemoteFormat(1054, 50300, "HD*04&#36;OI3kFza?c54,PZ"),
				Region1 = QuestTextTokens.RemoteFormat(4, 52000, "q7i^yK%uD&quot;FmM`F&amp;2iwr"),
				Region2 = QuestTextTokens.RemoteFormat(4010, 52000, "4r7Ld6vDEY4cdU5[&#39;v3/"),
				FieldEc = 1, QuestLineId = 1054, Field2c = 0x34A7, XpReward = 0x50, FieldF0 = 0x64,
				Objectives = new QuestObjectiveRegion
				{
					Goals = [goal]
				}
			};

			var body = QuestFullUpdateCodec.Build(new QuestFullUpdateIIR(0x0131720B, record));
			Assert.Equal(CaptureBody(OfferFixture, IirTypes.QuestFullUpdateIIR), Convert.ToHexString(body).ToLowerInvariant());

			// ...and the strict Parse reads every byte of that captured body back (EnsureAtEnd).
			Assert.Equal(body, QuestFullUpdateCodec.Build(QuestFullUpdateCodec.Parse(body)));
		}

		// All three update spellings, byte-exact against retail. The Satisfied case is the original Casilda fixture; the
		// other two are quest 12371 goal 3495 ("Get 3 stone bricks") from
		// xxx_mage-necromancer_login_play2_tortage_city_first-quests - the multi-count collect goal that is the only
		// thing in the corpus which sends anything but (1,0,0). Together they cover 66/66 captured QuestGoalIIR packets.
		[Theory]
		[InlineData(QuestGoalUpdate.Satisfied, 0x01316E71u, 0x34A7u, 0x1670u,
			"0774c8c10000c35001316e7101000034a700001670000000010000000000000000")]
		[InlineData(QuestGoalUpdate.Satisfied, 0x01317CF8u, 0x3053u, 0x0DA7u,
			"0774c8c10000c35001317cf8010000305300000da7000000010000000000000000")]
		[InlineData(QuestGoalUpdate.Progress, 0x01317CF8u, 0x3053u, 0x0DA7u,
			"0774c8c10000c35001317cf8010000305300000da7000000020000000000000001")]
		[InlineData(QuestGoalUpdate.Advanced, 0x01317CF8u, 0x3053u, 0x0DA7u,
			"0774c8c10000c35001317cf8010000305300000da7000000030000000200000000")]
		public void BuildQuestGoal_MatchesCapture(QuestGoalUpdate update, uint player, uint quest, uint goal, string expected)
		{
			var body = QuestGoalCodec.Build(player, quest, goal, update);
			Assert.Equal(expected, Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildQuestIir_MatchesCapture()
		{
			var body = QuestCodec.Build(0x01316E71, 0x34A7, QuestCodec.StateActive);
			Assert.Equal("97e023140000c35001316e71000000000100000002000034a700000000", Convert.ToHexString(body).ToLowerInvariant());
		}

		// Journal content packets (tools/capture/decode_quest.py). The Casilda quest 0x34A7 = 13479,
		// goal 0x1670 = 5744, area 1054, map 4010. Keys are the deterministic content hashes harvested
		// from the captures (identical across characters/sessions).
		[Fact]
		public void BuildQuestJournal_MatchesCasildaCapture()
		{
			// A single-quest journal for 13479, from Assets/casilda-quest-packets.bin. Area 1054's ACGHash is "RRL3"
			// (0x52524C33) - a per-area token, not a record-list magic.
			var journal = new QuestJournalIIR(0x01316D8A,
				new QuestJournalArea(1054,
					QuestTextTokens.RemoteFormat(1054, 50300, "HD*04&#36;OI3kFza?c54,PZ"),
					QuestTextTokens.RemoteFormat(4010, 52000, "4r7Ld6vDEY4cdU5[&#39;v3/"),
					QuestTextTokens.RemoteFormat(4, 52000, "q7i^yK%uD&quot;FmM`F&amp;2iwr"),
					[
						new QuestJournalEntry(0x34A7, QuestTextTokens.RemoteFormat(13479, 50301, "@MSeS6Le,s-7YMf0t1R6"),
							QuestTextTokens.RemoteFormat(13479, 50303, "i&#36;g&#92;DTrqI&#92;nBI0lUo6&amp;7"))
					])
				{
					AcgHash = 0x52524C33
				},
				0,
				[],
				QuestJournalCurrent.None);

			var body = QuestJournalCodec.Build(journal);
			Assert.Equal(CaptureBody(JournalFixture, IirTypes.QuestJournalIIR), Convert.ToHexString(body).ToLowerInvariant());
			Assert.Equal(body, QuestJournalCodec.Build(QuestJournalCodec.Parse(body)));
		}

		// The rich creation step (mode 7) - "NEW_character_creation Slave Galley": an f4 <remoteformat> token, an f5
		// resource-preload list, and a non-zero f6 pair. Golden vs the captured 780 B step, proving BuildStep covers
		// the full grammar. The big token/resource strings are the captured creation-step content (their live
		// authoring rides the create-burst construction); here they are the test oracle.
		[Fact]
		public void BuildScryCreationStep_MatchesCapture()
		{
			const string token =
				"<remoteformat token=\"playorskip(72)|onclientconnected(1)|NEW_character_creation Slave Galley (server-side)\" category=\"50023\" key=\"&quot;B]SZ.AGRGrAyOGe*BG-\" knubot=\"0\"  ></remoteformat>";
			const string resources =
				"1010004:793713;1010004:793714;1070003:4000;1000097:4000;1010030:156211;1010030:239448;1010030:565913;1010004:3728533;1060668:4000;1000059:4000;1060667:167368;1010004:3075445;1010004:3180025;1010004:221285;1010004:3663190;1010004:208923;1010004:208266;1010004:226891;1010004:3663191;1010004:3656109;1010030:210069;1010001:210070;1010004:3075452;1010004:3075450;1010004:3075451;1010300:4000;1000049:4000;1066611:4000;1000013:40000000;1000015:40000000;1010506:4000;1000058:4000;1010207:110006;1010207:262241;1010207:568806;1010207:3420331;1010207:167183";

			var built = ScryScriptCodec.Build(0x01316D8A, token, 7, 0, resources, 2699176, 4477056);

			Assert.Equal(
				"a36d3b740000c35001316d8a00000002f708051007180022b9013c72656d6f7465666f726d617420746f6b656e3d22706c61796f72736b6970283732297c6f6e636c69656e74636f6e6e65637465642831297c4e45575f6368617261637465725f6372656174696f6e20536c6176652047616c6c657920287365727665722d7369646529222063617465676f72793d22353030323322206b65793d222671756f743b425d535a2e414752477241794f47652a42472d22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e2aa604313031303030343a3739333731333b313031303030343a3739333731343b313037303030333a343030303b313030303039373a343030303b313031303033303a3135363231313b313031303033303a3233393434383b313031303033303a3536353931333b313031303030343a333732383533333b313036303636383a343030303b313030303035393a343030303b313036303636373a3136373336383b313031303030343a333037353434353b313031303030343a333138303032353b313031303030343a3232313238353b313031303030343a333636333139303b313031303030343a3230383932333b313031303030343a3230383236363b313031303030343a3232363839313b313031303030343a333636333139313b313031303030343a333635363130393b313031303033303a3231303036393b313031303030313a3231303037303b313031303030343a333037353435323b313031303030343a333037353435303b313031303030343a333037353435313b313031303330303a343030303b313030303034393a343030303b313036363631313a343030303b313030303031333a34303030303030303b313030303031353a34303030303030303b313031303530363a343030303b313030303035383a343030303b313031303230373a3131303030363b313031303230373a3236323234313b313031303230373a3536383830363b313031303230373a333432303333313b313031303230373a313637313833320a08a8dfa4011080a19102",
				Convert.ToHexString(built).ToLowerInvariant());
		}

		// ScryScript (0xA36D3B74) destiny/tutorial step driver, built from scratch. Golden vs the captured steps:
		// "IntroDone" (mode 2, flag 0) and "ChangeSex" (flag 6).
		[Fact]
		public void BuildScryStep_MatchesCapture()
		{
			Assert.Equal("a36d3b740000c35001316d8a00000000190805100218002209496e74726f446f6e652a00320408001000", Convert.ToHexString(ScryScriptCodec.Build(0x01316D8A, "IntroDone")).ToLowerInvariant());
			Assert.Equal("a36d3b740000c35001316d8a000000001908051002180622094368616e67655365782a00320408001000",
				Convert.ToHexString(ScryScriptCodec.Build(0x01316D8A, "ChangeSex", flag: 6)).ToLowerInvariant());

			// The PostReadyBurst login destiny steps (records 2 + 4), proving BuildStep reproduces them so the
			// verbatim byte arrays there can be retired (construct, don't replay).
			Assert.Equal("a36d3b740000c35001316d8a000000001c080510021800220c5374617274466978656433642a00320408001000",
				Convert.ToHexString(ScryScriptCodec.Build(0x01316D8A, "StartFixed3d")).ToLowerInvariant());
			Assert.Equal("a36d3b740000c35001316d8a000000001f080510021800220f57656c636f6d655475746f7269616c2a00320408001000",
				Convert.ToHexString(ScryScriptCodec.Build(0x01316D8A, "WelcomeTutorial")).ToLowerInvariant());
		}

		[Fact]
		public void MatchingCollectGoal_ByItemId_CollectTypeOnly()
		{
			// Shipwrecked V (12371): goal 3495 = CollectItem brick 193400; goal 3497 = deliver (TalkToNpc Turach) also
			// carries item 193400 in TrailingRefId, but the collect matcher must ignore it (wrong type).
			var goals = new[]
			{
				new QuestGoalDefinition
				{
					Sequence = 1,
					GoalId = 3495,
					GoalType = QuestGoalType.CollectItem,
					TrailingRefId = 193400
				},
				new QuestGoalDefinition
				{
					Sequence = 2,
					GoalId = 3497,
					GoalType = QuestGoalType.TalkToNpc,
					TargetName = "Turach",
					TrailingRefId = 193400
				}
			};
			Assert.Equal(3495u, QuestProgress.MatchingCollectGoal(goals, 193400)?.GoalId); // CollectItem by item id
			Assert.Null(QuestProgress.MatchingCollectGoal(goals, 999)); // no collect goal for that item
			Assert.Null(QuestProgress.MatchingCollectGoal(goals, 0)); // no item -> no match (never the TalkToNpc goal)
		}

		// A `Use` objective is solved by using the item, not by holding it - and the two matchers must not see each
		// other's goals. The shipped case is the one that makes the separation load-bearing: the vial is handed to the
		// player by the quest giver, so a collect matcher firing on it would complete the objective the moment it
		// arrived.
		[Fact]
		public void MatchingUseGoal_ByItemId_UseTypeOnly()
		{
			var goals = new[]
			{
				new QuestGoalDefinition
				{
					Sequence = 1,
					GoalId = 11252,
					GoalType = QuestGoalType.UseItem,
					TrailingRefId = 4349109 // Po-Sha Potency Vial
				},
				new QuestGoalDefinition
				{
					Sequence = 2,
					GoalId = 3495,
					GoalType = QuestGoalType.CollectItem,
					TrailingRefId = 193400
				}
			};

			Assert.Equal(11252u, QuestProgress.MatchingUseGoal(goals, 4349109)?.GoalId);
			Assert.Null(QuestProgress.MatchingUseGoal(goals, 193400)); // the collect goal's item is not a use goal
			Assert.Null(QuestProgress.MatchingUseGoal(goals, 0));
			Assert.Null(QuestProgress.MatchingCollectGoal(goals, 4349109)); // ...and the reverse
		}

		// The shipped row itself, so the typing rule cannot silently stop firing: `Use the Vial to Capture Gas`
		// (quest 17234) must stay typed UseItem against the item the client's own reference names.
		[Fact]
		public async Task ShippedUseGoal_IsTypedAgainstAnItemWeHold()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var goal = await db.QuestGoalDefinitions.FirstOrDefaultAsync(g => g.GoalId == 11252, ct);
			Assert.NotNull(goal);
			Assert.Equal(QuestGoalType.UseItem, goal!.GoalType);
			Assert.Equal(4349109u, goal.TrailingRefId);

			// The guard the typing rule applies: a use goal pointing at an item nobody can obtain is a silently
			// uncompletable objective.
			Assert.True(await db.Items.AnyAsync(i => i.Id == goal.TrailingRefId, ct),
				"the Use goal's item left the catalog - the objective is now uncompletable rather than inert");
		}
		// "Unlock the Acheronian gate" is solved by using the gate, not by standing next to it. The matcher must be
		// blind to every other goal type, and in particular a reach goal at the same spot must not answer for it.
		[Fact]
		public void MatchingUseObjectGoal_ByWorldObjectId_UseWorldObjectTypeOnly()
		{
			var goals = new[]
			{
				new QuestGoalDefinition
				{
					Sequence = 1,
					GoalId = 3492,
					GoalType = QuestGoalType.UseWorldObject,
					TargetWorldObjectId = 975369919, // the Acheronian Gate
					QuestItemId = 193186 // ...and the key that opens it
				},
				new QuestGoalDefinition
				{
					Sequence = 2,
					GoalId = 3498,
					GoalType = QuestGoalType.ReachArea
				}
			};

			Assert.Equal(3492u, QuestProgress.MatchingUseObjectGoal(goals, 975369919)?.GoalId);
			Assert.Null(QuestProgress.MatchingUseObjectGoal(goals, 244779243)); // a different object - the Grotto
			Assert.Null(QuestProgress.MatchingUseObjectGoal(goals, 0)); // no object -> no match, never the reach goal

			// The key half. It is a prerequisite, not a hand-over: nothing takes it, which is what separates this from
			// DeliveryItemId beside it.
			Assert.Equal(193186u, QuestProgress.RequiredHeldItemId(goals[0]));
			Assert.Equal(0u, QuestProgress.RequiredHeldItemId(goals[1]));
			Assert.False(QuestProgress.IsDelivery(goals[0]), "an unlock is not a hand-in - the key must not be consumed");
		}

		// The shipped row, so the proximity bug cannot come back by a re-seed: goal 3492 must stay a use of the gate
		// object, holding the key. Typed ReachArea it completes on arrival - the gate object stands on the goal's
		// marker - before the key is looted and without the gate being touched.
		[Fact]
		public async Task ShippedAcheronianGateGoal_IsAUseOfTheGate_NotAReach()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var goal = await db.QuestGoalDefinitions.FirstOrDefaultAsync(g => g.QuestDefinitionId == 12369 && g.GoalId == 3492, ct);
			Assert.NotNull(goal);
			Assert.Equal(QuestGoalType.UseWorldObject, goal!.GoalType);
			Assert.Equal(193186u, goal.QuestItemId);

			// The same guard the Use-item gate applies: a goal pointing at an object or an item we do not hold is a
			// silently uncompletable objective rather than an inert one.
			Assert.NotNull(goal.TargetWorldObjectId);
			Assert.True(await db.WorldObjects.AnyAsync(o => o.Id == goal.TargetWorldObjectId, ct),
				"the unlock goal's world object left the world - the objective is now uncompletable");
			Assert.True(await db.Items.AnyAsync(i => i.Id == goal.QuestItemId, ct),
				"the gate key left the catalog - the unlock can never be satisfied");
		}

		[Fact]
		public void CanHandIn_DeliveryNeedsTheItemInHand_OthersUnaffected()
		{
			// Shipwrecked V's hand-in (3497): kind tag Deliver (20), trailing ref = item 193400 in LDB cat 50200,
			// RequiredCount 3 - "Deliver 3 stone bricks to Turach".
			var deliver = new QuestGoalDefinition
			{
				GoalId = 3497,
				GoalType = QuestGoalType.TalkToNpc,
				TargetName = "Turach",
				Separator = (uint)QuestGoalKind.Deliver,
				TrailingCategory = QuestProgress.ItemNameCategory,
				TrailingRefId = 193400,
				RequiredCount = 3
			};

			Assert.True(QuestProgress.IsDelivery(deliver));
			Assert.False(QuestProgress.CanHandIn(deliver, 0)); // empty-handed - the whole point of the check
			Assert.False(QuestProgress.CanHandIn(deliver, 2)); // partial: 3 bricks means 3
			Assert.True(QuestProgress.CanHandIn(deliver, 3));
			Assert.True(QuestProgress.CanHandIn(deliver, 4)); // extras are fine

			// An ordinary talk goal is not a delivery (no kind tag, no item ref) and must not be gated - Casilda would
			// otherwise become un-freeable.
			var talk = GoalSeq(1, 13480, QuestGoalType.TalkToNpc, "Casilda");
			Assert.False(QuestProgress.IsDelivery(talk));
			Assert.True(QuestProgress.CanHandIn(talk, 0));

			// Nor is a collect goal, even though it carries the very same item reference - only the kind tag separates
			// them, which is what makes the tag load-bearing here.
			var collect = new QuestGoalDefinition
			{
				GoalId = 3495,
				GoalType = QuestGoalType.CollectItem,
				Separator = (uint)QuestGoalKind.Collect,
				TrailingCategory = QuestProgress.ItemNameCategory,
				TrailingRefId = 193400,
				RequiredCount = 3
			};
			Assert.False(QuestProgress.IsDelivery(collect));
			Assert.True(QuestProgress.CanHandIn(collect, 0));

			// A Deliver-kind goal with no item reference cannot be gated on an item; it must stay playable.
			var untyped = new QuestGoalDefinition { GoalId = 1, Separator = (uint)QuestGoalKind.Deliver };
			Assert.False(QuestProgress.IsDelivery(untyped));
			Assert.True(QuestProgress.CanHandIn(untyped, 0));

			// RequiredCount 0 behaves as 1, like every other count rule.
			var single = new QuestGoalDefinition
			{
				GoalId = 5792,
				Separator = (uint)QuestGoalKind.Deliver,
				TrailingCategory = QuestProgress.ItemNameCategory,
				TrailingRefId = 110891,
				RequiredCount = 0
			};
			Assert.False(QuestProgress.CanHandIn(single, 0));
			Assert.True(QuestProgress.CanHandIn(single, 1));
		}

		// The second delivery shape: a hand-in whose evidence is the client's plaintext objective sentence rather than
		// a captured wire record. 7,147 goals come from the quest catalog, which carries no kind tag and no trailing
		// reference at all, so a hand-in there is one whose item was resolved from its own words - "Give the Crate of
		// Steel to Turach" -> Items 31055 - and written to the server-side `QuestItemId`.
		//
		// It has to be `TalkToNpc` + `QuestItemId`, never `QuestItemId` alone: a `LootFromNpc` goal names its item in
		// the same column (what its target's corpse bag must hold), and gating a loot goal on carrying the item first
		// would make it uncompletable - you loot it to get it.
		[Fact]
		public void IsDelivery_AlsoReadsAResolvedQuestItem_ButNeverOnALootGoal()
		{
			var resolved = new QuestGoalDefinition
			{
				GoalId = 1371, GoalType = QuestGoalType.TalkToNpc, TargetName = "Turach",
				QuestItemId = 31055, RequiredCount = 1 // "Give the Crate of Steel to Turach"
			};

			Assert.True(QuestProgress.IsDelivery(resolved));
			Assert.Equal(31055u, QuestProgress.DeliveryItemId(resolved));
			Assert.False(QuestProgress.CanHandIn(resolved, 0));
			Assert.True(QuestProgress.CanHandIn(resolved, 1));

			// A loot goal carries the same column and must not become a delivery - you loot the key to get it.
			var loot = new QuestGoalDefinition
			{
				GoalId = 5867, GoalType = QuestGoalType.LootFromNpc, TargetName = "Scavenger Tuut", QuestItemId = 319224
			};
			Assert.False(QuestProgress.IsDelivery(loot));
			Assert.Equal(0u, QuestProgress.DeliveryItemId(loot));
			Assert.True(QuestProgress.CanHandIn(loot, 0));

			// An ordinary talk goal with no item is unaffected.
			Assert.False(QuestProgress.IsDelivery(GoalSeq(1, 13480, QuestGoalType.TalkToNpc, "Casilda")));

			// The captured shape still resolves its item from the wire reference, and a goal carrying both prefers the
			// resolved one - someone confirmed that id against the catalog.
			var captured = new QuestGoalDefinition
			{
				GoalId = 3497, GoalType = QuestGoalType.TalkToNpc, Separator = (uint)QuestGoalKind.Deliver,
				TrailingCategory = QuestProgress.ItemNameCategory, TrailingRefId = 193400
			};
			Assert.Equal(193400u, QuestProgress.DeliveryItemId(captured));
			captured.QuestItemId = 31055;
			Assert.Equal(31055u, QuestProgress.DeliveryItemId(captured));
		}

		// The hand-in takes the goods, and the two ways that goes wrong are both invisible from a single full stack:
		// over-taking a larger stack, and missing a stack split across cells or containers.
		[Fact]
		public void InventoryTake_TakesExactlyWhatIsAskedFor_AcrossSplitStacksAndContainers()
		{
			// "Deliver 3 stone bricks to Turach" against five bricks in one quest-container cell: 3 come out, 2 stay.
			// Clearing the cell would destroy items the player legitimately keeps.
			var oneBigStack = new List<ItemInstance>
			{
				Held(ContainerKind.Quest, 0, 193400, 5)
			};
			var plan = InventoryTake.Plan(oneBigStack, 193400, 3);
			var step = Assert.Single(plan);
			Assert.Equal((3u, 2u), (step.Taken, step.Remaining)); // decremented, not cleared
			Assert.Equal(3u, InventoryTake.Total(plan));

			// Split across two containers and three cells, with an unrelated item in between: the walk spans them and
			// stops at the count - the last brick-holding cell is untouched.
			var split = new List<ItemInstance>
			{
				Held(ContainerKind.Bag, 4, 999999),
				Held(ContainerKind.Quest, 2, 193400),
				Held(ContainerKind.Bag, 1, 193400),
				Held(ContainerKind.Bag, 7, 193400)
			};
			plan = InventoryTake.Plan(split, 193400, 3);
			Assert.Equal(3, plan.Count);
			Assert.Equal(3u, InventoryTake.Total(plan));
			Assert.All(plan, s => Assert.Equal(1u, s.Taken));
			Assert.DoesNotContain(plan, s => s is { Container: ContainerKind.Bag, Slot: 4 }); // never the other item

			plan = InventoryTake.Plan(split, 193400, 2);
			Assert.Equal(2u, InventoryTake.Total(plan));
			Assert.Equal(2, plan.Count); // stopped early - the third brick stays

			// Not holding enough is reported as a short plan, not an exception and not an over-take. The caller has
			// already gated on CanHandIn, so this is the "inventory moved underneath us" case.
			plan = InventoryTake.Plan(oneBigStack, 193400, 9);
			Assert.Equal(5u, InventoryTake.Total(plan));

			// Degenerate inputs take nothing rather than everything.
			Assert.Empty(InventoryTake.Plan(split, 193400, 0));
			Assert.Empty(InventoryTake.Plan(split, 0, 3));
			Assert.Empty(InventoryTake.Plan(split, 12345, 3));

			static ItemInstance Held(ContainerKind container, int slot, uint templateId, uint count = 1) =>
				new ItemInstance
			{
				Container = container,
				Slot = slot,
				TemplateId = templateId,
				Count = count
			};
		}

		[Fact]
		public void PreconditionMet_LevelChecksNow_RankIsUnmetUntilFactionsExist()
		{
			var none = GoalSeq(1, 100, QuestGoalType.TalkToNpc, "Casilda");
			Assert.True(QuestProgress.PreconditionMet(none, 1)); // no precondition -> a no-op for almost every goal

			// "Talk to Sharak (level 30)" - checkable today against the character's level.
			var level = new QuestGoalDefinition { GoalId = 5374, TargetName = "Sharak", RequiredLevel = 30 };
			Assert.False(QuestProgress.PreconditionMet(level, 29));
			Assert.True(QuestProgress.PreconditionMet(level, 30));
			Assert.True(QuestProgress.PreconditionMet(level, 80));

			// "...to Itsing When You Have Become an Acolyte (Rank 1)" - needs the faction system, which does not exist yet.
			var rank = new QuestGoalDefinition { GoalId = 11880, TargetName = "Itsing", RequiredRankTitle = "Acolyte", RequiredRank = 1 };

			// Unknown is not met. This is the safety property the whole design rests on: with no resolver the goal stays
			// inert, rather than completing early the first time the player talks to Itsing.
			Assert.False(QuestProgress.PreconditionMet(rank, 80));
			Assert.False(QuestProgress.PreconditionMet(rank, 80, _ => null));

			// ...and it starts working the day a resolver can answer - no re-harvest, no re-wiring.
			Assert.False(QuestProgress.PreconditionMet(rank, 80, t => t == "Acolyte" ? 0u : null));
			Assert.True(QuestProgress.PreconditionMet(rank, 80, t => t == "Acolyte" ? 1u : null));
			Assert.True(QuestProgress.PreconditionMet(rank, 80, t => t == "Acolyte" ? 5u : null));
			Assert.False(QuestProgress.PreconditionMet(rank, 80, t => t == "Thug" ? 5u : null)); // right rank, wrong faction

			// Both kinds on one goal: each must hold independently.
			var both = new QuestGoalDefinition { GoalId = 1, RequiredLevel = 30, RequiredRankTitle = "Thug", RequiredRank = 1 };
			Assert.False(QuestProgress.PreconditionMet(both, 29, _ => 5u)); // rank ok, level short
			Assert.True(QuestProgress.PreconditionMet(both, 30, _ => 5u));
		}

		// A reach goal's whole target is its pin, so a placeholder marker makes it solvable by standing in the corner
		// of the map. The tell that (50,50) and (1,1) are placeholders rather than places is that the same coordinate
		// spans different playfields - nine of them for (50,50), three for (1,1) - while every other repeated marker
		// in the shipped data sits on one map.
		[Fact]
		public void HasReachablePin_RejectsThePlaceholderMarkers()
		{
			Assert.True(QuestProgress.HasReachablePin(Pin(4010, 416, 701))); // "Enter the jungle" - a real pin
			Assert.True(QuestProgress.HasReachablePin(Pin(2000, 826, 533)));

			// The two shipped goals that would be typed ReachArea off one of these, and are not reaches in the first
			// place: 11446 "Win the Battle of Blood Ravine" and 11951 "Extinguish the Pods".
			Assert.False(QuestProgress.HasReachablePin(Pin(5201, 1, 1)));
			Assert.False(QuestProgress.HasReachablePin(Pin(6212, 50, 50)));

			// A placeholder is only a placeholder as a pair - 50 on one axis with a real value on the other is a place.
			Assert.True(QuestProgress.HasReachablePin(Pin(6212, 50, 700)));
			Assert.True(QuestProgress.HasReachablePin(Pin(6212, 700, 50)));

			// The markerless cases this subsumes.
			Assert.False(QuestProgress.HasReachablePin(new QuestGoalDefinition { GoalId = 1 }));
			Assert.False(QuestProgress.HasReachablePin(Pin(0, 416, 701)));
			Assert.False(QuestProgress.HasReachablePin(new QuestGoalDefinition { GoalId = 1, MapId = 4010 }));
		}

		// ...and no shipped goal is typed ReachArea on one - the gate is in the importer's typing pass and in the
		// runtime trigger, so a row that slips past one is caught by the other.
		[Fact]
		public async Task NoShippedReachGoal_SitsOnAPlaceholderMarker()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var offenders = await db.QuestGoalDefinitions
				.AsNoTracking()
				.Where(g => g.GoalType == QuestGoalType.ReachArea && g.RegionId == null)
				.Where(g => (g.MarkerX == 50 && g.MarkerY == 50) || (g.MarkerX == 1 && g.MarkerY == 1))
				.Select(g => g.GoalId + " " + g.Name)
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.True(offenders.Count == 0, "a ReachArea goal on a placeholder marker is solved by standing in the corner of the map:\n  " + string.Join("\n  ", offenders));
		}

		private static QuestGoalDefinition Pin(uint mapId, uint x, uint y)
		{
			return new QuestGoalDefinition
			{
				GoalId = 1,
				MapId = mapId == 0 ? null : mapId,
				MarkerX = x,
				MarkerY = y
			};
		}

		// --- Quest objective-tracking rules ---------------------------------------------------------------------------

		[Fact]
		public void MatchingGoal_PicksByTypeAndTarget_NotAlwaysFirst()
		{
			// A two-goal quest: the trigger fires the goal it matches, not goal[0].
			var goals = new[] { GoalSeq(1, 100, QuestGoalType.Kill, "Wolf"), GoalSeq(2, 200, QuestGoalType.TalkToNpc, "Casilda") };
			Assert.Equal(200u, QuestProgress.MatchingGoal(goals, QuestGoalType.TalkToNpc, "casilda")?.GoalId); // second goal, case-insensitive
			Assert.Equal(100u, QuestProgress.MatchingGoal(goals, QuestGoalType.Kill, "Wolf")?.GoalId);
			Assert.Null(QuestProgress.MatchingGoal(goals, QuestGoalType.LootFromNpc, "Wolf")); // right target, wrong type
			Assert.Null(QuestProgress.MatchingGoal(goals, QuestGoalType.Kill, "")); // empty target
			Assert.Null(QuestProgress.MatchingGoal(goals, QuestGoalType.TalkToNpc, "Nobody")); // no such target
		}

		[Fact]
		public void MatchingLootGoal_NeedsTheItemTheGoalNames_NotJustTheCorpse()
		{
			// Scavenger Tuut: the objective is holding the Scavenger's Key (319224), so taking his other drops off the
			// same corpse must not solve it. Matching on the source NPC alone does exactly that - and also solves the
			// quest on emptying a bag that never held it.
			var goals = new[]
			{
				new QuestGoalDefinition
				{
					Sequence = 1,
					GoalId = 3500,
					GoalType = QuestGoalType.LootFromNpc,
					TargetName = "Scavenger Tuut",
					QuestItemId = 319224
				}
			};

			Assert.Equal(3500u, QuestProgress.MatchingLootGoal(goals, "scavenger tuut", 319224)?.GoalId); // the quest item, case-insensitive
			Assert.Null(QuestProgress.MatchingLootGoal(goals, "Scavenger Tuut", 42)); // his corpse, some other item
			Assert.Null(QuestProgress.MatchingLootGoal(goals, "Scavenger Tuut", 0)); // nothing received (an empty bag)
			Assert.Null(QuestProgress.MatchingLootGoal(goals, "Scavenger Lackey", 319224)); // the quest item, wrong corpse
		}

		[Fact]
		public void MatchingLootGoal_WithoutAQuestItem_MatchesNothing()
		{
			// A goal seeded from a capture that never showed the item has nothing to gate on, and the objective is
			// receiving an item - so it matches nothing rather than accepting any item off that corpse. Both goals in
			// the game data name theirs; one that does not is a data gap, and the loot path logs it as an error.
			var goals = new[] { GoalSeq(1, 3600, QuestGoalType.LootFromNpc, "Rock Pile") };

			Assert.Null(QuestProgress.MatchingLootGoal(goals, "Rock Pile", 12345));
			Assert.Null(QuestProgress.MatchingLootGoal(goals, "Rock Pile", 0));
			Assert.Null(QuestProgress.MatchingLootGoal(goals, "", 12345)); // no source name -> no match
		}

		[Fact]
		public void MatchingKillGoal_PrefersTemplateId_FallsBackToName()
		{
			// Shipwrecked 10238: goal 3490 = kill Saddur (TrailingRefId 9500 = his template); goal 1448 =
			// ReachArea "enter jungle", which the kill matcher must never return.
			var goals = new[]
			{
				new QuestGoalDefinition
				{
					Sequence = 1,
					GoalId = 1448,
					GoalType = QuestGoalType.ReachArea,
					TrailingRefId = 180
				},
				new QuestGoalDefinition
				{
					Sequence = 2,
					GoalId = 3490,
					GoalType = QuestGoalType.Kill,
					TargetName = "Saddur",
					TrailingRefId = 9500
				}
			};
			Assert.Equal(3490u, QuestProgress.MatchingKillGoal(goals, 9500, "Saddur")?.GoalId); // by the stable template id
			Assert.Equal(3490u, QuestProgress.MatchingKillGoal(goals, 9500, null!)?.GoalId); // id alone, no name needed
			Assert.Null(QuestProgress.MatchingKillGoal(goals, 12141, "Some Lackey")); // wrong id + unknown name -> no match

			// Fallback: a goal seeded by name only (no TrailingRefId - e.g. a "kill N <group>" whose ref is a group id).
			var byName = new[]
			{
				new QuestGoalDefinition
				{
					Sequence = 1,
					GoalId = 700,
					GoalType = QuestGoalType.Kill,
					TargetName = "Scavenger Lackey"
				}
			};
			Assert.Equal(700u, QuestProgress.MatchingKillGoal(byName, 12141, "scavenger lackey")?.GoalId); // id misses -> name, case-insensitive
			Assert.Equal(700u, QuestProgress.MatchingKillGoal(byName, null, "Scavenger Lackey")?.GoalId); // no id -> name
			Assert.Null(QuestProgress.MatchingKillGoal(byName, null, "Nobody")); // no id, wrong name
		}

		// A template id of zero means "no type id known", not "the goal whose TrailingRefId is 0". Zero is reachable:
		// the one caller passes `WorldEntity.NpcTemplateId`, a non-nullable uint that is 0 for anything with no
		// template behind it, so an unguarded id match advances an arbitrary goal of every quest the player has open.
		//
		// All four sibling matchers guard 0 on entry: a caller with no id must not match a goal by id.
		[Fact]
		public void MatchingKillGoal_TreatsAZeroTemplateIdAsUnknown_NotAsAMatch()
		{
			var untyped = new[]
			{
				new QuestGoalDefinition { Sequence = 1, GoalId = 4242, GoalType = QuestGoalType.Kill, TrailingRefId = 0 },
				new QuestGoalDefinition { Sequence = 2, GoalId = 4243, GoalType = QuestGoalType.Kill, TargetName = "Scavenger Lackey", TrailingRefId = 0 }
			};

			// A 0 falls through to the name branch, exactly as a null does.
			Assert.Null(QuestProgress.MatchingKillGoal(untyped, 0, "Nobody At All"));
			Assert.Null(QuestProgress.MatchingKillGoal(untyped, 0, null!));
			Assert.Equal(4243u, QuestProgress.MatchingKillGoal(untyped, 0, "Scavenger Lackey")?.GoalId);
		}

		// A Kill goal's `TrailingRefId` is only a template id when the goal's own typed reference says so. Six shipped
		// kill goals name a monster category instead - `TrailingCategory` 51100 `categorytree.description`, "Kill
		// Atzel's Watch", "Kill Undeads in Xibaluku" - whose ids (246,944 .. 10,367,287) are a different space
		// entirely. Comparing one to a killed entity's template id is a category error that is safe only until an id
		// collides, which is a matter of NPC coverage rather than design.
		[Fact]
		public void MatchingKillGoal_MatchesByIdOnlyWhenTheReferenceIsAnNpcTemplate()
		{
			var goals = new[]
			{
				// The real shape of goal 6340 "Kill Atzel's Watch": a kill goal whose target is an editor group.
				new QuestGoalDefinition
				{
					Sequence = 1, GoalId = 6340, GoalType = QuestGoalType.Kill,
					TrailingCategory = 51100, TrailingRefId = 10173329
				},
				// ...and goal 3490's, which does name a template.
				new QuestGoalDefinition
				{
					Sequence = 2, GoalId = 3490, GoalType = QuestGoalType.Kill,
					TrailingCategory = QuestProgress.NpcTemplateNameCategory, TrailingRefId = 9500, TargetName = "Saddur"
				}
			};

			// Were an NPC template ever to carry the category's id, the goal must not advance on killing it.
			Assert.Null(QuestProgress.MatchingKillGoal(goals, 10173329, "Atzel Watchman"));
			// The 51000 sibling matches as before...
			Assert.Equal(3490u, QuestProgress.MatchingKillGoal(goals, 9500, null!)?.GoalId);
			// ...and so does a hand-authored goal with no typed reference at all, where TrailingRefId means what the
			// person who set it meant.
			var handAuthored = new[] { new QuestGoalDefinition { Sequence = 1, GoalId = 900, GoalType = QuestGoalType.Kill, TrailingRefId = 9500 } };
			Assert.Equal(900u, QuestProgress.MatchingKillGoal(handAuthored, 9500, null!)?.GoalId);
		}

		// End-to-end: the importer harvests the Casilda definition from the captures, and feeding it back
		// through the builders reproduces the captured packets byte-for-byte (proves QuestDefinitionExtractor's
		// refs + objective marker, and the QuestTextRef -> record mapping).
		[Fact]
		public void QuestDefinitionExtractor_RoundTripsCasilda()
		{
			// The extractor's inputs from one capture, committed whole: QuestDefinitionExtractor takes CapturedPacket
			// and strips the framing itself, so bodies alone would not do, and it dispatches on each packet's own
			// sub-opcode - which CapturedFixture reads back out of the bytes rather than the caller asserting it.
			var packets = QuestPackets("casilda-quest-packets.bin");
			var def = QuestDefinitionExtractor.Extract(packets).Single(d => d.QuestId == 0x34A7);

			Assert.Equal(1054u, def.QuestLineId);
			Assert.Equal(0x1670u, def.GoalId);
			Assert.Equal(4010u, def.MapId);
			Assert.Equal(0x147u, def.MarkerX);
			Assert.Equal(0x2A6u, def.MarkerY);

			// Journal list (no timestamp) - byte-exact against frame 9947.
			var journal = QuestJournalCodec.Build(QuestJournals.ForQuests(0x01316D8A, [def]));
			Assert.Equal(CaptureBody(JournalFixture, IirTypes.QuestJournalIIR), Convert.ToHexString(journal).ToLowerInvariant());

			// Full update - byte-exact against frame 687. The goal's reserved (70099, "T7UT") pair is not passed in: the
			// extractor lifts it off the same body through the codec, so it rides the harvested definition.
			var primary = def.Goals.Single(g => g.GoalId == 0x1670);
			Assert.Equal(70099u, primary.Extra5);
			Assert.Equal(0x54375554u, primary.Extra6); // "T7UT"
			var iir = QuestFullUpdates.ForQuest(0x0131720B, def, 3);
			var full = QuestFullUpdateCodec.Build(iir);
			Assert.Equal(CaptureBody(OfferFixture, IirTypes.QuestFullUpdateIIR), Convert.ToHexString(full).ToLowerInvariant());

			// Quest-log list (the packet that populates the J-panel) - byte-exact against frame 687. The captured
			// character had four active quests (tracked 13479 first); the area-title slot is the 13479 def's QuestLineTitle.
			var logList = MainQuestUpdateCodec.Build(0x0131720B, def.QuestLineId, 0x34A7, [0x34A7, 0x34FB, 0x34A8, 0x3077], QuestTextTokens.Record(def, QuestTextSlot.QuestLineTitle));
			Assert.Equal(CaptureBody(OfferFixture, IirTypes.MainQuestUpdateIIR), Convert.ToHexString(logList).ToLowerInvariant());

			// Inverse: Parse recovers the handle, tracked id, and the exact quest-id list (the CaptureQuestImporter
			// path that harvests each character's held-quest set from the J-panel log).
			var parsedLog = MainQuestUpdateCodec.Parse(logList);
			Assert.Equal(0x0131720Bu, parsedLog.PlayerHandle);
			Assert.Equal(0x34A7u, parsedLog.TrackedQuestId);
			Assert.Equal([0x34A7u, 0x34FBu, 0x34A8u, 0x3077u], parsedLog.Entries.Select(e => e.QuestId));
			Assert.Equal(MainQuestUpdateOp.Add, parsedLog.Operation);

			// The J-panel remove, byte-exact against retail. `--report iir-bodies --report-arg MainQuestUpdate` finds six
			// across five captures, byte-identical but for the handle and the line id, and they are this very record with
			// `Operation = Remove` and nothing in it. The client's reader agrees - `FUN_00918560` has no branch on the
			// operation word at all, so there is only ever one grammar.
			//
			// The two hexes below are the deletes of Hakotep_hox_login_gameplay_untrain-feats-..._delete-quests frames
			// 3938 and 3976, verbatim.
			var removeA = MainQuestUpdateCodec.Build(new MainQuestUpdateIIR(0x1101B4F5, MainQuestUpdateOp.Remove, 0x7C4, 0, [], ""));
			var removeB = MainQuestUpdateCodec.Build(new MainQuestUpdateIIR(0x1101B4F5, MainQuestUpdateOp.Remove, 0x7FB, 0, [], ""));

			Assert.Equal("504b79240000c3501101b4f50100000002000007c4000000000000000000000000000000000000000000000000",
				Convert.ToHexString(removeA).ToLowerInvariant());
			Assert.Equal("504b79240000c3501101b4f50100000002000007fb000000000000000000000000000000000000000000000000",
				Convert.ToHexString(removeB).ToLowerInvariant());
			Assert.Equal(45, removeA.Length);

			// ...and it round-trips, so the empty list is a real parse rather than a length coincidence.
			var parsedRemove = MainQuestUpdateCodec.Parse(removeA);
			Assert.Equal(MainQuestUpdateOp.Remove, parsedRemove.Operation);
			Assert.Equal(0x7C4u, parsedRemove.QuestLineId);
			Assert.Empty(parsedRemove.Entries);
			Assert.Equal(0u, parsedRemove.TrackedQuestId);
			Assert.Equal(removeA, MainQuestUpdateCodec.Build(parsedRemove));

			// Snapshot-tails per-quest block = the QuestFullUpdate content (questId onward) minus the trailing 0x00.
			// Proven byte-identical to the same quest's block embedded in a real login snapshot (the conqueror's 13479
			// tails block) except the 3 explained spots: quest progress @577 (just-accepted 0x64 vs in-progress 0x50)
			// and @585 (0 vs 7), and the player instance @606-609 - i.e. a freshly-accepted quest generates the exact
			// retail block (decode_snapshot_quests.py). Validates the full update's content shape (the basis the
			// from-DB quest-region generation builds on). The block is the full-update's QuestcRecord: the live message
			// and the snapshot journal carry the same record, which is why this comparison works at all.
			var block = QuestcRecordCodec.Build(iir.Record);
			Assert.Equal(879, block.Length);
			var tailsBlock = SnapshotQuestBlock(0x34A7); // the 13479 block from the conqueror's login snapshot (879 B)
			for (var i = 0; i < 879; i++)
			{
				if (i is 577 or 585 or >= 606 and <= 609)
					continue; // progress (577/585) + player instance (606-609): expected per-character differences

				Assert.True(block[i] == tailsBlock[i], $"snapshot block byte @{i}: built=0x{block[i]:x2} capture=0x{tailsBlock[i]:x2}");
			}

		}

		[Theory]
		[InlineData(QuestGoalUpdate.Satisfied)]
		[InlineData(QuestGoalUpdate.Progress)]
		[InlineData(QuestGoalUpdate.Advanced)]
		public void QuestGoalCodec_ParseRoundTripsBuild(QuestGoalUpdate update)
		{
			var body = QuestGoalCodec.Build(0x01316E71, 0x34A7, 0x1670, update);

			Assert.Equal(new QuestGoalIIR(0x01316E71, 0x34A7, 0x1670, update), QuestGoalCodec.Parse(body));

			// Strict Parse: a short body and a wrong marker throw rather than returning false.
			Assert.Throws<CodecParseException>(() => QuestGoalCodec.Parse(new byte[32]));
			var corrupt = (byte[])body.Clone();
			corrupt[12] = 0x00; // marker must be 0x01
			Assert.Throws<CodecParseException>(() => QuestGoalCodec.Parse(corrupt));
		}

		// A word-triple retail never sends must not parse. A reader that only requires the last two words to be zero
		// accepts (9,0,0) as "count 9" and rejects the real Progress/Advanced packets; both halves of that are pinned
		// here.
		[Fact]
		public void QuestGoalCodec_RejectsAnUnknownUpdateTriple()
		{
			var body = QuestGoalCodec.Build(0x01316E71, 0x34A7, 0x1670, QuestGoalUpdate.Satisfied);
			body[^9] = 0x09; // first update word 1 -> 9, a combination that appears nowhere in the corpus
			Assert.Throws<CodecParseException>(() => QuestGoalCodec.Parse(body));
		}

		[Fact]
		public void QuestCodec_ParseRoundTripsBuild()
		{
			var body = QuestCodec.Build(0x01316E71, 0x34A7, QuestCodec.StateActive);

			Assert.Equal(new QuestIIR(0x01316E71, 0x34A7, QuestCodec.StateActive), QuestCodec.Parse(body));

			var corrupt = (byte[])body.Clone();
			corrupt[0] ^= 0xFF; // sub-opcode
			Assert.Throws<CodecParseException>(() => QuestCodec.Parse(corrupt));
		}

		// The extractor's inputs, as CapturedPacket. The RPC opcode is the same IIR mux for every one of them; the
		// sub-opcode it dispatches on comes from the packet itself.
		private static List<CapturedPacket> QuestPackets(string asset)
		{
			return CapturedFixture.Packets(asset).Select(p => new CapturedPacket(0, true, CapturedPacket.Wire(GameInterfaceMethods.DispatchPassBlob), p.Iir, p.Full)).ToList();
		}

		// The body (sub-opcode onward, dropping the 4-byte length prefix) of the captured packet of type `iir`, to
		// assert builders byte-for-byte. `asset` picks which capture: the fresh Casilda offer and the single-quest
		// journal come from two different characters, and both carry a QuestFullUpdate for quest 13479 - the offer's
		// is the one these gates are pinned on.
		private static string CaptureBody(string asset, IirTypes iir)
		{
			foreach (var p in CapturedFixture.Packets(asset))
			{
				if (p.Iir != (uint)iir)
					continue;

				var headerLength = (int)p.Full.ReadU32Be(8);
				return Convert.ToHexString(p.Full[(12 + headerLength + 4)..]).ToLowerInvariant(); // past the body's u32 length
			}

			throw new XunitException($"{asset} carries no {iir} packet");
		}

		private static QuestGoalDefinition GoalReq(uint goalId, uint required)
		{
			return new QuestGoalDefinition
			{
				GoalId = goalId,
				RequiredCount = required
			};
		}

		private static QuestGoalDefinition GoalSeq(uint sequence, uint goalId, QuestGoalType type, string target)
		{
			return new QuestGoalDefinition
			{
				Sequence = sequence,
				GoalId = goalId,
				GoalType = type,
				TargetName = target
			};
		}

		// The per-quest detail block (questId onward, 879 B) for `questId`, carved out of a captured 0x737A6DF9 login
		// snapshot - the conqueror's, which holds quest 13479 in progress.
		private static byte[] SnapshotQuestBlock(uint questId)
		{
			var body = TestPaths.AssetBytes("snapshot-login-conqueror.bin");

			// block header = [questId][1][questId]; find it and take 879 bytes.
			Span<byte> needle = stackalloc byte[12];
			needle.WriteU32Be(0, questId);
			needle.WriteU32Be(4, 1);
			needle.WriteU32Be(8, questId);
			for (var i = 0; i + 879 <= body.Length; i++)
			{
				if (body.AsSpan(i, 12).SequenceEqual(needle))
					return body[i..(i + 879)];
			}

			throw new XunitException($"no snapshot block for quest {questId}");
		}
	}
}
