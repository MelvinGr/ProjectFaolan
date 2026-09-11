using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     One Quest_c journal record - the client's per-quest journal entry, read by FUN_0091df50. Composed by
	///     <see cref="QuestcRegionCodec" /> for the snapshot's journal list, and by <see cref="QuestFullUpdateCodec" />,
	///     which carries exactly one of these as its whole payload.
	/// </summary>
	/// <remarks>
	///     <code>
	///     [u32 listKey(=questId)][u32 ver(=1)][u32 questId][u32 level][u32 bandTolerance][u32 bandLevel][u32 n]
	///     [string title][string summary][string area][string region1][string region2]
	///     [u32 fieldEc][u32 questLineId][u32 field2c][u32 field11c][u64 money][u32 xpReward]
	///     [rewardList1][rewardList2][u32 fieldF0][u64 fieldF8][rewardList3][rewardList4]
	///     [objective region]
	///     </code>
	///     Each reward list is a plain u32 count followed by that many <see cref="QuestRewardItem" /> elements.
	///     <para>
	///         The objective region is part of the record, interleaved per quest, and not a separate top-level region: a
	///         reader that skips it desyncs on the second record of any multi-quest journal.
	///     </para>
	/// </remarks>
	public static class QuestcRecordCodec
	{
		public const uint RecordVersion = 1;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(QuestcRecord value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestcRecord Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestcRecordCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestcRecord Read(ref SpanReader r)
		{
			var at = r.Position;
			var listKey = r.ReadU32Be();
			r.ExpectU32Be(RecordVersion, "Quest_c record version");

			var questId = r.ReadU32Be();
			if (listKey != questId)
				throw CodecParseException.Unexpected(nameof(QuestcRecordCodec), at, $"listKey == questId ({questId})", listKey.ToString());

			var level = r.ReadU32Be();
			var field114 = r.ReadU32Be();
			var field118 = r.ReadU32Be();
			var n = r.ReadU32Be();

			var title = r.ReadStringU16Length();
			var summary = r.ReadStringU16Length();
			var area = r.ReadStringU16Length();
			var region1 = r.ReadStringU16Length();
			var region2 = r.ReadStringU16Length();

			var fieldEc = r.ReadU32Be();
			var areaId = r.ReadU32Be();
			var field2c = r.ReadU32Be();
			var field11c = r.ReadU32Be();
			var money = r.ReadU64Be();
			var field28 = r.ReadU32Be();

			var obj1 = ReadRewardList(ref r);
			var obj2 = ReadRewardList(ref r);
			var fieldF0 = r.ReadU32Be();
			var fieldF8 = r.ReadU64Be();
			var obj3 = ReadRewardList(ref r);
			var obj4 = ReadRewardList(ref r);

			return new QuestcRecord
			{
				QuestId = questId,
				Level = level,
				BandTolerance = field114,
				BandLevel = field118,
				QuestLineFlags = n,
				Title = title,
				Summary = summary,
				Area = area,
				Region1 = region1,
				Region2 = region2,
				FieldEc = fieldEc,
				QuestLineId = areaId,
				Field2c = field2c,
				Field11c = field11c,
				MoneyReward = money,
				XpReward = field28,
				FieldF0 = fieldF0,
				FieldF8 = fieldF8,
				Obj1 = obj1,
				Obj2 = obj2,
				Obj3 = obj3,
				Obj4 = obj4,
				Objectives = QuestObjectiveRegionCodec.Read(ref r)
			};
		}

		internal static void Write(FaolanStream s, QuestcRecord v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(v.QuestId); // listKey
			s.WriteUInt32(RecordVersion);
			s.WriteUInt32(v.QuestId);
			s.WriteUInt32(v.Level);
			s.WriteUInt32(v.BandTolerance);
			s.WriteUInt32(v.BandLevel);
			s.WriteUInt32(v.QuestLineFlags);

			s.WriteString(v.Title);
			s.WriteString(v.Summary);
			s.WriteString(v.Area);
			s.WriteString(v.Region1);
			s.WriteString(v.Region2);

			s.WriteUInt32(v.FieldEc);
			s.WriteUInt32(v.QuestLineId);
			s.WriteUInt32(v.Field2c);
			s.WriteUInt32(v.Field11c);
			s.WriteUInt64(v.MoneyReward);
			s.WriteUInt32(v.XpReward);

			WriteRewardList(s, v.Obj1);
			WriteRewardList(s, v.Obj2);
			s.WriteUInt32(v.FieldF0);
			s.WriteUInt64(v.FieldF8);
			WriteRewardList(s, v.Obj3);
			WriteRewardList(s, v.Obj4);

			// Null means the caller composes this record's objective region, which is how the beach goldens slice the
			// journal at @609 and compare the objective region separately. A record read off the wire always carries
			// one, so the asymmetry only reaches hand-built values.
			if (v.Objectives != null)
				QuestObjectiveRegionCodec.Write(s, v.Objectives);
		}

		/// <summary>
		///     Reads one reward list. The count is a plain u32, not a <see cref="ListFrame" /> word: the client reads it bare
		///     and clamps it to 100 (FUN_0091df50).
		/// </summary>
		private static List<QuestRewardItem> ReadRewardList(ref SpanReader r)
		{
			var count = r.ReadU32Be();
			var list = new List<QuestRewardItem>((int)count);
			for (var i = 0; i < count; i++)
				list.Add(QuestRewardItemCodec.Read(ref r));

			return list;
		}

		private static void WriteRewardList(FaolanStream s, List<QuestRewardItem> list)
		{
			s.WriteUInt32(list.Count);
			foreach (var entry in list)
				QuestRewardItemCodec.Write(s, entry);
		}
	}

	/// <summary>One quest's journal record. See QuestcRecords for the database-row factory.</summary>
	public sealed record QuestcRecord
	{
		public uint QuestLineId { get; init; } // @+0xcc (matches the Area string's id)
		public uint QuestId { get; init; }

		/// <summary>
		///     The quest's four reward lists. Each entry is one reward item (see <see cref="QuestRewardItem" />, whose key
		///     half is proven by decrypting the template it names). They are empty for the beach starter, the one record the
		///     golden template covers; over the dumps 7,347 elements are populated across 3,741 journal records
		///     (<c>--report quest-rewards</c>).
		/// </summary>
		/// <remarks>
		///     obj1 and obj2 are this quest's own granted and choose-one items, obj3 and obj4 the line's, previewed on every
		///     step but the line's last - see <see cref="QuestRewardLists" />. The wire order
		///     <c>[obj1][obj2][fieldF0][fieldF8][obj3][obj4]</c> pairs each list pair with its XP and money words the same
		///     way. The positional names stay on the record so it reads like the wire; the meaning lives with the factory.
		/// </remarks>
		public List<QuestRewardItem> Obj1 { get; init; } = [];
		public List<QuestRewardItem> Obj2 { get; init; } = [];
		public List<QuestRewardItem> Obj3 { get; init; } = [];
		public List<QuestRewardItem> Obj4 { get; init; } = [];

		/// <summary>
		///     The quest's @609 objective region, which on the wire follows this record's obj4: the journal record and its
		///     objective region are interleaved per quest rather than two separate top-level regions. Null only for a record
		///     built without one. tools/capture/decode_questc_progress.py validates the interleaved grammar across all 141
		///     captured snapshots.
		/// </summary>
		public QuestObjectiveRegion? Objectives { get; set; }
		public string Area { get; init; } = ""; // <remoteformat category=50300>
		public string Region1 { get; init; } = ""; // <remoteformat category=52000>
		public string Region2 { get; init; } = ""; // <remoteformat category=52000>
		public string Summary { get; init; } = ""; // <remoteformat category=50302>
		public string Title { get; init; } = ""; // <remoteformat category=50301>
		/// <summary>
		///     @+0x114 is the tolerance of the quest's level band and @+0x118 the level it is centred on, named for what
		///     their one consumer does with them: the client's band function FUN_0091f6f0(quest, characterLevel), which
		///     returns a five-way difficulty code.
		///     <code>
		///     H = quest.BandLevel != 0 ? quest.BandLevel : quest.Level     // @+0x10 is the fallback
		///     L = quest.BandTolerance;  low = H - L - 1;  top = H + L + 3
		///     level in [low, top] -&gt; 2  |  above: 1 within 3 more, else 0  |  below: 3 within 1 less, else 4
		///     </code>
		///     That is the quest's colour in the journal - on level, slightly or far above, slightly or far below - and its
		///     caller is the journal panel at FUN_012ce1c0. It also pins BandLevel as the level the quest is for from the
		///     client side, independently of the reward-scaling measurement in QuestDefinition.BandLevel.
		/// </summary>
		public uint BandTolerance { get; init; } // @+0x114 (=0 on the beach template)
		public uint BandLevel { get; init; } // @+0x118
		public uint Field11c { get; init; } // @+0x11c (=0)
		/// <summary>
		///     @+0x28 - the quest's experience reward, the number the journal's detail pane renders as "XP:". Proven
		///     in-client by driving one quest's value to 5553, 0 and 7777 and reading the panel back; see
		///     QuestDefinition.XpReward. It is 0 on the beach template and 0x50 in the live full-update.
		/// </summary>
		public uint XpReward { get; init; }
		/// <summary>
		///     @+0x2c - the chain head: the first quest of the series this quest belongs to, which is the quest's own id for
		///     a standalone one. Measured over the 99 quests with a captured record, 84 carry their own id and each of the
		///     other 15 carries an earlier quest that is a step of the same chain: 10915 and 12371 both carry 10238, the
		///     beach starter, and both appear in its own area catalog; 12068 carries 12067; 13986 carries 13984. Generation
		///     takes it from QuestDefinition.ChainHeadQuestId, falling back to the quest's own id, since the head is not
		///     derivable from anything else we store.
		/// </summary>
		public uint Field2c { get; init; }
		public uint FieldEc { get; init; } // @+0xec (=1 in template)
		/// <summary>
		///     @+0xf0 - not the journal's XP (that is <see cref="XpReward" />, verified in-client) and not its money either.
		///     It correlates with the client catalog's line-level <c>xp_reward</c>, which is suggestive rather than
		///     established; see QuestDefinition.FieldF0. It also selects the objective region's text spelling. It is 0x96 in
		///     the template and 0x64 in the live full-update.
		/// </summary>
		/// <remarks>
		///     The client has no consumer for this field or for <see cref="FieldF8" />. Exactly three functions in the image
		///     touch Quest_c+0xf0 and +0xf8: the constructor FUN_0091cf10 zeroes them, the stream reader FUN_0091df50 fills
		///     them and the stream writer FUN_0091d260 puts them back; nothing reads them to compute or draw anything, which
		///     is why driving them in-client moves no label. That is a real difference between fields rather than a property
		///     of the record - the neighbouring BandTolerance and BandLevel do have a consumer - so they are values the
		///     journal round-trips on retail's behalf and the sender's job is to echo what was captured per quest.
		/// </remarks>
		public uint FieldF0 { get; init; }

		/// <summary>
		///     @+0x10 - the quest's level, not a state. It equals the database Level on 96 of the 99 quests with a captured
		///     record, the three that differ (12373, 17402, 17403) differing by 1 or 2 because the row took its level from a
		///     different capture. Distinct from QuestCodec's state codes, which are a real state on a different message.
		/// </summary>
		public uint Level { get; set; } // settable: world entry patches the lead quest's word

		/// <summary>
		///     @+0x14 - a bitfield of the quest line, inherited by each of its steps: constant per quest across 2,626
		///     captured records and constant across every quest of the same line (90 of 90). It is neither a goal count -
		///     emitting it as one desyncs the client and crashes it in StatList_t - nor a <see cref="ListFrame" /> word.
		/// </summary>
		/// <remarks>
		///     Bit 4 (0x10) means the quest may not be shared, and the journal greys its Share button; proven in-client, where
		///     bits 3 and 10 change nothing in that panel. See QuestDefinition.QuestLineFlags.
		/// </remarks>
		public uint QuestLineFlags { get; init; }
		/// <summary>
		///     @+0x20 - the quest's money reward, and the twin of <see cref="XpReward" /> at @+0x28, the two sitting adjacent
		///     in the struct. The journal's detail pane prints it on the "Money:" line. The unit is tin, with 100 tin to a
		///     copper and 100 copper to a silver, so 214 renders "2C 14T" and 250000 renders "25S".
		/// </summary>
		/// <remarks>
		///     It is not a timestamp. 21 of the 99 captured quests carry a non-zero value, constant per quest across every
		///     character and capture, and that set is a subset of the quests carrying an XP reward, some quests paying XP
		///     only. Proven in-client by driving it and reading the panel back; see QuestDefinition.MoneyReward.
		/// </remarks>
		public ulong MoneyReward { get; init; }
		/// <summary>
		///     @+0xf8 - not the journal's money: setting it to 999999 leaves that label blank. It tracks
		///     <see cref="FieldF0" /> through a level-dependent factor that 99 samples do not close, so neither may be
		///     derived from the other.
		/// </summary>
		public ulong FieldF8 { get; init; } // (=15 in the template, 0 in the live full-update)
	}
}
