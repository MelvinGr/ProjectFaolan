using System;
using Faolan.Core.Extensions;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     A codec's strict `Parse`, as used by an importer sweep over a mixed stream: the sweep dispatches on the
	///     packet's sub-opcode first and only then calls the codec, so `Parse` stays strict and the sweep still gets to
	///     say "not mine".
	/// </summary>
	/// <remarks>
	///     What remains after the opcode filter is a truncated capture - a body the dump cut short, or one whose
	///     trailing bytes are the next record's. That is the only failure <see cref="CapturedRecords.TryParse{T}" />
	///     swallows: a corpus sweep that threw on one short packet would abandon the rest. It is deliberately not a
	///     second decoder - the codec is still the only thing reading the bytes - and it is not a licence to skip a
	///     packet whose grammar is wrong: a codec that throws on a whole capture's worth of a type is a decode gap,
	///     and the module's row count going to zero is what surfaces it.
	/// </remarks>
	internal delegate T RecordParser<T>(in ReadOnlySpan<byte> data);

	internal static class CapturedRecords
	{
		/// <summary>
		///     The IIR record inside a whole captured packet - `[len4][crc4][headerLen4][header][u32 len][record]`. The
		///     one place that walk lives; `CapturedPacket.Record` is this, and so is every reader handed loose bytes.
		///     Empty when the packet cannot carry a record.
		/// </summary>
		public static ReadOnlySpan<byte> Record(byte[]? packet)
		{
			if (packet == null || packet.Length < 12)
				return default;

			var start = 12 + (int)packet.ReadU32Be(8);
			if (start < 12 || start + 4 > packet.Length)
				return default;

			var declared = (int)packet.ReadU32Be(start);
			var available = packet.Length - (start + 4);

			// Bounded by the declared length: a record that declares less than the body holds has a tail that is not
			// its own, and a codec's EnsureAtEnd() would (rightly) reject it.
			return packet.AsSpan(start + 4, declared >= 0 && declared < available ? declared : available);
		}

		/// <summary>
		///     Parses this carrier's IIR record with <paramref name="parse" />, or returns false when the capture cut it
		///     short.
		/// </summary>
		public static bool TryParse<T>(this CapturedPacket packet, RecordParser<T> parse, out T value)
		{
			try
			{
				value = parse(packet.Record);
				return true;
			}
			catch (CodecParseException)
			{
				value = default!;
				return false;
			}
		}
	}
}
