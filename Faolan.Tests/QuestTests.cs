using System;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Quest state packets (docs/archive/quests-and-combat.md), byte-verified against the hoxxling-login-gameplay capture
	// (quest 0x34A7 = 13479, player 0x01316E71).
	public class QuestTests
	{
		[Fact]
		public void AdvancesOnKill_MatchesActiveIncompleteTarget()
		{
			Assert.True(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 0, 5), "Scavenger Lackey"));
			Assert.True(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 4, 5), "scavenger lackey")); // case-insensitive
		}

		[Fact]
		public void AdvancesOnKill_RejectsNonMatching()
		{
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 0, 5), "Wolf")); // wrong target
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 5, 5), "Scavenger Lackey")); // already done
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 0, 5, QuestState.Completed), "Scavenger Lackey")); // completed
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest(null, 0, 5), "Scavenger Lackey")); // no kill objective
		}

		[Fact]
		public void BuildQuestGoal_MatchesCapture()
		{
			var body = WorldQuest.BuildQuestGoal(0x01316E71, 0x34A7, 0x1670, 1);
			Assert.Equal("0774c8c10000c35001316e7101000034a700001670000000010000000000000000", Convert
				.ToHexString(body)
				.ToLowerInvariant());
		}

		[Fact]
		public void BuildQuestIir_MatchesCapture()
		{
			var body = WorldQuest.BuildQuestIir(0x01316E71, 0x34A7, WorldQuest.StateActive);
			Assert.Equal("97e023140000c35001316e71000000000100000002000034a700000000", Convert
				.ToHexString(body)
				.ToLowerInvariant());
		}

		// Journal CONTENT packets (Captures/decode_quest.py). The Casilda quest 0x34A7 = 13479,
		// goal 0x1670 = 5744, area 1054, map 4010. Keys are the deterministic content hashes harvested
		// from the captures (identical across characters/sessions).
		[Fact]
		public void BuildQuestJournal_MatchesCasildaCapture()
		{
			// stricken998 strickenbla capture, frame 9947 (single-quest journal for 13479).
			var areaHeader = new[]
			{
				WorldQuest.RemoteFormat(1054, 50300, "HD*04&#36;OI3kFza?c54,PZ"),
				WorldQuest.RemoteFormat(4010, 52000, "4r7Ld6vDEY4cdU5[&#39;v3/"),
				WorldQuest.RemoteFormat(4, 52000, "q7i^yK%uD&quot;FmM`F&amp;2iwr")
			};
			var entries = new[]
			{
				new WorldQuest.JournalEntry(0x34A7,
					WorldQuest.RemoteFormat(13479, 50301, "@MSeS6Le,s-7YMf0t1R6"),
					WorldQuest.RemoteFormat(13479, 50303, "i&#36;g&#92;DTrqI&#92;nBI0lUo6&amp;7"))
			};
			var trailing = Convert.FromHexString("00000000000003f100000000000003f100000000");

			var body = WorldQuest.BuildQuestJournal(0x01316D8A, 1054, areaHeader, entries, trailing);
			Assert.Equal(CaptureBody("79f8c7a1"), Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildQuestFullUpdate_MatchesCasildaCapture()
		{
			// stricken997 randochar capture, frame 687 (fresh Casilda offer, player 0x0131720B).
			var textRefs = new[]
			{
				WorldQuest.RemoteFormat(13479, 50301, "@MSeS6Le,s-7YMf0t1R6"),
				WorldQuest.RemoteFormat(13479, 50302, "iHLlH81PhqeCC&#36;cD0VwJ"),
				WorldQuest.RemoteFormat(1054, 50300, "HD*04&#36;OI3kFza?c54,PZ"),
				WorldQuest.RemoteFormat(4, 52000, "q7i^yK%uD&quot;FmM`F&amp;2iwr"),
				WorldQuest.RemoteFormat(4010, 52000, "4r7Ld6vDEY4cdU5[&#39;v3/")
			};
			var objectiveText = WorldQuest.RemoteFormat(5744, 50304, "M8Zw&quot;&quot;xo*V@DWHQ&amp;oPn1");
			var objectiveLocalized = WorldQuest.Localized(11561, 51000, "m", "I!,pP8[uf?E5+a&#36;1uQ6O");

			var body = WorldQuest.BuildQuestFullUpdate(
				playerHandle: 0x0131720B, questId: 0x34A7, state: 3, areaId: 1054, textRefs: textRefs,
				goalId: 0x1670, acceptTime: 0x000111D354375554UL, mapId: 4010, markerX: 0x147, markerY: 0x2A6,
				objectiveText: objectiveText, objectiveLocalized: objectiveLocalized);
			Assert.Equal(CaptureBody("63268258"), Convert.ToHexString(body).ToLowerInvariant());
		}

		// Pull the body (subopcode onward, dropping the 4-byte length prefix) of the named captured
		// packet, to assert builders byte-for-byte.
		private static string CaptureBody(string subOpHex)
		{
			foreach (var (file, frame) in new[]
			{
				("account-stricken997-character-randochar-login-get-casilda-quest.tshark.json", 687),
				("account-stricken998-character-strickenbla-create-random-actions-combat-levelup-questcomplete.tshark.json", 9947)
			})
			{
				var path = System.IO.Path.Combine(CapturesDir, file);
				using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
				foreach (var p in doc.RootElement.GetProperty("Packets").EnumerateArray())
				{
					var bh = p.GetProperty("BodyHex").GetString();
					if (bh != null && bh.Length >= 16 && bh.Substring(8, 8) == subOpHex && p.GetProperty("Frame").GetInt32() == frame)
						return bh.Substring(8); // drop the 4-byte (8 hex) length prefix
				}
			}

			throw new Xunit.Sdk.XunitException($"no captured packet for sub-op {subOpHex}");
		}

		private static readonly string CapturesDir = FindCaptures();

		private static string FindCaptures()
		{
			for (var dir = AppContext.BaseDirectory; dir != null; dir = System.IO.Directory.GetParent(dir)?.FullName)
			{
				var c = System.IO.Path.Combine(dir, "Captures");
				if (System.IO.Directory.Exists(c))
					return c;
			}

			throw new System.IO.DirectoryNotFoundException("Captures/ not found above " + AppContext.BaseDirectory);
		}

		private static CharacterQuest KillQuest(string target, uint progress, uint required, QuestState state = QuestState.Active)
		{
			return new CharacterQuest
			{
				QuestId = 1,
				State = state,
				GoalTargetName = target,
				GoalProgress = progress,
				GoalRequired = required
			};
		}
	}
}
