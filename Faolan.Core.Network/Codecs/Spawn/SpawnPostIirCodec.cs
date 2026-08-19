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
	// The reader used to try the stream BOTH ways (a "player" and an "NPC" entry-slot carve, end-exactness picking
	// the winner). #466 removed the need: the client reads one slot grammar for both families, so there is nothing
	// to resolve and the parse is a single pass.
	internal static class SpawnPostIirCodec
	{
		internal static SpawnPostIir Read(in ReadOnlySpan<byte> post)
		{
			var r = new SpanReader(post, nameof(SpawnPostIirCodec));
			var value = ReadPrologue(ref r);
			value.Slot = ReadSlot(ref r);

			ReadPairs(ref r, value.EndPairs);
			ReadPairs(ref r, value.EndPairs2);
			var tail = r.ReadListCount();
			for (var n = 0; n < tail; n++)
				value.EndPairs3.Add(new ScaffoldPair(r.ReadU32Be(), r.ReadI32Be()));

			// The stream is end-anchored: a walk that lands on the last byte is a walk that read the whole thing.
			r.EnsureAtEnd();
			return value;
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
			foreach (var group in post.BuffRecords)
			{
				stream.WriteUInt32(group.SpellId).WriteUInt32(ListFrame.ForCount(group.Records.Count));
				foreach (var buff in group.Records)
					WriteBuff(stream, buff);
			}

			WriteSlot(stream, post.Slot);

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
			{
				// The client's carve (#658): one u32 spell id per group, then an INNER ListFrame'd record list -
				// the word the flat model carried as a per-record "Kind" constant.
				var group = new ScaffoldBuffGroup
				{
					SpellId = r.ReadU32Be()
				};
				var records = r.ReadListCount();
				for (var m = 0; m < records; m++)
					group.Records.Add(ReadBuff(ref r));

				value.BuffRecords.Add(group);
			}

			return value;
		}

		private static SpawnEntrySlot ReadSlot(ref SpanReader r)
		{
			var slot = new SpawnEntrySlot();
			var count = r.ReadU32Be();
			if (count > 0x1000)
				throw CodecParseException.Unexpected(nameof(SpawnPostIirCodec), r.Position - 4, "a plausible appearance-entry count", count.ToString());

			for (var n = 0; n < count; n++)
			{
				slot.Appearance.Add(new AppearanceEntry
				{
					MorphId = r.ReadU32Be(),
					Value = r.ReadF32Be(),
					Word2 = r.ReadU32Be(),
					Text = Encoding.Latin1.GetString(r.ReadBytesU16Length())
				});
			}

			// Both closing lists are ListFrame'd, and the client VALIDATES the framing (the word must be an exact
			// multiple of 0x3F1) - ReadListCount throws on anything else, which is the same check.
			var skills = r.ReadListCount();
			for (var n = 0; n < skills; n++)
			{
				var id = r.ReadU32Be();
				slot.Skills.Add(new ScaffoldSkill(id, r.ReadU8()));
			}

			var pets = r.ReadListCount();
			for (var n = 0; n < pets; n++)
			{
				// ⚠️ The third word is a SLOT SELECTOR, not a zero constant - `PetHolder_c::RegisterPet` tests it
				// for 1 and routes that pet to the holder's single primary slot (#466). It is 0 on every captured
				// spawn only because no captured character had a primary pet out, so asserting it would have thrown
				// on a body retail can legitimately send.
				r.ExpectU32Be(EntityClass.Character, $"pet record {n} entity class");
				var petInstance = r.ReadU32Be();
				var slotSelector = r.ReadU32Be();
				slot.Pets.Add(new ScaffoldPetRef(petInstance, r.ReadU8(), slotSelector));
			}

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
				SourceHandle = r.ReadU32Be(),
				Stamp = r.ReadU32Be(),
				TimerTotal = r.ReadU32Be(),
				TimerRemaining = r.ReadU32Be(),
				Word6 = r.ReadU32Be(),
				CasterLevel = r.ReadU32Be(),
				TargetClass = r.ReadU32Be(),
				TargetHandle = r.ReadU32Be()
			};
			for (var w = 0; w < buff.Trailer.Length; w++)
				buff.Trailer[w] = r.ReadU32Be();

			return buff;
		}

		// ---- write -----------------------------------------------------------------------------------------

		private static void WriteBuff(FaolanStream s, ScaffoldBuff buff)
		{
			s
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

		private static void WriteSlot(FaolanStream s, SpawnEntrySlot slot)
		{
			s.WriteUInt32((uint)slot.Appearance.Count);
			foreach (var entry in slot.Appearance)
			{
				s
					.WriteUInt32(entry.MorphId)
					.WriteFloat(entry.Value)
					.WriteUInt32(entry.Word2)
					.WriteArrayUInt16Length(Encoding.Latin1.GetBytes(entry.Text ?? ""));
			}

			// ForCount(0) IS 0x3F1, so an empty list regenerates the exact "constant" it was long read as.
			s.WriteUInt32(ListFrame.ForCount(slot.Skills.Count));
			foreach (var skill in slot.Skills)
				s.WriteUInt32(skill.Id).WriteByte(skill.Rank);

			s.WriteUInt32(ListFrame.ForCount(slot.Pets.Count));
			foreach (var pet in slot.Pets)
				s.WriteUInt32(EntityClass.Character).WriteUInt32(pet.PetInstance).WriteUInt32(pet.SlotSelector).WriteByte(pet.Kind);
		}
	}
}
