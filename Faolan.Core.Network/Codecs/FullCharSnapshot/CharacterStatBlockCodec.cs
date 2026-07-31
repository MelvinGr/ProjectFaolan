using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The count-prefixed stat-record block shared by the 0x737A6DF9 snapshot and the 0x96B8DC59 vitals/stat-update
	// packet. Confirmed wire encoding (docs/subsystems/player-stats.md, docs/re/quests-and-combat.md):
	//
	//   [u8 recordCount][ N x ([u32 statId][u32 value]) ]
	//
	// Values are at WIRE scale: vitals (Health/Mana/Stamina) x100, attributes x10, everything else x1 - see
	// CharacterStatIdExtensions.GetScale. The value's Set/SetRaw pair is where that conversion happens; this codec
	// only moves the words.
	public static class CharacterStatBlockCodec
	{
		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CharacterStatBlock value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static CharacterStatBlock Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharacterStatBlockCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CharacterStatBlock Read(ref SpanReader r)
		{
			var count = r.ReadU8();
			var block = new CharacterStatBlock();
			for (var i = 0; i < count; i++)
				block.SetRaw((CharacterStatId)r.ReadU32Be(), r.ReadU32Be());

			return block;
		}

		internal static void Write(FaolanStream s, CharacterStatBlock v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteByte(v.Records.Count);
			foreach (var (statId, rawValue) in v.Records)
				s.WriteUInt32(statId).WriteUInt32(rawValue);
		}
	}
}
