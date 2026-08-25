using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// #893: the four reward lists of a Quest_c record, built from rows. The rules under test are the two the corpus
	// and the client's journal renderer (FUN_01617260) agree on:
	//   * obj3/obj4 are the LINE's catalog reward, carried on every step but the line's last;
	//   * obj1/obj2 are the quest's OWN set - and on the last step, with nothing of its own, the line's reward FOLDS in.
	// `--report quest-reward-lists` is where those rules are re-measured against the dumps; this gates the builder.
	public class QuestRewardListsTests
	{
		private const uint Instance = 0x01317CF8;

		private static readonly Item Gloves = new() { Id = 3806325, Level = 6, Key = new ProtoDecryptionKey { Key1 = 1, Key2 = 2, Key3 = 3, Key4 = 4 } };
		private static readonly Item Staff = new() { Id = 3394679, Level = 5, Key = new ProtoDecryptionKey { Key1 = 5, Key2 = 6, Key3 = 7, Key4 = 8 } };
		private static readonly Item Keyless = new() { Id = 27655, Level = 5 };

		private static Item? ItemOf(uint id)
		{
			return new[] { Gloves, Staff, Keyless }.FirstOrDefault(i => i.Id == id);
		}

		private static QuestLine Line386(uint last = 10836)
		{
			return new QuestLine
			{
				QuestLineId = 386,
				FirstQuestId = 10928,
				LastQuestId = last,
				Rewards = [new QuestLineReward { QuestLineId = 386, Pos = 0, ItemTemplateId = Gloves.Id, Optional = false, Quantity = 1 }]
			};
		}

		[Fact]
		public void AMidStep_CarriesTheLineRewardAsObj3_AndItsOwnSetAsObj1Obj2()
		{
			// Quest 10928 as captured: first step of line 386, own choose-one of three weapons, line reward gloves.
			var def = new QuestDefinition
			{
				QuestId = 10928,
				QuestLineId = 386,
				Rewards = [new QuestRewardOption { QuestId = 10928, Pos = 0, ItemTemplateId = Staff.Id, Level = 5, Optional = true, Quantity = 1 }]
			};

			var lists = QuestRewardLists.ForQuest(def, Line386(), Instance, ItemOf);

			Assert.Empty(lists.Obj1);
			Assert.Equal([Staff.Id], lists.Obj2.Select(e => e.TemplateId));
			Assert.Equal([Gloves.Id], lists.Obj3.Select(e => e.TemplateId));
			Assert.Empty(lists.Obj4);

			// Every word of the element comes from a row, not a guess: the key and level off the item, the quantity
			// off the reward row, the instance and quest id as addressed, and the template2 half a mirror.
			var e = lists.Obj3[0];
			Assert.Equal(Instance, e.Instance);
			Assert.Equal(10928u, e.QuestId);
			Assert.Equal(QuestRewardLists.TemplateType, e.TemplateType);
			Assert.Equal(Gloves.Key, e.Key);
			Assert.Equal(Gloves.Id, e.TemplateId2);
			Assert.Equal(Gloves.Key, e.Key2);
			Assert.Equal(6u, e.Level);
			Assert.Equal(1u, e.Quantity);
		}

		[Fact]
		public void TheLastStep_FoldsTheLineRewardIntoObj1_AndCarriesNoObj3()
		{
			var def = new QuestDefinition { QuestId = 10836, QuestLineId = 386 };

			var lists = QuestRewardLists.ForQuest(def, Line386(), Instance, ItemOf);

			Assert.Equal([Gloves.Id], lists.Obj1.Select(e => e.TemplateId));
			Assert.Empty(lists.Obj2);
			Assert.Empty(lists.Obj3);
			Assert.Empty(lists.Obj4);
		}

		[Fact]
		public void TheLastStep_WithItsOwnSet_KeepsItsOwnAndDoesNotFold()
		{
			// The 317 captured last-step records whose own set differs from the line's - e.g. 17403's Glowing Rune.
			var def = new QuestDefinition
			{
				QuestId = 10836,
				QuestLineId = 386,
				Rewards = [new QuestRewardOption { QuestId = 10836, Pos = 0, ItemTemplateId = Staff.Id, Optional = false, Quantity = 2 }]
			};

			var lists = QuestRewardLists.ForQuest(def, Line386(), Instance, ItemOf);

			Assert.Equal([Staff.Id], lists.Obj1.Select(e => e.TemplateId));
			Assert.Equal(2u, lists.Obj1[0].Quantity);
			Assert.Empty(lists.Obj3);
		}

		// A template we hold no key for is LEFT OUT, never sent with a zero key: the client decrypts each element from
		// its own RDB with the key we ship, and a wrong one renders as "No name".
		[Fact]
		public void AnElementWithoutAKey_IsLeftOut()
		{
			var def = new QuestDefinition
			{
				QuestId = 10928,
				QuestLineId = 386,
				Rewards =
				[
					new QuestRewardOption { QuestId = 10928, Pos = 0, ItemTemplateId = Keyless.Id, Optional = true },
					new QuestRewardOption { QuestId = 10928, Pos = 1, ItemTemplateId = Staff.Id, Optional = true }
				]
			};

			var lists = QuestRewardLists.ForQuest(def, Line386(), Instance, ItemOf);

			Assert.Equal([Staff.Id], lists.Obj2.Select(e => e.TemplateId));
		}

		[Fact]
		public void NoLine_AndNoOwnRows_BuildsFourEmptyLists()
		{
			var lists = QuestRewardLists.ForQuest(new QuestDefinition { QuestId = 1 }, null, Instance, ItemOf);

			Assert.All(new[] { lists.Obj1, lists.Obj2, lists.Obj3, lists.Obj4 }, l => Assert.Empty(l));
		}

		// A line reward with no captured quantity is previewed at 1 - the value 61% of captured elements carry and the
		// only one a non-token item has ever carried. Explicit here so a change to that default is a deliberate one.
		[Fact]
		public void AnUncapturedLineQuantity_DefaultsToOne()
		{
			var line = Line386();
			line.Rewards.First().Quantity = null;

			var lists = QuestRewardLists.ForQuest(new QuestDefinition { QuestId = 10928, QuestLineId = 386 }, line, Instance, ItemOf);

			Assert.Equal(1u, lists.Obj3[0].Quantity);
		}
	}
}
