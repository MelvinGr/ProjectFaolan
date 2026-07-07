using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
    /// <summary>
    ///     Builds the count-prefixed stat-record block shared by the <c>0x737A6DF9</c> snapshot and the
    ///     <c>0x96B8DC59</c> vitals/stat-update packet. Confirmed wire encoding (see docs/player-stats.md and
    ///     docs/archive/quests-and-combat.md): a single-byte record count, then N records of
    ///     <c>[uint32 statId BE][uint32 value BE]</c>.
    ///     Values are stored at wire scale: vitals (Health/Mana/Stamina) ×100, attributes ×10, everything else ×1
    ///     (see <see cref="CharacterStatIdExtensions.GetScale" />). Use <see cref="Set" /> with the player-facing
    ///     value (it applies the scale) or <see cref="SetRaw" /> with the already-scaled wire value.
    /// </summary>
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

        public int Count => _records.Count;

        /// <summary>The parsed/built records at wire scale, in order ([statId], [rawValue]).</summary>
        public IReadOnlyList<(CharacterStatId StatId, uint RawValue)> Records => _records;

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

        public byte[] ToArray()
        {
            using var stream = new FaolanStream();
            WriteTo(stream);
            return stream.ToArray();
        }

        /// <summary>Writes <c>[count:1][N×(statId:u32, value:u32)]</c> (big-endian) into <paramref name="stream" />.</summary>
        public FaolanStream WriteTo(FaolanStream stream)
        {
            stream.WriteByte(_records.Count);
            foreach (var (statId, rawValue) in _records)
                stream.WriteUInt32(statId).WriteUInt32(rawValue);

            return stream;
        }

        /// <summary>
        ///     The inverse of <see cref="WriteTo" />: parses <c>[count:1][N×(statId:u32, value:u32)]</c> (big-endian)
        ///     from the front of <paramref name="data" />, returning the block and the number of bytes consumed. The
        ///     same codec covers both the <c>0x737A6DF9</c> snapshot and the <c>0x96B8DC59</c> vitals packet. Returns
        ///     false (and <paramref name="consumed" /> = 0) when the buffer is too short for the declared count.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> data, out CharacterStatBlock block, out int consumed)
        {
            block = new CharacterStatBlock();
            consumed = 0;
            if (data.Length < 1)
                return false;

            int count = data[0];
            var needed = 1 + count * 8;
            if (data.Length < needed)
                return false;

            for (var i = 0; i < count; i++)
            {
                var off = 1 + i * 8;
                var statId = (CharacterStatId)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(off, 4));
                var rawValue = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(off + 4, 4));
                block._records.Add((statId, rawValue));
            }

            consumed = needed;
            return true;
        }
    }
}
