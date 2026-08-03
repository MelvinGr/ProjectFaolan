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

		/// <summary>
		///     Announces a character's level: <see cref="CharacterStatId.Level" />, plus the
		///     <see cref="CharacterStatId.ApprenticeLevel" /> that has to be RE-STATED alongside it.
		/// </summary>
		/// <remarks>
		///     <b>ApprenticeLevel is not a second copy of the level.</b> It is the apprentice/mentor system's
		///     EFFECTIVE level (#144) - the level a mentored character fights at, which is not its own. We do not
		///     model mentoring, so for every character we serve the two happen to be equal, and this helper exists
		///     precisely because that coincidence has to be MAINTAINED on a level change rather than assumed.
		///     <para>
		///         Two client behaviours make a stale 0x3C5 visible. The over-head nameplate does not render the
		///         Level stat at all: it renders <c>max(GetStat(0x36), GetStat(0x3C5))</c> - the nameplate text
		///         builder (<c>FUN_011d8b00</c>) reads both, takes the larger, and formats it with <c>" (%d)"</c>,
		///         which is exactly right for a mentored character and wrong for a stale one. And the client DIVIDES
		///         by 0x3C5 in its armour-mitigation term <c>2.0 - Level/ApprenticeLevel</c> (see
		///         <see cref="CharacterStatId.ApprenticeLevel" />).
		///     </para>
		///     <para>
		///         So pushing Level alone makes the two readouts disagree on a level DOWN: `.level 10` on a level-23
		///         character left the character SHEET (which reads 0x36 alone) at 10 and the nameplate at 23, because
		///         the ApprenticeLevel seeded at world entry still said 23 and won the max (#472). It is not a
		///         missing refresh - the nameplate SUBSCRIBES to 0x36/0x3C5/0x46/0xD7 and rebuilds on any of them
		///         (<c>FUN_011d79e0</c> registers the watchers, <c>FUN_011d9490</c> is the callback); it rebuilt
		///         correctly and re-read the same stale max.
		///     </para>
		///     <para>
		///         ⚠️ When mentoring IS modelled, this helper is the thing to revisit: a mentored character's
		///         announce must carry its own Level and the mentor-derived ApprenticeLevel separately, and the
		///         nameplate showing the higher of the two stops being a bug. ⚠️ <c>SimpleChar_t::SetStat</c> also
		///         CLAMPS 0x3C5 back to the Level stat for a value &lt; 1 or &gt; 80 on a non-GM client, so past 80
		///         the client re-derives it on its own.
		///     </para>
		/// </remarks>
		public CharacterStatBlock SetLevel(uint level)
		{
			return Set(CharacterStatId.Level, level).Set(CharacterStatId.ApprenticeLevel, level);
		}
	}
}
