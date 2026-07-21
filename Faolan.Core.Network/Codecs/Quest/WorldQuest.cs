using System.Collections.Generic;
using System.Text;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// Quest state packets, decoded from captures (docs/re/quests-and-combat.md). These build the 0x2000 IIR record
	// bodies (after the u32 length prefix). Quest text (title/desc/journal) is NOT sent here - the client renders it
	// from its RDB by quest id; the server only drives quest STATE + objective progress. The full accept/branch logic
	// is the pinned RDB Lua quest VM.
	public static class WorldQuest
	{
		// BuildQuestFullUpdate emits [subop 4][ack 4][player 4][u32 0 = 4][0x01 = 1] = 17 bytes before `questId`; the
		// per-quest snapshot-tails content is that output from `questId` onward (used by the snapshot-shape tests).
		public const int QuestFullUpdateContentOffset = 17;
		public const uint AreaTitleCategory = 50300; // category of the area journal-title remoteformat

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 ("Shipwrecked") in its tails, with its
		// FULL multi-objective journal record, for every character (snapshot_embeds_strie_quest). The snapshot is the
		// authoritative delivery for it - the server must NOT re-send a (thinner, single-objective) QuestFullUpdate for
		// it on login, or it clobbers the snapshot's richer entry. Persisted via EnsureBeachStarterQuest; skipped in
		// RestorePlayerQuests.
		public const uint BeachStarterQuestId = 10238;

		// #262 the Casilda intro-chain ESCORT stage (the final stage: 13479 kill -> 13563 loot -> 13480 talk -> 12407
		// escort). While this stage is active the player has "freed" Casilda and she follows them as their escort; on
		// login at this stage the follow is restored (BurstlessWorldEntry). Completing it ends the escort (she stops).
		public const uint CasildaEscortQuestId = 12407;

		// The QuestFullUpdate (0x63268258) carries the quest's LEVEL (the u32 after the second questId) - a quest PROPERTY,
		// CONSTANT per quest across its whole lifecycle (every captured 13479 full-update uses 3, on grant and on every
		// re-display: frames 687/8876/9947/10129). It was earlier read as a "state/category" enum, but the harvest across
		// faolan.db tracks LEVEL, not state (Tortage starter 1/3/4/5, open-world 23/24, endgame 80; two quests in the same
		// zone can be 23 and 80; never the QuestIIR StateActive value 2). Passing the QuestIIR state (2) here makes the
		// client silently DROP the full-update (journal stays empty) - the bug that first surfaced this field. Now
		// harvested per quest into QuestDefinition.Level (#352); this const is the FALLBACK the sender uses only for a
		// quest with no captured full-update (Level == 0) - 3 is Casilda 13479's level, a sane Tortage default.
		public const uint DefaultQuestLevel = 3;

		// --- Journal CONTENT packets (tools/capture/decode_quest.py, byte-exact golden tests) ----------
		//
		// These carry the J-panel text the player reads. Text is NEVER literal: every slot is a
		// <remoteformat .../> or <localized .../> token packed as  00 <u8 len> <ascii>.  The client
		// resolves (id, category) against its own RDB; the `key` attribute is a DETERMINISTIC content
		// hash (identical across characters/sessions/captures), so it is harvested data, not a nonce.
		// Builders emit from the subopcode onward; the 4-byte length prefix is added by the send layer
		// (matching BuildQuestIir/BuildQuestGoal).

		// Quest state codes (from the capture). On completion retail sends QuestIIR twice for the quest: state 2
		// (active/update) then state 4 (completed) - see the Stihetfv compeltefirstquest capture (13479 idx858/860).
		public const uint StateActive = 2;
		public const uint StateCompleted = 4;

		// The fixed u64 the QuestFullUpdate carries at its `acceptTime` slot inside the snapshot tails (NOT a live
		// timestamp - it is the same constant in the live grant packet and the in-progress login snapshot; decoded as
		// a structural header field).
		public const ulong SnapshotQuestAcceptTime = 0x000111D354375554UL;
		private const int EntrySize = 8;

		// QuestLogList: the fixed head before the entries, and one [u32 ordinal][u32 questId] entry.
		private const int HeaderLength = 29;
		private const string RrlMagic = "RRL3"; // record-list magic that precedes the per-quest entries

		private const uint ChapterTag = 0x00000BD3; // per-quest grouping word, constant across Tortage quests

		// The IIR types this codec constructs or parses (#317 convention; read by the coverage matrix).
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.QuestFullUpdateIIR, GameServer0X2000Opcodes.QuestGoalIIR, GameServer0X2000Opcodes.QuestIIR, GameServer0X2000Opcodes.QuestJournalIIR,
			GameServer0X2000Opcodes.QuestLogListIIR
		];

		// QuestFullUpdate: one quest's full record (description, summary, objective + world marker).
		//   header: [00000000][01][questId][00000001][questId][level][00000000][00000003][0x412]
		//   <title 50301> <summary 50302> <areaTitle 50300> <region 52000> <map 52000>
		//   <stat/objective run carrying areaId, questId, goalId, player, acceptTime, map+x+y marker>
		//   <objective text 50304> <objective localized> [00]
		// The mid-run scalars (0x50/0x64/0x7e2/0x0c ...) are not yet semantically pinned; they are
		// reproduced as a documented template and locked by the golden test.
		public static byte[] BuildQuestFullUpdate(uint playerHandle, uint questId, uint level, uint areaId, IReadOnlyList<byte[]> textRefs, uint goalId, ulong acceptTime, uint mapId, uint markerX,
			uint markerY, byte[] objectiveText, byte[] objectiveLocalized)
		{
			var w = new FaolanStream();
			WriteHeader(w, (uint)GameServer0X2000Opcodes.QuestFullUpdateIIR, playerHandle);
			w.WriteUInt32(0);
			w.WriteByte(0x01);
			w.WriteUInt32(questId);
			w.WriteUInt32(1);
			w.WriteUInt32(questId);
			w.WriteUInt32(level);
			w.WriteUInt32(0);
			w.WriteUInt32(3);
			w.WriteUInt32(0x412);
			foreach (var rec in textRefs) // title, summary, areaTitle, region, map
				w.WriteArray(rec);
			// Objective/stat run.
			w.WriteUInt32(1);
			w.WriteUInt32(areaId);
			w.WriteUInt32(questId);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0x50);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0x64);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			// A count-framed list word for ONE element, not a constant. Established from the CLIENT, not the captures:
			// of the 136 functions that touch this word, 77 DIVIDE it by 0x3F1 to get a count and 59 multiply to build
			// one - NONE compares it to a constant. (Which list it frames here is not decoded; the surrounding run is
			// still a verbatim single-goal template, so this stays ForCount(1) = the captured 0x7E2, byte-identical.)
			w.WriteUInt32(ListFrame.ForCount(1));
			w.WriteUInt32(1);
			w.WriteUInt32(0x0C);
			w.WriteUInt32(playerHandle);
			w.WriteUInt32(questId);
			w.WriteUInt32(goalId);
			w.WriteUInt32(1);
			w.WriteUInt32(goalId);
			w.WriteUInt32(1);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt64(acceptTime);
			w.WriteUInt32(0);
			w.WriteUInt32(mapId);
			w.WriteUInt32(markerX);
			w.WriteUInt32(markerY);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteByte(0x00); // trailing u16 before the objective record
			w.WriteByte(0x00);
			w.WriteArray(objectiveText);
			w.WriteArray(objectiveLocalized);
			w.WriteByte(0x00);
			return w.ToArray();
		}

		// QuestGoal (33 B): [subop][ack][playerHandle][0x01][u32 questId][u32 goalId][u32 count][0][0].
		public static byte[] BuildQuestGoal(uint playerHandle, uint questId, uint goalId, uint count)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.QuestGoalIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.WriteByte(0x01)
				.WriteUInt32(questId)
				.WriteUInt32(goalId)
				.WriteUInt32(count)
				.WriteUInt32(0) // trailing [0][0]
				.WriteUInt32(0)
				.ToArray();
		}

		// QuestIIR (29 B): [subop][ack][playerHandle][0][0x01 marker][u32 state][u32 questId][0].
		public static byte[] BuildQuestIir(uint playerHandle, uint questId, uint state)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.QuestIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.WriteUInt32(0) // [12..16] = 0
				.WriteByte(0x01)
				.WriteUInt32(state)
				.WriteUInt32(questId)
				.WriteUInt32(0) // [25..29] = 0
				.ToArray();
		}

		// QuestJournal: the whole J-panel quest list.
		//   [01][u32 areaId] <3 area-header records> "RRL3" [00000000][u32 count]
		//   per entry: [u32 index(1-based)][u32 questId][u32 chapter] <title> <subLine>
		//   [trailing tracked-quest block]
		// `areaHeader` is the 3 region/zone-title records (cat 50300 + two cat 52000 map/region names).
		// `trailing` is the "currently tracked quest" pointer block - pass BuildTrackedQuestPointer() (the typed
		// no-track form; the server doesn't drive quest tracking).
		public static byte[] BuildQuestJournal(uint playerHandle, uint areaId, IReadOnlyList<byte[]> areaHeader, IReadOnlyList<JournalEntry> entries, byte[] trailing)
		{
			var w = new FaolanStream();
			WriteHeader(w, (uint)GameServer0X2000Opcodes.QuestJournalIIR, playerHandle);
			w.WriteByte(0x01);
			w.WriteUInt32(areaId);
			foreach (var rec in areaHeader)
				w.WriteArray(rec);
			w.WriteArray(Encoding.Latin1.GetBytes(RrlMagic));
			w.WriteUInt32(0);
			w.WriteUInt32(entries.Count);
			for (var i = 0; i < entries.Count; i++)
			{
				w.WriteUInt32(i + 1); // 1-based index
				w.WriteUInt32(entries[i].QuestId);
				w.WriteUInt32(ChapterTag);
				w.WriteArray(entries[i].TitleRecord);
				w.WriteArray(entries[i].SubLineRecord);
			}

			w.WriteArray(trailing);
			return w.ToArray();
		}

		// The active-quest LIST (0x504B7924) that POPULATES the J-panel. WITHOUT this the client never shows an
		// accepted quest in the log even though it received the per-quest QuestFullUpdate detail (the detail alone is
		// not enough - decoded from randochar frame 687 / strickenbla frame 3086). Retail sends it at every grant,
		// listing ALL the player's active quests (tracked quest first), plus the current area's journal-title slot.
		// Wire (subopcode onward):
		//   [subop][ack][u32 player] 01 [u32=1] [u32 areaId] [u32 trackedQuestId] [u32 count]
		//   count × { u32 index(1-based); u32 questId }
		//   <areaTitle text slot: 00 <len> <remoteformat areaId/50300>>
		//   [u32=1] [10 zero bytes]
		public static byte[] BuildQuestLogList(uint playerHandle, uint areaId, uint trackedQuestId, IReadOnlyList<uint> questIds, byte[] areaTitleRecord)
		{
			var w = new FaolanStream();
			WriteHeader(w, (uint)GameServer0X2000Opcodes.QuestLogListIIR, playerHandle);
			w.WriteByte(0x01);
			w.WriteUInt32(1);
			w.WriteUInt32(areaId);
			w.WriteUInt32(trackedQuestId);
			w.WriteUInt32(questIds.Count);
			for (var i = 0; i < questIds.Count; i++)
			{
				w.WriteUInt32(i + 1);
				w.WriteUInt32(questIds[i]);
			}

			w.WriteArray(areaTitleRecord);
			w.WriteUInt32(1);
			for (var i = 0; i < 10; i++)
				w.WriteByte(0x00);
			return w.ToArray();
		}

		// The "currently tracked quest" pointer block that closes a QuestJournal: [u32 trackedQuestId][list A][list B],
		// each list = [u32 (count+1)*0x3F1 framing][u32 count][entries]. Decoded byte-exact from every captured
		// single-quest journal, which all carry the NO-TRACK state (trackedQuestId 0, both lists empty). The server does
		// not drive quest TRACKING - the client picks which quest to follow - so this emits that no-track form from typed
		// fields instead of a hex blob. (Pinning the populated tracked form needs a track-a-quest capture; #193.)
		public static byte[] BuildTrackedQuestPointer(uint trackedQuestId = 0)
		{
			var w = new FaolanStream();
			w.WriteUInt32(trackedQuestId);
			w.WriteUInt32(ListFrame.Empty); // an EMPTY count-framed list
			w.WriteUInt32(0); // list A (empty)
			w.WriteUInt32(ListFrame.Empty); // an EMPTY count-framed list
			w.WriteUInt32(0); // list B (empty)
			return w.ToArray();
		}

		public static byte[] Localized(int id, int category, string flags, string key)
		{
			var xml = $"<localized id=\"{id}\" category=\"{category}\" flags=\"{flags}\" key=\"{key}\" >";
			return new FaolanStream().WriteString(xml).ToArray();
		}

		// One <remoteformat>/<localized> text slot:  00 <u8 length> <ascii bytes>.  `key` is passed
		// pre-escaped (it already contains entities like &#36; in the capture).
		public static byte[] RemoteFormat(int id, int category, string key)
		{
			var xml = $"<remoteformat id=\"{id}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
			return new FaolanStream().WriteString(xml).ToArray();
		}

		// The inverse of BuildQuestGoal: recovers (playerHandle, questId, goalId, count) from a 33-byte QuestGoal body.
		// Validates the sub-opcode, ack TypeTag, the 0x01 marker and the trailing zero words. Proven by the QuestTests
		// round-trip (#87 quest-packets codec consolidation).
		//
		// Read SEQUENTIALLY through FaolanStream rather than by fixed offsets: the offsets ran 8 -> 13 -> 17 -> 21,
		// and that non-uniform step was the only trace of the single-byte marker sitting between them. Reading in
		// order states the layout instead of encoding it in arithmetic the reader has to diff.
		public static bool TryParseQuestGoal(byte[] body, out uint playerHandle, out uint questId, out uint goalId, out uint count)
		{
			playerHandle = 0;
			questId = 0;
			goalId = 0;
			count = 0;
			if (body is not { Length: 33 })
				return false;

			var r = new FaolanStream(body);
			if (r.ReadUInt32() != (uint)GameServer0X2000Opcodes.QuestGoalIIR)
				return false;
			if (r.ReadUInt32() != EntityClass.Character)
				return false;

			var handle = r.ReadUInt32();
			if (r.ReadByte() != 0x01) // marker
				return false;

			var quest = r.ReadUInt32();
			var goal = r.ReadUInt32();
			var amount = r.ReadUInt32();
			if (r.ReadUInt32() != 0 || r.ReadUInt32() != 0) // trailing zero words
				return false;

			playerHandle = handle;
			questId = quest;
			goalId = goal;
			count = amount;
			return true;
		}

		// The inverse of BuildQuestIir: recovers (playerHandle, state, questId) from a 29-byte QuestIIR body. Validates
		// the sub-opcode, ack TypeTag, the zeroed word before the 0x01 marker, and the trailing zero word.
		public static bool TryParseQuestIir(byte[] body, out uint playerHandle, out uint questId, out uint state)
		{
			playerHandle = 0;
			questId = 0;
			state = 0;
			if (body is not { Length: 29 })
				return false;

			var r = new FaolanStream(body);
			if (r.ReadUInt32() != (uint)GameServer0X2000Opcodes.QuestIIR)
				return false;
			if (r.ReadUInt32() != EntityClass.Character)
				return false;

			var handle = r.ReadUInt32();
			if (r.ReadUInt32() != 0) // zero word
				return false;
			if (r.ReadByte() != 0x01) // marker
				return false;

			var questState = r.ReadUInt32();
			var quest = r.ReadUInt32();
			if (r.ReadUInt32() != 0) // trailing zero word
				return false;

			playerHandle = handle;
			state = questState;
			questId = quest;
			return true;
		}

		// The inverse of BuildQuestLogList: recovers (playerHandle, trackedQuestId, questIds) from a QuestLogList body.
		// The list populates the J-panel - the character's full quest log as the retail server sent it on login - so it
		// is the bulk per-character quest set (many ids in ONE packet), unlike the one-quest QuestIIR/QuestGoal packets.
		//
		// Reading sequentially surfaced two regions the fixed-offset version stepped straight over: the [u32 one=1]
		// [u32 areaId] pair after the marker (offsets 13..21 - areaId is real data this parser DISCARDS; see
		// BuildQuestLogList, which writes it), and the per-entry 1-based ordinal that precedes each quest id.
		public static bool TryParseQuestLogList(byte[] body, out uint playerHandle, out uint trackedQuestId, out List<uint> questIds)
		{
			playerHandle = 0;
			trackedQuestId = 0;
			questIds = null;
			if (body is not { Length: >= 29 })
				return false;

			var r = new FaolanStream(body);
			if (r.ReadUInt32() != (uint)GameServer0X2000Opcodes.QuestLogListIIR)
				return false;
			if (r.ReadUInt32() != EntityClass.Character)
				return false;

			var handle = r.ReadUInt32();
			if (r.ReadByte() != 0x01) // marker
				return false;

			r.ReadUInt32(); // constant 1
			r.ReadUInt32(); // areaId - written by the builder, not surfaced here
			var tracked = r.ReadUInt32();
			var count = r.ReadUInt32();
			if (count > 4096 || HeaderLength + count * EntrySize > body.Length)
				return false; // implausible count / truncated - not a well-formed log list

			var ids = new List<uint>((int)count);
			for (uint i = 0; i < count; i++)
			{
				r.ReadUInt32(); // 1-based ordinal
				ids.Add(r.ReadUInt32());
			}

			playerHandle = handle;
			trackedQuestId = tracked;
			questIds = ids;
			return true;
		}

		private static void WriteHeader(FaolanStream w, uint subOpcode, uint playerHandle)
		{
			w.WriteUInt32(subOpcode);
			w.WriteUInt32(EntityClass.Character);
			w.WriteUInt32(playerHandle);
		}

		// One line in the journal list: the quest id plus its title (cat 50301) and list sub-line (cat 50303).
		public readonly record struct JournalEntry(uint QuestId, byte[] TitleRecord, byte[] SubLineRecord);
	}
}
