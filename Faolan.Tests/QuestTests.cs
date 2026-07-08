using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Xunit;
using Xunit.Sdk;

namespace Faolan.Tests
{
	// Quest state packets (docs/archive/quests-and-combat.md), byte-verified against the hoxxling-login-gameplay capture
	// (quest 0x34A7 = 13479, player 0x01316E71).
	public class QuestTests
	{
		private static readonly string CapturesDir = FindCaptures();

		// --- Multi-stage chain supersession predicate (#261) ----------------------------------------------------------

		[Fact]
		public void Advances_MatchesGoalTypeAndTarget()
		{
			// The Casilda chain's three captured trigger stages: each matches only its own goal TYPE + target.
			Assert.True(QuestProgress.Advances(Live(0, 1), Goal(QuestGoalType.Kill, "Scavenger Tuut"), QuestGoalType.Kill, "Scavenger Tuut"));
			Assert.True(QuestProgress.Advances(Live(0, 1), Goal(QuestGoalType.LootFromNpc, "Scavenger Tuut"), QuestGoalType.LootFromNpc, "scavenger tuut")); // case-insensitive
			Assert.True(QuestProgress.Advances(Live(0, 1), Goal(QuestGoalType.TalkToNpc, "Casilda"), QuestGoalType.TalkToNpc, "Casilda"));
		}

		[Fact]
		public void Advances_RejectsWrongTypeTargetOrState()
		{
			var lootGoal = Goal(QuestGoalType.LootFromNpc, "Scavenger Tuut");
			Assert.False(QuestProgress.Advances(Live(0, 1), lootGoal, QuestGoalType.Kill, "Scavenger Tuut")); // a kill must not fire a loot goal
			Assert.False(QuestProgress.Advances(Live(0, 1), lootGoal, QuestGoalType.LootFromNpc, "Casilda")); // wrong target NPC
			Assert.False(QuestProgress.Advances(Live(1, 1), lootGoal, QuestGoalType.LootFromNpc, "Scavenger Tuut")); // objective already complete
			Assert.False(QuestProgress.Advances(Live(0, 1, QuestState.Completed), lootGoal, QuestGoalType.LootFromNpc, "Scavenger Tuut")); // not active
			Assert.False(QuestProgress.Advances(Live(0, 1), null, QuestGoalType.LootFromNpc, "Scavenger Tuut")); // no goal definition
			Assert.False(QuestProgress.Advances(Live(0, 1), Goal(QuestGoalType.LootFromNpc, null), QuestGoalType.LootFromNpc, "Scavenger Tuut")); // unsourced target
		}

		[Fact]
		public void Advances_TreatsZeroRequiredAsOne()
		{
			// A goal seeded with RequiredCount 0 still completes on the first trigger (the predicate clamps to >= 1).
			Assert.True(QuestProgress.Advances(Live(0, 0), Goal(QuestGoalType.TalkToNpc, "Casilda"), QuestGoalType.TalkToNpc, "Casilda"));
		}

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
		public void AdvancesOnKill_SingleKillObjective_TheCasildaShape()
		{
			// Casilda's quest 13479 seeds RequiredCount=1, GoalTargetName="Scavenger Tuut" (#47): one kill of Tuut
			// must advance (0<1) and the next state is complete (1>=1, so a second kill no longer advances).
			Assert.True(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Tuut", 0, 1), "Scavenger Tuut"));
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Tuut", 1, 1), "Scavenger Tuut"));
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

			var body = WorldQuest.BuildQuestFullUpdate(0x0131720B, 0x34A7, 3, 1054, textRefs, 0x1670, 0x000111D354375554UL, 4010, 0x147, 0x2A6, objectiveText, objectiveLocalized);
			Assert.Equal(CaptureBody("63268258"), Convert.ToHexString(body).ToLowerInvariant());
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

		// Journal CONTENT packets (Captures/scripts/decode_quest.py). The Casilda quest 0x34A7 = 13479,
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
				new WorldQuest.JournalEntry(0x34A7, WorldQuest.RemoteFormat(13479, 50301, "@MSeS6Le,s-7YMf0t1R6"), WorldQuest.RemoteFormat(13479, 50303, "i&#36;g&#92;DTrqI&#92;nBI0lUo6&amp;7"))
			};
			var trailing = WorldQuest.BuildTrackedQuestPointer();

			var body = WorldQuest.BuildQuestJournal(0x01316D8A, 1054, areaHeader, entries, trailing);
			Assert.Equal(CaptureBody("79f8c7a1"), Convert.ToHexString(body).ToLowerInvariant());
		}

		// The RICH creation step (mode 7) - "NEW_character_creation Slave Galley": an f4 <remoteformat> token, an f5
		// resource-preload list, and a non-zero f6 pair. Golden vs strickenbla frame 2207 (780 B), proving BuildStep
		// covers the full grammar (#191). The big token/resource strings are the captured creation-step content (their
		// LIVE authoring rides the create-burst construction, #189); here they're the test oracle.
		[Fact]
		public void BuildScryCreationStep_MatchesCapture()
		{
			const string token = "<remoteformat token=\"playorskip(72)|onclientconnected(1)|NEW_character_creation Slave Galley (server-side)\" category=\"50023\" key=\"&quot;B]SZ.AGRGrAyOGe*BG-\" knubot=\"0\"  ></remoteformat>";
			const string resources =
				"1010004:793713;1010004:793714;1070003:4000;1000097:4000;1010030:156211;1010030:239448;1010030:565913;1010004:3728533;1060668:4000;1000059:4000;1060667:167368;1010004:3075445;1010004:3180025;1010004:221285;1010004:3663190;1010004:208923;1010004:208266;1010004:226891;1010004:3663191;1010004:3656109;1010030:210069;1010001:210070;1010004:3075452;1010004:3075450;1010004:3075451;1010300:4000;1000049:4000;1066611:4000;1000013:40000000;1000015:40000000;1010506:4000;1000058:4000;1010207:110006;1010207:262241;1010207:568806;1010207:3420331;1010207:167183";

			var built = ScryScript.BuildStep(0x01316D8A, token, 7, 0, resources, 2699176, 4477056);

			Assert.Equal(
				"a36d3b740000c35001316d8a00000002f708051007180022b9013c72656d6f7465666f726d617420746f6b656e3d22706c61796f72736b6970283732297c6f6e636c69656e74636f6e6e65637465642831297c4e45575f6368617261637465725f6372656174696f6e20536c6176652047616c6c657920287365727665722d7369646529222063617465676f72793d22353030323322206b65793d222671756f743b425d535a2e414752477241794f47652a42472d22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e2aa604313031303030343a3739333731333b313031303030343a3739333731343b313037303030333a343030303b313030303039373a343030303b313031303033303a3135363231313b313031303033303a3233393434383b313031303033303a3536353931333b313031303030343a333732383533333b313036303636383a343030303b313030303035393a343030303b313036303636373a3136373336383b313031303030343a333037353434353b313031303030343a333138303032353b313031303030343a3232313238353b313031303030343a333636333139303b313031303030343a3230383932333b313031303030343a3230383236363b313031303030343a3232363839313b313031303030343a333636333139313b313031303030343a333635363130393b313031303033303a3231303036393b313031303030313a3231303037303b313031303030343a333037353435323b313031303030343a333037353435303b313031303030343a333037353435313b313031303330303a343030303b313030303034393a343030303b313036363631313a343030303b313030303031333a34303030303030303b313030303031353a34303030303030303b313031303530363a343030303b313030303035383a343030303b313031303230373a3131303030363b313031303230373a3236323234313b313031303230373a3536383830363b313031303230373a333432303333313b313031303230373a313637313833320a08a8dfa4011080a19102",
				Convert.ToHexString(built).ToLowerInvariant());
		}

		// ScryScript (0xA36D3B74) destiny/tutorial step driver, built from scratch (#191). Golden vs the
		// strickenbla capture: "IntroDone" (mode 2, flag 0) frame 2237 and "ChangeSex" (flag 6) frame 2333.
		[Fact]
		public void BuildScryStep_MatchesCapture()
		{
			Assert.Equal("a36d3b740000c35001316d8a00000000190805100218002209496e74726f446f6e652a00320408001000", Convert.ToHexString(ScryScript.BuildStep(0x01316D8A, "IntroDone")).ToLowerInvariant());
			Assert.Equal("a36d3b740000c35001316d8a000000001908051002180622094368616e67655365782a00320408001000", Convert.ToHexString(ScryScript.BuildStep(0x01316D8A, "ChangeSex", flag: 6)).ToLowerInvariant());

			// The PostReadyBurst login destiny steps (records 2 + 4), proving BuildStep reproduces them so the
			// verbatim byte arrays there can be retired (construct, don't replay - #191).
			Assert.Equal("a36d3b740000c35001316d8a000000001c080510021800220c5374617274466978656433642a00320408001000", Convert.ToHexString(ScryScript.BuildStep(0x01316D8A, "StartFixed3d")).ToLowerInvariant());
			Assert.Equal("a36d3b740000c35001316d8a000000001f080510021800220f57656c636f6d655475746f7269616c2a00320408001000", Convert.ToHexString(ScryScript.BuildStep(0x01316D8A, "WelcomeTutorial")).ToLowerInvariant());
		}

		// End-to-end: the importer harvests the Casilda definition from the captures, and feeding it back
		// through the builders reproduces the captured packets byte-for-byte (proves QuestDefinitionExtractor's
		// refs + objective marker, and the QuestTextRef -> record mapping).
		[Fact]
		public void QuestDefinitionExtractor_RoundTripsCasilda()
		{
			var packets = TsharkCaptureReader.Read(Path.Combine(CapturesDir, "Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json"));
			var def = QuestDefinitionExtractor.Extract(packets).Single(d => d.QuestId == 0x34A7);

			Assert.Equal(1054u, def.AreaId);
			Assert.Equal(0x1670u, def.GoalId);
			Assert.Equal(4010u, def.MapId);
			Assert.Equal(0x147u, def.MarkerX);
			Assert.Equal(0x2A6u, def.MarkerY);

			// Journal list (no timestamp) - byte-exact against frame 9947.
			var areaHeader = new[]
			{
				Rec(QuestTextSlot.AreaTitle),
				Rec(QuestTextSlot.MapName),
				Rec(QuestTextSlot.RegionName)
			};
			var entries = new[]
			{
				new WorldQuest.JournalEntry(0x34A7, Rec(QuestTextSlot.Title), Rec(QuestTextSlot.ListSubLine))
			};
			var trailing = WorldQuest.BuildTrackedQuestPointer();
			var journal = WorldQuest.BuildQuestJournal(0x01316D8A, def.AreaId, areaHeader, entries, trailing);
			Assert.Equal(CaptureBody("79f8c7a1"), Convert.ToHexString(journal).ToLowerInvariant());

			// Full update - byte-exact against frame 687 when fed the captured accept timestamp.
			var textRefs = new[]
			{
				Rec(QuestTextSlot.Title),
				Rec(QuestTextSlot.Summary),
				Rec(QuestTextSlot.AreaTitle),
				Rec(QuestTextSlot.RegionName),
				Rec(QuestTextSlot.MapName)
			};
			var full = WorldQuest.BuildQuestFullUpdate(0x0131720B,
				def.QuestId,
				3,
				def.AreaId,
				textRefs,
				def.GoalId,
				WorldQuest.SnapshotQuestAcceptTime,
				def.MapId,
				def.MarkerX,
				def.MarkerY,
				Rec(QuestTextSlot.Objective),
				Rec(QuestTextSlot.ObjectiveLocalized));
			Assert.Equal(CaptureBody("63268258"), Convert.ToHexString(full).ToLowerInvariant());

			// Quest-log LIST (the packet that POPULATES the J-panel) - byte-exact against frame 687. The captured
			// randochar had four active quests (tracked 13479 first); the area-title slot is the 13479 def's AreaTitle.
			var logList = WorldQuest.BuildQuestLogList(0x0131720B, def.AreaId, 0x34A7, [0x34A7, 0x34FB, 0x34A8, 0x3077], Rec(QuestTextSlot.AreaTitle));
			Assert.Equal(CaptureBody("504b7924"), Convert.ToHexString(logList).ToLowerInvariant());

			// SNAPSHOT-TAILS per-quest block = the QuestFullUpdate content (questId onward) minus the trailing 0x00.
			// Proven byte-identical to the SAME quest's block embedded in a real login snapshot (xxx-login's 13479
			// tails block) EXCEPT the 3 explained spots: quest PROGRESS @577 (just-accepted 0x64 vs in-progress 0x50)
			// and @585 (0 vs 7), and the player INSTANCE @606-609 - i.e. a freshly-accepted quest generates the exact
			// retail block (decode_snapshot_quests.py). Validates BuildQuestFullUpdate's content shape (the basis the
			// from-DB quest-region generation builds on).
			var block = full[WorldQuest.QuestFullUpdateContentOffset..^1];
			Assert.Equal(879, block.Length);
			var tailsBlock = SnapshotQuestBlock(0x34A7); // the 13479 block from xxx-login's login snapshot (879 B)
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

		// Pull the body (subopcode onward, dropping the 4-byte length prefix) of the named captured
		// packet, to assert builders byte-for-byte.
		private static string CaptureBody(string subOpHex)
		{
			foreach (var (file, frame) in new[]
			{
				("Ranochar_mage-necromancer_login_get-casilda-quest.tshark.json", 687),
				("Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json", 9947)
			})
			{
				var path = Path.Combine(CapturesDir, file);
				using var doc = JsonDocument.Parse(File.ReadAllText(path));
				foreach (var p in doc.RootElement.GetProperty("Packets").EnumerateArray())
				{
					var bh = p.GetProperty("BodyHex").GetString();
					if (bh is { Length: >= 16 } && bh.Substring(8, 8) == subOpHex && p.GetProperty("Frame").GetInt32() == frame)
						return bh.Substring(8); // drop the 4-byte (8 hex) length prefix
				}
			}

			throw new XunitException($"no captured packet for sub-op {subOpHex}");
		}

		private static string FindCaptures()
		{
			for (var dir = AppContext.BaseDirectory; dir != null; dir = Directory.GetParent(dir)?.FullName)
			{
				var c = Path.Combine(dir, "Captures");
				if (Directory.Exists(c))
					return c;
			}

			throw new DirectoryNotFoundException("Captures/ not found above " + AppContext.BaseDirectory);
		}

		private static QuestGoalDefinition Goal(QuestGoalType type, string target)
		{
			return new QuestGoalDefinition
			{
				GoalType = type,
				TargetName = target,
				Sequence = 1
			};
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

		private static CharacterQuest Live(uint progress, uint required, QuestState state = QuestState.Active)
		{
			return new CharacterQuest
			{
				QuestId = 1,
				State = state,
				GoalProgress = progress,
				GoalRequired = required
			};
		}

		// The per-quest detail block (questId onward, 879 B) for `questId` from xxx-login's 0x737A6DF9 login snapshot.
		private static byte[] SnapshotQuestBlock(uint questId)
		{
			var path = Path.Combine(CapturesDir, "Stihetfv_soldier-conqueror_login.tshark.json");
			using var doc = JsonDocument.Parse(File.ReadAllText(path));
			Span<byte> needle = stackalloc byte[12];
			foreach (var p in doc.RootElement.GetProperty("Packets").EnumerateArray())
			{
				var bh = p.GetProperty("BodyHex").GetString();
				if (bh == null || bh.Length < 16 || bh.Substring(8, 8) != "737a6df9")
					continue;

				var body = Convert.FromHexString(bh);
				// block header = [questId][1][questId]; find it and take 879 bytes.
				BinaryPrimitives.WriteUInt32BigEndian(needle[..4], questId);
				BinaryPrimitives.WriteUInt32BigEndian(needle.Slice(4, 4), 1);
				BinaryPrimitives.WriteUInt32BigEndian(needle.Slice(8, 4), questId);
				for (var i = 0; i + 879 <= body.Length; i++)
				{
					if (body.AsSpan(i, 12).SequenceEqual(needle))
						return body[i..(i + 879)];
				}
			}

			throw new XunitException($"no snapshot block for quest {questId}");
		}
	}
}
