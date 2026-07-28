using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// One 60-byte ACTIVE BUFF record in an FA015807 post-IIR stream, an element of the count-framed list that closes
	// the stream's first record. Fifteen u32, decoded over all 941 captured records (Gitea #475) - it was carried as
	// an opaque byte[] described only as "buff/timer state".
	//
	// NOT a player record: it lives in the shared part of the stream (SpawnPostIirCommon) and NPCs carry it too. Nine
	// distinct spells turn up on NPC spawns, all rows in our own Spells table - Stagger, Hearty Meal, Shrink, Frozen
	// Hatred, two necromancer PET summons - which is corroboration from a direction the walk knows nothing about.
	//
	// What the corpus establishes, and what it does not:
	//   SpellId       w0  - THE find: the distinct values are rows in our own Spells catalog, and they read as a buff
	//                       bar - "Hearty Meal", "Focused Mind", "Refresh Strength", "Death Penalty 1", "Spirit of the
	//                       Bear (Rank 1)". That is what identifies the record.
	//   Kind          w1  - ListFrame.ForCount(1) on all 941. ⚠️ Whether that is a CONSTANT or a real one-element
	//                       list count cannot be decided from this corpus: the value never varies, so the record's
	//                       fixed 60-byte width is equally consistent with "a constant" and with "a one-element list
	//                       whose element is the remaining 52 bytes". That is the 0x7E2 trap's exact signature, so it
	//                       is written as ForCount(1) rather than as a magic number, and CARRIED per record - a
	//                       capture with a count of 2 then round-trips and shows up, instead of being rejected.
	//   SourceHandle  w2  - 0 on nearly all; the exceptions carry a value in the same 0x131xxxx range as TargetHandle,
	//                       so it reads as an entity reference that is simply absent for a self-applied buff.
	//   Stamp         w3  - large values (874724..98289218), unrelated to the timer pair. Unknown.
	//   TimerTotal    w4  } TimerRemaining <= TimerTotal on ALL 941 records, which is what makes them a PAIR rather
	//   TimerRemaining w5 } than two independent numbers. Their unit is not pinned; only the ordering is evidence.
	//   CasterLevel   w7  - 0..82 across both families. #438 independently found the caster's LEVEL in the MagicBuff
	//                       payload, which is the same quantity arriving by another route - but 82 is ABOVE the level
	//                       cap, so this corpus does not prove the field IS the level and no bound is asserted on it.
	//   TargetClass   w8  - EntityClass.Character on all 941, and
	//   TargetHandle  w9  - the spawning character's own handle on nearly all: the standard [class][handle] entity
	//                       reference, i.e. who the buff is ON.
	//   Word6, Trailer    - zero on every record. Carried rather than asserted, so a non-zero one round-trips
	//                       instead of being silently dropped.
	public sealed class ScaffoldBuff
	{
		public const int ByteCount = 60;
		public const int TrailerWords = 5;

		// See the Kind note above: ForCount(1) on every captured record, and not provably a constant.
		public static readonly uint DefaultKind = ListFrame.ForCount(1);

		public uint SpellId { get; set; }
		public uint Kind { get; set; } = DefaultKind;
		public uint SourceHandle { get; set; }
		public uint Stamp { get; set; }
		public uint TimerTotal { get; set; }
		public uint TimerRemaining { get; set; }
		public uint Word6 { get; set; }
		public uint CasterLevel { get; set; }
		public uint TargetClass { get; set; } = EntityClass.Character;
		public uint TargetHandle { get; set; }
		public uint[] Trailer { get; set; } = new uint[TrailerWords];

		// Reads one record at `i`, advancing it. The caller has already established that ByteCount bytes are there
		// (the record's count comes from the list frame ahead of it, so this never has to guess).
		public static ScaffoldBuff Read(byte[] b, ref int i)
		{
			var buff = new ScaffoldBuff
			{
				SpellId = b.ReadU32Be(i),
				Kind = b.ReadU32Be(i + 4),
				SourceHandle = b.ReadU32Be(i + 8),
				Stamp = b.ReadU32Be(i + 12),
				TimerTotal = b.ReadU32Be(i + 16),
				TimerRemaining = b.ReadU32Be(i + 20),
				Word6 = b.ReadU32Be(i + 24),
				CasterLevel = b.ReadU32Be(i + 28),
				TargetClass = b.ReadU32Be(i + 32),
				TargetHandle = b.ReadU32Be(i + 36)
			};
			for (var w = 0; w < TrailerWords; w++)
				buff.Trailer[w] = b.ReadU32Be(i + 40 + 4 * w);

			i += ByteCount;
			return buff;
		}

		public void Write(FaolanStream stream)
		{
			stream.WriteUInt32(SpellId).WriteUInt32(Kind).WriteUInt32(SourceHandle).WriteUInt32(Stamp).WriteUInt32(TimerTotal).WriteUInt32(TimerRemaining)
				.WriteUInt32(Word6).WriteUInt32(CasterLevel).WriteUInt32(TargetClass).WriteUInt32(TargetHandle);
			foreach (var word in Trailer)
				stream.WriteUInt32(word);
		}
	}
}
