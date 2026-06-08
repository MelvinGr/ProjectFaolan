using System.Collections.Generic;
using Faolan.Core.Data;

namespace Faolan.GameServer.Packets._0x737A6DF9_Test
{
	/// <summary>
	///     Builds the count-prefixed stat-record block shared by the <c>0x737A6DF9</c> snapshot and the
	///     <c>0x96B8DC59</c> vitals/stat-update packet. Confirmed wire encoding (see docs/player-stats.md and
	///     docs/quests-and-combat.md): a single-byte record count, then N records of
	///     <c>[uint32 statId BE][uint32 value BE]</c>.
	///     Values are stored at wire scale: vitals (Health/Mana/Stamina) ×100, attributes ×10, everything else ×1
	///     (see <see cref="LegacyCharacterStatEntry.GetScale" />). Use <see cref="Set" /> with the player-facing
	///     value (it applies the scale) or <see cref="SetRaw" /> with the already-scaled wire value.
	/// </summary>
	public sealed class CharacterStatBlock
	{
		private readonly List<(uint StatId, uint RawValue)> _records = [];

		public int Count => _records.Count;

		/// <summary>Adds a record using the player-facing value; the wire scale for the stat is applied.</summary>
		public CharacterStatBlock Set(LegacyCharacterStatId statId, uint displayValue)
		{
			return SetRaw(statId, displayValue * LegacyCharacterStatEntry.GetScale(statId));
		}

		/// <summary>Adds a record using the already-scaled wire value.</summary>
		public CharacterStatBlock SetRaw(LegacyCharacterStatId statId, uint rawValue)
		{
			_records.Add(((uint)statId, rawValue));
			return this;
		}

		public byte[] ToArray()
		{
			using var stream = new FaolanStream();
			WriteTo(stream);
			return stream.ToArray();
		}

		/// <summary>Writes <c>[count:1][N×(statId:u32, value:u32)]</c> (big-endian) into <paramref name="stream" />.</summary>
		public FaolanStream WriteTo(FaolanStream stream)
		{
			stream.WriteByte((byte)_records.Count);
			foreach (var (statId, rawValue) in _records)
				stream.WriteUInt32(statId).WriteUInt32(rawValue);

			return stream;
		}
	}
}
