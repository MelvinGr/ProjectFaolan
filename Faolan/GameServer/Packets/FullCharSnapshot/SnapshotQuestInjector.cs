using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	// Injects a character's accepted quests into the FullChar snapshot (0x737A6DF9) TAILS so they appear in the
	// J-panel ON LOGIN (retail delivers the login quest log via the snapshot, not separate packets - an in-progress
	// login sends ZERO quest packets; docs/quest-log-handover.md). The tails quest region (decoded from captures,
	// `Captures/decode_snapshot_quests.py`):
	//
	//   <per-quest DETAIL block> ...                      # each == the QuestFullUpdate content (questId onward)
	//   [0x000003F1][marker u32][questId u32 × N][N u32]  # the INDEX: N = quest count (== id-run length)
	//   [0x000000F1][0x57365154] ...                      # fixed sentinel onward
	//
	// To add a quest: splice its detail block immediately before the 0x000003F1 index tag, append its id to the
	// id-run, and bump N. The template already carries the beach quest (10238); this adds the rest.
	//
	// IN-CLIENT GATED: a 1-byte length error in a block mis-frames the access-window table that follows the index →
	// broken FullChar (chat/world-load regression, memory protocol_chat_connection_regression). Wired behind the
	// FAOLAN_SNAPSHOT_QUESTS opt-in; the block length is the value to bisect in-client (the captured 13479 block is
	// 879 B = the QuestFullUpdate content minus its trailing 0x00 separator).
	public static class SnapshotQuestInjector
	{
		private const uint IndexTag = 0x000003F1;       // opens the quest index after the detail blocks
		private const uint IndexSentinel = 0x000000F1;  // the fixed word right after the count
		private const uint MinQuestId = 0x1000;         // quest ids are large; the count/sentinel are small

		// Returns `tails` with each (questId, detailBlock) that is not already listed spliced in before the index and
		// added to the id-run (count bumped). Returns `tails` unchanged when there is nothing to add or the index is
		// not found / is shaped unexpectedly (never corrupt a tails we don't fully recognise).
		public static byte[] Inject(byte[] tails, IReadOnlyList<(uint QuestId, byte[] Block)> quests)
		{
			if (quests == null || quests.Count == 0)
				return tails;

			var idx = FindIndexTag(tails);
			if (idx < 0)
				return tails;

			// Parse [0x3F1][marker][id × N][N][0xF1].
			var o = idx + 8; // skip the tag + marker
			var ids = new List<uint>();
			while (o + 4 <= tails.Length && ReadU32(tails, o) >= MinQuestId)
			{
				ids.Add(ReadU32(tails, o));
				o += 4;
			}

			if (o + 8 > tails.Length)
				return tails;

			var count = ReadU32(tails, o);
			var afterCount = o + 4;
			if (count != ids.Count || ReadU32(tails, afterCount) != IndexSentinel)
				return tails; // unrecognised framing - leave the tails untouched

			var toAdd = quests.Where(q => q.Block is { Length: > 0 } && !ids.Contains(q.QuestId)).ToList();
			if (toAdd.Count == 0)
				return tails;

			var marker = ReadU32(tails, idx + 4);
			using var ms = new MemoryStream(tails.Length + toAdd.Sum(q => q.Block.Length) + toAdd.Count * 4);
			ms.Write(tails, 0, idx);                              // detail blocks + everything before the index
			foreach (var q in toAdd)
				ms.Write(q.Block, 0, q.Block.Length);             // new detail blocks, inserted before the index tag
			WriteU32(ms, IndexTag);
			WriteU32(ms, marker);
			foreach (var id in ids)
				WriteU32(ms, id);
			foreach (var q in toAdd)
				WriteU32(ms, q.QuestId);
			WriteU32(ms, (uint)(ids.Count + toAdd.Count));        // bumped count
			ms.Write(tails, afterCount, tails.Length - afterCount); // sentinel onward, verbatim
			return ms.ToArray();
		}

		// First offset of the 0x000003F1 index tag (skips any incidental match inside a detail block by requiring the
		// [tag][marker][id≥0x1000] shape).
		private static int FindIndexTag(byte[] tails)
		{
			for (var i = 0; i + 12 <= tails.Length; i++)
			{
				if (ReadU32(tails, i) == IndexTag && ReadU32(tails, i + 8) >= MinQuestId)
					return i;
			}

			return -1;
		}

		private static uint ReadU32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));

		private static void WriteU32(Stream s, uint v)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(b, v);
			s.Write(b);
		}
	}
}
