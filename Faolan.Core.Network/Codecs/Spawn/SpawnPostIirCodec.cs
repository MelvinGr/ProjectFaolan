using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Reads / writes the FA015807 post-IIR stream (SpawnPostIir) byte-exact, for BOTH families. See SpawnPostIir for
	// the grammar; this file is the one place either family's stream is encoded or decoded.
	//
	// A SUB-RECORD codec (#530 §2 rule 5c): composed inside SimpleCharFullUpdateCodec, so it declares no opcode.
	//
	// ⚠️ Its reader takes the WHOLE remaining stream rather than a `ref SpanReader`, deliberately: the grammar is
	// END-ANCHORED. The entry slot is the one region whose reading depends on the family, and what proves the slot
	// was read as the right element is that the three closing pair lists land on the stream's LAST byte - a
	// mis-parsed slot lands somewhere else. So the reader needs the end, and it is the end-exactness (not a per-field
	// guess) that resolves the family.
	internal static class SpawnPostIirCodec
	{
		internal static SpawnPostIir Read(in ReadOnlySpan<byte> post)
		{
			// The move-task slot's presence is what tells the two families apart: an NPC's first record carries one
			// (5 bytes on a fresh spawn), a player's first record IS the buff frame (4 bytes). A stream whose entry
			// slot is EMPTY reads identically either way - the same twelve bytes - so the fallback below only ever
			// fires on a genuinely mixed-up classification, and when it does it is the end-exactness that decides.
			var probe = new SpanReader(post, nameof(SpawnPostIirCodec));
			var prologue = ReadPrologue(ref probe);

			var first = prologue.HasMoveTaskSlot;
			try
			{
				return ReadFrom(post, first);
			}
			catch (CodecParseException)
			{
				return ReadFrom(post, !first);
			}
		}

		internal static void Write(FaolanStream stream, SpawnPostIir post)
		{
			using var record = new FaolanStream();
			if (post.HasMoveTaskSlot)
			{
				record.WriteByte(post.MoveTask != null
					? (byte)1
					: (byte)0);
				if (post.MoveTask != null)
					MoveTaskNodeCodec.Write(record, post.MoveTask);
			}

			var slot = record.ToArray();

			// The header's own first-record length is DERIVED from what the record turned out to be, rather than
			// carried beside it - two sources of truth for one width is how the fixed-width readers went wrong.
			stream.WriteUInt32((uint)post.HeadWords.Count);
			foreach (var word in post.HeadWords)
				stream.WriteUInt32(word);

			stream.WriteUInt32((uint)slot.Length + 4).WriteUInt32(post.MovementMode).WriteArray(slot).WriteUInt32(ListFrame.ForCount(post.BuffRecords.Count));
			foreach (var buff in post.BuffRecords)
				WriteBuff(stream, buff);

			switch (post.Slot)
			{
				case PlayerEntrySlot player:
				{
					WritePlayerSlot(stream, player);
					break;
				}

				case NpcEntrySlot npc:
				{
					WriteNpcSlot(stream, npc);
					break;
				}

				default:
				{
					throw new ArgumentOutOfRangeException(nameof(post), post.Slot?.GetType().Name, "unknown spawn entry slot");
				}
			}

			foreach (var pairs in new[] { post.EndPairs, post.EndPairs2 })
			{
				stream.WriteUInt32((uint)pairs.Count);
				foreach (var pair in pairs)
					stream.WriteUInt32(pair.Id).WriteUInt32((uint)pair.Value);
			}

			// ForCount(0) IS 0x3F1, so an empty third list regenerates the exact closing word it always did.
			stream.WriteUInt32(ListFrame.ForCount(post.EndPairs3.Count));
			foreach (var pair in post.EndPairs3)
				stream.WriteUInt32(pair.Id).WriteUInt32((uint)pair.Value);
		}

		// ---- read ------------------------------------------------------------------------------------------

		private static SpawnPostIir ReadFrom(in ReadOnlySpan<byte> post, bool npcSlot)
		{
			var r = new SpanReader(post, nameof(SpawnPostIirCodec));
			var value = ReadPrologue(ref r);
			value.Slot = npcSlot
				? ReadNpcSlot(ref r)
				: ReadPlayerSlot(ref r);

			ReadPairs(ref r, value.EndPairs);
			ReadPairs(ref r, value.EndPairs2);
			var tail = r.ReadListCount();
			for (var n = 0; n < tail; n++)
				value.EndPairs3.Add(new ScaffoldPair(r.ReadU32Be(), r.ReadI32Be()));

			// The end-exactness is the check doing the real work in this grammar: a mis-parsed entry slot lands
			// somewhere else, so a walk that ends on the stream's last byte is a walk that read the whole thing.
			r.EnsureAtEnd();
			return value;
		}

		// The header, the first record and the buff list - everything ahead of the entry slot.
		private static SpawnPostIir ReadPrologue(ref SpanReader r)
		{
			var value = new SpawnPostIir();
			var headCount = r.ReadU32Be();
			if (headCount > 0x1000)
				throw CodecParseException.Unexpected(nameof(SpawnPostIirCodec), r.Position - 4, "a plausible head-word count", headCount.ToString());

			for (var i = 0; i < headCount; i++)
				value.HeadWords.Add(r.ReadU32Be());

			value.FirstRecordLen = r.ReadU32Be();
			value.MovementMode = r.ReadU32Be();
			if (value.FirstRecordLen < 4)
				throw CodecParseException.Unexpected(nameof(SpawnPostIirCodec), r.Position - 8, "a first-record length that can hold the buff list's framing word", value.FirstRecordLen.ToString());

			// The move-task slot, if the record has one. It must consume EXACTLY the bytes ahead of the buff frame -
			// that is what says the slot was read as the right thing rather than skipped over.
			var slotLength = (int)value.FirstRecordLen - 4;
			if (slotLength > 0)
			{
				value.HasMoveTaskSlot = true;
				var slot = new SpanReader(r.ReadBytes(slotLength), nameof(SpawnPostIirCodec));
				var flag = slot.ReadU8();
				if (flag == 1)
					value.MoveTask = MoveTaskNodeCodec.Read(ref slot);
				else if (flag != 0)
					throw CodecParseException.Unexpected(nameof(SpawnPostIirCodec), r.Position - slotLength, "a move-task presence byte of 0 or 1", flag.ToString());

				slot.EnsureAtEnd();
			}

			var count = r.ReadListCount();
			for (var n = 0; n < count; n++)
				value.BuffRecords.Add(ReadBuff(ref r));

			return value;
		}

		private static PlayerEntrySlot ReadPlayerSlot(ref SpanReader r)
		{
			var slot = new PlayerEntrySlot();
			var count = r.ReadU32Be();
			if (count > 0x1000)
				throw CodecParseException.Unexpected(nameof(SpawnPostIirCodec), r.Position - 4, "a plausible entry count", count.ToString());

			slot.EntryTableX = r.ReadU32Be();
			if (count == 0)
			{
				// The ID list exists iff the entry table does; otherwise a bare list word stands in for it.
				slot.Marker = r.ReadU32Be();
				return slot;
			}

			r.ExpectU32Be(PlayerEntrySlot.EntryWeightOne, "entry-table weight (1.0)");
			for (var n = 0; n < count; n++)
			{
				var index = r.ReadU32Be();
				var flag = r.ReadU32Be();
				var tag = r.ReadU16Be();
				slot.Entries.Add(new ScaffoldEntry(index, tag, r.ReadU32Be(), flag));
			}

			while (true)
			{
				var rank = r.ReadU8();
				var at = r.Position;
				var id = r.ReadU32Be();

				// The closing group's "id" is a count-framed LIST WORD, not the constant 0x3F1 it was read as for
				// years - 0x3F1 is simply ForCount(0). Ids ascend, so a value that frames AND is not larger than the
				// last real id is the close, never a member. The end-exactness check is what ultimately proves the
				// choice: guess wrong and the walk does not land on the stream's end.
				if (ListFrame.TryCountOf(id, out var tailCount) && (slot.IdList.Count == 0 || id < slot.IdList[^1].Id))
				{
					// The terminator's rank byte is USUALLY zero and used to be asserted as such; 23 captured spawns
					// carry a non-zero one and still consume to exactly the body's end, so it is carried.
					slot.IdListTerminatorRank = rank;
					for (var n = 0; n < tailCount; n++)
					{
						r.ExpectU32Be(EntityClass.Character, $"ID-list tail record {n} entity class");
						var refId = r.ReadU32Be();
						r.ExpectU32Be(0, $"ID-list tail record {n} zero field");
						slot.IdListTail.Add(new ScaffoldTailRef(refId, r.ReadU8()));
					}

					return slot;
				}

				// Ids ascend. Kept as a hard failure: it is what exposed the trap above, and a descending id that
				// does NOT frame means the walk diverged earlier and should say so rather than read on.
				if (slot.IdList.Count > 0 && id < slot.IdList[^1].Id)
					throw CodecParseException.Unexpected(nameof(SpawnPostIirCodec), at, "an ascending ID-list id", $"0x{id:X8}");

				slot.IdList.Add(new ScaffoldIdEntry(rank, id));
			}
		}

		private static NpcEntrySlot ReadNpcSlot(ref SpanReader r)
		{
			var slot = new NpcEntrySlot();
			var count = r.ReadU32Be();
			if (count > 0x100)
				throw CodecParseException.Unexpected(nameof(SpawnPostIirCodec), r.Position - 4, "a plausible morph count", count.ToString());

			for (var n = 0; n < count; n++)
			{
				slot.Morphs.Add(new NpcAppearanceMorph
				{
					MorphId = r.ReadU32Be(),
					Value = r.ReadF32Be(),
					Word2 = r.ReadU32Be(),
					Flag = r.ReadU8(),
					Text = Encoding.Latin1.GetString(r.ReadBytesU8Length())
				});
			}

			slot.EntryTableX = r.ReadU32Be();
			slot.EntryTableMarker = r.ReadU32Be();
			return slot;
		}

		// A PLAIN-counted pair list: a bare count, not a ListFrame word. The one list spelling ListFrame does not
		// cover, and the reason the head-word list at the top of the header was misread as a constant too.
		private static void ReadPairs(ref SpanReader r, List<ScaffoldPair> pairs)
		{
			var at = r.Position;
			var count = r.ReadU32Be();
			if (count > 0x100)
				throw CodecParseException.Unexpected(nameof(SpawnPostIirCodec), at, "a plausible closing-pair count", count.ToString());

			for (var n = 0; n < count; n++)
				pairs.Add(new ScaffoldPair(r.ReadU32Be(), r.ReadI32Be()));
		}

		private static ScaffoldBuff ReadBuff(ref SpanReader r)
		{
			var buff = new ScaffoldBuff
			{
				SpellId = r.ReadU32Be(),
				Kind = r.ReadU32Be(),
				SourceHandle = r.ReadU32Be(),
				Stamp = r.ReadU32Be(),
				TimerTotal = r.ReadU32Be(),
				TimerRemaining = r.ReadU32Be(),
				Word6 = r.ReadU32Be(),
				CasterLevel = r.ReadU32Be(),
				TargetClass = r.ReadU32Be(),
				TargetHandle = r.ReadU32Be()
			};
			for (var w = 0; w < ScaffoldBuff.TrailerWords; w++)
				buff.Trailer[w] = r.ReadU32Be();

			return buff;
		}

		// ---- write -----------------------------------------------------------------------------------------

		private static void WriteBuff(FaolanStream s, ScaffoldBuff buff)
		{
			s
				.WriteUInt32(buff.SpellId)
				.WriteUInt32(buff.Kind)
				.WriteUInt32(buff.SourceHandle)
				.WriteUInt32(buff.Stamp)
				.WriteUInt32(buff.TimerTotal)
				.WriteUInt32(buff.TimerRemaining)
				.WriteUInt32(buff.Word6)
				.WriteUInt32(buff.CasterLevel)
				.WriteUInt32(buff.TargetClass)
				.WriteUInt32(buff.TargetHandle);
			foreach (var word in buff.Trailer)
				s.WriteUInt32(word);
		}

		private static void WritePlayerSlot(FaolanStream s, PlayerEntrySlot slot)
		{
			s.WriteUInt32((uint)slot.Entries.Count).WriteUInt32(slot.EntryTableX);
			if (slot.Entries.Count == 0)
			{
				s.WriteUInt32(slot.Marker);
				return;
			}

			s.WriteUInt32(PlayerEntrySlot.EntryWeightOne);
			foreach (var entry in slot.Entries)
				s.WriteUInt32(entry.Index).WriteUInt32(entry.Flag).WriteUInt16(entry.Tag).WriteUInt32(entry.Value);

			foreach (var entry in slot.IdList)
				s.WriteByte(entry.Rank).WriteUInt32(entry.Id);

			// ForCount(0) IS 0x3F1, so a spawn with an empty tail regenerates the exact byte it always did.
			s.WriteByte(slot.IdListTerminatorRank).WriteUInt32(ListFrame.ForCount(slot.IdListTail.Count));
			foreach (var tail in slot.IdListTail)
				s.WriteUInt32(EntityClass.Character).WriteUInt32(tail.Id).WriteUInt32(0).WriteByte(tail.Kind);
		}

		private static void WriteNpcSlot(FaolanStream s, NpcEntrySlot slot)
		{
			s.WriteUInt32(slot.Morphs.Count);
			foreach (var morph in slot.Morphs)
			{
				s
					.WriteUInt32(morph.MorphId)
					.WriteFloat(morph.Value)
					.WriteUInt32(morph.Word2)
					.WriteByte(morph.Flag)
					.WriteArrayByteLength(Encoding.Latin1.GetBytes(morph.Text ?? ""));
			}

			s.WriteUInt32(slot.EntryTableX).WriteUInt32(slot.EntryTableMarker);
		}
	}
}
