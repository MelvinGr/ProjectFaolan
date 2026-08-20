using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchUpdateSprintbar(energyValue, forceUpdate) - GameCharInterfaceProject 0x0E. The yellow ENERGY bar's
	// value, the pool sprint drains and spellweaving burns (#867; the weave half is docs/subsystems/combos.md).
	//
	// Body is `[u32 energyValue][u8 forceUpdate]` - five bytes. Corpus (`--report sprint`): 509 frames over 13
	// captures, 251 distinct values, and the trailing byte is 01 on every one of them. The two modal values are
	// 0x2328 = 9,000 and 0x36B0 = 14,000, which are class ENERGY maxima (SpellweaveCatalog), so this is an absolute
	// value rather than a delta.
	//
	// ⚠️ `energyValue` is big-endian like every other raw-body integer in this protocol - it is not a protobuf field.
	public static class SprintBarCodec
	{
		public const uint Method = (uint)GameCharInterfaceProjectMethods.DispatchUpdateSprintbar;

		public static byte[] Build(SprintBar value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		// The only shape the corpus carries: a value with forceUpdate set.
		public static byte[] Build(uint energyValue)
		{
			return Build(new SprintBar(energyValue, true));
		}

		public static SprintBar Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SprintBarCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static SprintBar Read(ref SpanReader r)
		{
			var energy = r.ReadU32Be();

			return new SprintBar(energy, r.ReadU8() != 0);
		}

		internal static void Write(FaolanStream s, SprintBar v)
		{
			s.WriteUInt32(v.EnergyValue);
			s.WriteByte(v.ForceUpdate ? (byte)0x01 : (byte)0x00);
		}
	}

	public sealed record SprintBar(uint EnergyValue, bool ForceUpdate);
}
