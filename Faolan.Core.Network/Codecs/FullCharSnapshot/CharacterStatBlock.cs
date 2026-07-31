using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The decoded stat-record block <see cref="CharacterStatBlockCodec" /> reads and writes: an ordered list of
	///     <c>(statId, rawValue)</c> records at WIRE scale - vitals (Health/Mana/Stamina) ×100, attributes ×10,
	///     everything else ×1 (see <see cref="CharacterStatIdExtensions.GetScale" />). Use <see cref="Set" /> with the
	///     player-facing value (it applies the scale) or <see cref="SetRaw" /> with the already-scaled wire value.
	/// </summary>
	/// <remarks>
	///     A plain value: it does not serialize itself (#530 rule 1). <see cref="CharacterStatBlockCodec.Build" /> is
	///     how it becomes bytes.
	/// </remarks>
	public sealed class CharacterStatBlock
	{
		private readonly List<(CharacterStatId StatId, uint RawValue)> _records = [];

		/// <summary>
		///     Indexer accessor by stat id. The getter returns the player-facing value (0 if the stat is absent); the
		///     setter adds-or-updates the record, applying the wire scale like <see cref="Set" />. For fluent chained
		///     building keep using <see cref="Set" />/<see cref="SetRaw" />; for the raw wire value use <see cref="Records" />.
		/// </summary>
		public uint this[CharacterStatId statId]
		{
			get
			{
				foreach (var (id, raw) in _records)
				{
					if (id == statId)
						return raw / statId.GetScale();
				}

				return 0;
			}
			set
			{
				var raw = value * statId.GetScale();
				for (var i = 0; i < _records.Count; i++)
				{
					if (_records[i].StatId == statId)
					{
						_records[i] = (statId, raw);
						return;
					}
				}

				_records.Add((statId, raw));
			}
		}

		/// <summary>The records at wire scale, in order ([statId], [rawValue]).</summary>
		public IReadOnlyList<(CharacterStatId StatId, uint RawValue)> Records => _records;

		public int Count => _records.Count;

		/// <summary>Adds a record using the player-facing value; the wire scale for the stat is applied.</summary>
		public CharacterStatBlock Set(CharacterStatId statId, uint displayValue)
		{
			return SetRaw(statId, displayValue * statId.GetScale());
		}

		/// <summary>Adds a record using the already-scaled wire value.</summary>
		public CharacterStatBlock SetRaw(CharacterStatId statId, uint rawValue)
		{
			_records.Add((statId, rawValue));
			return this;
		}
	}
}
