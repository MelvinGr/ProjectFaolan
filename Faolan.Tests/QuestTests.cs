using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Extractors;
using Faolan.Tests.Support;
using Xunit;
using Xunit.Sdk;

namespace Faolan.Tests
{
	// Quest state packets (docs/re/quests-and-combat.md), byte-verified against the captured Casilda quest
	// (0x34A7 = 13479, player 0x01316E71).
	public class QuestTests
	{
		// The two committed slices these gates are pinned on. Both carry a QuestFullUpdate for quest 13479 - one from
		// the moment it is OFFERED, one from a later session's journal - so which fixture a builder is compared
		// against is part of the assertion, not an accident of search order (it used to be the latter).
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

		[Fact]
		public void BuildQuestFullUpdate_MatchesCasildaCapture()
		{
			// The fresh Casilda offer (player 0x0131720B), from Assets/casilda-quest-offer.bin.
			var textRefs = new[]
			{
				WorldQuest.RemoteFormat(13479, 50301, "@MSeS6Le,s-7YMf0t1R6"), WorldQuest.RemoteFormat(13479, 50302, "iHLlH81PhqeCC&#36;cD0VwJ"),
				WorldQuest.RemoteFormat(1054, 50300, "HD*04&#36;OI3kFza?c54,PZ"), WorldQuest.RemoteFormat(4, 52000, "q7i^yK%uD&quot;FmM`F&amp;2iwr"),
				WorldQuest.RemoteFormat(4010, 52000, "4r7Ld6vDEY4cdU5[&#39;v3/")
			};
			var objectiveText = WorldQuest.RemoteFormat(5744, 50304, "M8Zw&quot;&quot;xo*V@DWHQ&amp;oPn1");
			var objectiveLocalized = WorldQuest.Localized(11561, 51000, "m", "I!,pP8[uf?E5+a&#36;1uQ6O");

			var body = WorldQuest.BuildQuestFullUpdate(0x0131720B, 0x34A7, 3, 1054, textRefs, 0x1670, 0x000111D354375554UL, 4010, 0x147, 0x2A6, objectiveText, objectiveLocalized);
			Assert.Equal(CaptureBody(OfferFixture, GameServer0X2000Opcodes.QuestFullUpdateIIR), Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildQuestGoal_MatchesCapture()
		{
			var body = WorldQuest.BuildQuestGoal(0x01316E71, 0x34A7, 0x1670, 1);
			Assert.Equal("0774c8c10000c35001316e7101000034a700001670000000010000000000000000", Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildQuestIir_MatchesCapture()
		{
			var body = WorldQuest.BuildQuestIir(0x01316E71, 0x34A7, WorldQuest.StateActive);
			Assert.Equal("97e023140000c35001316e71000000000100000002000034a700000000", Convert.ToHexString(body).ToLowerInvariant());
		}

		// Journal CONTENT packets (tools/capture/decode_quest.py). The Casilda quest 0x34A7 = 13479,
		// goal 0x1670 = 5744, area 1054, map 4010. Keys are the deterministic content hashes harvested
		// from the captures (identical across characters/sessions).
		[Fact]
		public void BuildQuestJournal_MatchesCasildaCapture()
		{
			// A single-quest journal for 13479, from Assets/casilda-quest-packets.bin.
			var areaHeader = new[]
			{
				WorldQuest.RemoteFormat(1054, 50300, "HD*04&#36;OI3kFza?c54,PZ"), WorldQuest.RemoteFormat(4010, 52000, "4r7Ld6vDEY4cdU5[&#39;v3/"),
				WorldQuest.RemoteFormat(4, 52000, "q7i^yK%uD&quot;FmM`F&amp;2iwr")
			};
			var entries = new[]
			{
				new WorldQuest.JournalEntry(0x34A7, WorldQuest.RemoteFormat(13479, 50301, "@MSeS6Le,s-7YMf0t1R6"), WorldQuest.RemoteFormat(13479, 50303, "i&#36;g&#92;DTrqI&#92;nBI0lUo6&amp;7"))
			};
			var trailing = WorldQuest.BuildTrackedQuestPointer();

			var body = WorldQuest.BuildQuestJournal(0x01316D8A, 1054, areaHeader, entries, trailing);
			Assert.Equal(CaptureBody(JournalFixture, GameServer0X2000Opcodes.QuestJournalIIR), Convert.ToHexString(body).ToLowerInvariant());
		}

		// The RICH creation step (mode 7) - "NEW_character_creation Slave Galley": an f4 <remoteformat> token, an f5
		// resource-preload list, and a non-zero f6 pair. Golden vs the captured 780 B step, proving BuildStep
		// covers the full grammar (#191). The big token/resource strings are the captured creation-step content (their
		// LIVE authoring rides the create-burst construction, #189); here they're the test oracle.
		[Fact]
		public void BuildScryCreationStep_MatchesCapture()
		{
			const string token =
				"<remoteformat token=\"playorskip(72)|onclientconnected(1)|NEW_character_creation Slave Galley (server-side)\" category=\"50023\" key=\"&quot;B]SZ.AGRGrAyOGe*BG-\" knubot=\"0\"  ></remoteformat>";
			const string resources =
				"1010004:793713;1010004:793714;1070003:4000;1000097:4000;1010030:156211;1010030:239448;1010030:565913;1010004:3728533;1060668:4000;1000059:4000;1060667:167368;1010004:3075445;1010004:3180025;1010004:221285;1010004:3663190;1010004:208923;1010004:208266;1010004:226891;1010004:3663191;1010004:3656109;1010030:210069;1010001:210070;1010004:3075452;1010004:3075450;1010004:3075451;1010300:4000;1000049:4000;1066611:4000;1000013:40000000;1000015:40000000;1010506:4000;1000058:4000;1010207:110006;1010207:262241;1010207:568806;1010207:3420331;1010207:167183";

			var built = ScryScript.BuildStep(0x01316D8A, token, 7, 0, resources, 2699176, 4477056);

			Assert.Equal(
				"a36d3b740000c35001316d8a00000002f708051007180022b9013c72656d6f7465666f726d617420746f6b656e3d22706c61796f72736b6970283732297c6f6e636c69656e74636f6e6e65637465642831297c4e45575f6368617261637465725f6372656174696f6e20536c6176652047616c6c657920287365727665722d7369646529222063617465676f72793d22353030323322206b65793d222671756f743b425d535a2e414752477241794f47652a42472d22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e2aa604313031303030343a3739333731333b313031303030343a3739333731343b313037303030333a343030303b313030303039373a343030303b313031303033303a3135363231313b313031303033303a3233393434383b313031303033303a3536353931333b313031303030343a333732383533333b313036303636383a343030303b313030303035393a343030303b313036303636373a3136373336383b313031303030343a333037353434353b313031303030343a333138303032353b313031303030343a3232313238353b313031303030343a333636333139303b313031303030343a3230383932333b313031303030343a3230383236363b313031303030343a3232363839313b313031303030343a333636333139313b313031303030343a333635363130393b313031303033303a3231303036393b313031303030313a3231303037303b313031303030343a333037353435323b313031303030343a333037353435303b313031303030343a333037353435313b313031303330303a343030303b313030303034393a343030303b313036363631313a343030303b313030303031333a34303030303030303b313030303031353a34303030303030303b313031303530363a343030303b313030303035383a343030303b313031303230373a3131303030363b313031303230373a3236323234313b313031303230373a3536383830363b313031303230373a333432303333313b313031303230373a313637313833320a08a8dfa4011080a19102",
				Convert.ToHexString(built).ToLowerInvariant());
		}

		// ScryScript (0xA36D3B74) destiny/tutorial step driver, built from scratch (#191). Golden vs the
		// captured steps: "IntroDone" (mode 2, flag 0) and "ChangeSex" (flag 6).
		[Fact]
		public void BuildScryStep_MatchesCapture()
		{
			Assert.Equal("a36d3b740000c35001316d8a00000000190805100218002209496e74726f446f6e652a00320408001000", Convert.ToHexString(ScryScript.BuildStep(0x01316D8A, "IntroDone")).ToLowerInvariant());
			Assert.Equal("a36d3b740000c35001316d8a000000001908051002180622094368616e67655365782a00320408001000",
				Convert.ToHexString(ScryScript.BuildStep(0x01316D8A, "ChangeSex", flag: 6)).ToLowerInvariant());

			// The PostReadyBurst login destiny steps (records 2 + 4), proving BuildStep reproduces them so the
			// verbatim byte arrays there can be retired (construct, don't replay - #191).
			Assert.Equal("a36d3b740000c35001316d8a000000001c080510021800220c5374617274466978656433642a00320408001000",
				Convert.ToHexString(ScryScript.BuildStep(0x01316D8A, "StartFixed3d")).ToLowerInvariant());
			Assert.Equal("a36d3b740000c35001316d8a000000001f080510021800220f57656c636f6d655475746f7269616c2a00320408001000",
				Convert.ToHexString(ScryScript.BuildStep(0x01316D8A, "WelcomeTutorial")).ToLowerInvariant());
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

			// An ORDINARY talk goal is not a delivery (no kind tag, no item ref) and must not be gated - Casilda would
			// otherwise become un-freeable.
			var talk = GoalSeq(1, 13480, QuestGoalType.TalkToNpc, "Casilda");
			Assert.False(QuestProgress.IsDelivery(talk));
			Assert.True(QuestProgress.CanHandIn(talk, 0));

			// Nor is a COLLECT goal, even though it carries the very same item reference - only the kind tag separates
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

		// --- Quest objective-tracking rules (#261/#357) ---------------------------------------------------------------

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
			// #261 Scavenger Tuut: the objective is HOLDING the Scavenger's Key (319224), so taking his other drops off
			// the same corpse must not solve it. Matching on the source NPC alone did exactly that - and also solved the
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
			// RECEIVING an item - so it matches nothing rather than accepting any item off that corpse. Both goals in
			// the game data name theirs; one that does not is a data gap, and the loot path logs it as an error.
			var goals = new[] { GoalSeq(1, 3600, QuestGoalType.LootFromNpc, "Rock Pile") };

			Assert.Null(QuestProgress.MatchingLootGoal(goals, "Rock Pile", 12345));
			Assert.Null(QuestProgress.MatchingLootGoal(goals, "Rock Pile", 0));
			Assert.Null(QuestProgress.MatchingLootGoal(goals, "", 12345)); // no source name -> no match
		}

		[Fact]
		public void MatchingKillGoal_PrefersTemplateId_FallsBackToName()
		{
			// Shipwrecked 10238 (#262): goal 3490 = kill Saddur (TrailingRefId 9500 = his template); goal 1448 =
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
			Assert.Equal(3490u, QuestProgress.MatchingKillGoal(goals, 9500, "Saddur")?.GoalId); // by STABLE template id
			Assert.Equal(3490u, QuestProgress.MatchingKillGoal(goals, 9500, null!)?.GoalId); // id alone, no name needed
			Assert.Null(QuestProgress.MatchingKillGoal(goals, 12141, "Some Lackey")); // wrong id + unknown name -> no match

			// Fallback: a goal seeded by NAME only (no TrailingRefId - e.g. a "kill N <group>" whose ref is a group id).
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

			Assert.Equal(1054u, def.AreaId);
			Assert.Equal(0x1670u, def.GoalId);
			Assert.Equal(4010u, def.MapId);
			Assert.Equal(0x147u, def.MarkerX);
			Assert.Equal(0x2A6u, def.MarkerY);

			// Journal list (no timestamp) - byte-exact against frame 9947.
			var areaHeader = new[] { Rec(QuestTextSlot.AreaTitle), Rec(QuestTextSlot.MapName), Rec(QuestTextSlot.RegionName) };
			var entries = new[] { new WorldQuest.JournalEntry(0x34A7, Rec(QuestTextSlot.Title), Rec(QuestTextSlot.ListSubLine)) };
			var trailing = WorldQuest.BuildTrackedQuestPointer();
			var journal = WorldQuest.BuildQuestJournal(0x01316D8A, def.AreaId, areaHeader, entries, trailing);
			Assert.Equal(CaptureBody(JournalFixture, GameServer0X2000Opcodes.QuestJournalIIR), Convert.ToHexString(journal).ToLowerInvariant());

			// Full update - byte-exact against frame 687 when fed the captured accept timestamp.
			var textRefs = new[] { Rec(QuestTextSlot.Title), Rec(QuestTextSlot.Summary), Rec(QuestTextSlot.AreaTitle), Rec(QuestTextSlot.RegionName), Rec(QuestTextSlot.MapName) };
			var full = WorldQuest.BuildQuestFullUpdate(0x0131720B, def.QuestId, 3, def.AreaId, textRefs, def.GoalId, WorldQuest.SnapshotQuestAcceptTime, def.MapId, def.MarkerX, def.MarkerY,
				Rec(QuestTextSlot.Objective), Rec(QuestTextSlot.ObjectiveLocalized));
			Assert.Equal(CaptureBody(OfferFixture, GameServer0X2000Opcodes.QuestFullUpdateIIR), Convert.ToHexString(full).ToLowerInvariant());

			// Quest-log LIST (the packet that POPULATES the J-panel) - byte-exact against frame 687. The captured
			// captured character had four active quests (tracked 13479 first); the area-title slot is the 13479 def's AreaTitle.
			var logList = WorldQuest.BuildQuestLogList(0x0131720B, def.AreaId, 0x34A7, [0x34A7, 0x34FB, 0x34A8, 0x3077], Rec(QuestTextSlot.AreaTitle));
			Assert.Equal(CaptureBody(OfferFixture, GameServer0X2000Opcodes.QuestLogListIIR), Convert.ToHexString(logList).ToLowerInvariant());

			// Inverse: TryParseQuestLogList recovers the handle, tracked id, and the exact quest-id list (the
			// CaptureQuestImporter path that harvests each character's held-quest set from the J-panel log).
			Assert.True(WorldQuest.TryParseQuestLogList(logList, out var logHandle, out var trackedId, out var logIds));
			Assert.Equal(0x0131720Bu, logHandle);
			Assert.Equal(0x34A7u, trackedId);
			Assert.Equal([0x34A7u, 0x34FBu, 0x34A8u, 0x3077u], logIds);

			// SNAPSHOT-TAILS per-quest block = the QuestFullUpdate content (questId onward) minus the trailing 0x00.
			// Proven byte-identical to the SAME quest's block embedded in a real login snapshot (the conqueror's 13479
			// tails block) EXCEPT the 3 explained spots: quest PROGRESS @577 (just-accepted 0x64 vs in-progress 0x50)
			// and @585 (0 vs 7), and the player INSTANCE @606-609 - i.e. a freshly-accepted quest generates the exact
			// retail block (decode_snapshot_quests.py). Validates BuildQuestFullUpdate's content shape (the basis the
			// from-DB quest-region generation builds on).
			var block = full[WorldQuest.QuestFullUpdateContentOffset..^1];
			Assert.Equal(879, block.Length);
			var tailsBlock = SnapshotQuestBlock(0x34A7); // the 13479 block from the conqueror's login snapshot (879 B)
			for (var i = 0; i < 879; i++)
			{
				if (i is 577 or 585 || i is >= 606 and <= 609)
					continue; // progress (577/585) + player instance (606-609): expected per-character differences

				Assert.True(block[i] == tailsBlock[i], $"snapshot block byte @{i}: built=0x{block[i]:x2} capture=0x{tailsBlock[i]:x2}");
			}

			return;

			byte[] Rec(QuestTextSlot slot)
			{
				var r = def.TextRefs.Single(t => t.Slot == slot);
				return r.IsLocalized
					? WorldQuest.Localized((int)r.RefId, (int)r.Category, r.Flags ?? "", r.KeyText)
					: WorldQuest.RemoteFormat((int)r.RefId, (int)r.Category, r.KeyText);
			}
		}

		[Fact]
		public void TryParseQuestGoal_RoundTripsBuild()
		{
			var body = WorldQuest.BuildQuestGoal(0x01316E71, 0x34A7, 0x1670, 1);

			Assert.True(WorldQuest.TryParseQuestGoal(body, out var player, out var quest, out var goal, out var count));
			Assert.Equal(0x01316E71u, player);
			Assert.Equal(0x34A7u, quest);
			Assert.Equal(0x1670u, goal);
			Assert.Equal(1u, count);

			Assert.False(WorldQuest.TryParseQuestGoal(new byte[32], out _, out _, out _, out _)); // wrong length
			var corrupt = (byte[])body.Clone();
			corrupt[12] = 0x00; // marker must be 0x01
			Assert.False(WorldQuest.TryParseQuestGoal(corrupt, out _, out _, out _, out _));
		}

		[Fact]
		public void TryParseQuestIir_RoundTripsBuild()
		{
			var body = WorldQuest.BuildQuestIir(0x01316E71, 0x34A7, WorldQuest.StateActive);

			Assert.True(WorldQuest.TryParseQuestIir(body, out var player, out var quest, out var state));
			Assert.Equal(0x01316E71u, player);
			Assert.Equal(0x34A7u, quest);
			Assert.Equal(WorldQuest.StateActive, state);

			var corrupt = (byte[])body.Clone();
			corrupt[0] ^= 0xFF; // sub-opcode
			Assert.False(WorldQuest.TryParseQuestIir(corrupt, out _, out _, out _));
		}

		// The extractor's inputs, as CapturedPacket. The RPC opcode is the same IIR mux for every one of them; the
		// sub-opcode it dispatches on comes from the packet itself.
		private static List<CapturedPacket> QuestPackets(string asset)
		{
			return CapturedFixture.Packets(asset).Select(p => new CapturedPacket(0, true, (uint)GameInterfaceOpcodes.DispatchPassBlob, p.Iir, p.Full)).ToList();
		}

		// The body (sub-opcode onward, dropping the 4-byte length prefix) of the captured packet of type `iir`, to
		// assert builders byte-for-byte. `asset` picks WHICH capture: the fresh Casilda offer and the single-quest
		// journal come from two different characters, and both carry a QuestFullUpdate for quest 13479 - the offer's
		// is the one these gates are pinned on.
		private static string CaptureBody(string asset, GameServer0X2000Opcodes iir)
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
