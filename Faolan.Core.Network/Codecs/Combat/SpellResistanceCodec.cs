using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     DispatchAddSpellResistance(metaType, percentage) / DispatchRemoveSpellResistance(metaType, percentage) - a
	///     per-character resistance to one spell metatype. The body is eight bytes,
	///     <c>[u32 metaType][u32 percentage]</c>, and both methods carry the same pair with the verb in the method, so they
	///     share one codec through <see cref="HandledMethods" />. The field names are the client's own signature.
	/// </summary>
	/// <remarks>
	///     This is a third registry alongside the stat sheet and the buff list: a resistance is added and removed by its own
	///     message rather than riding a StatIIR or a MagicBuff, so a character's resistances do not appear in either of the
	///     places the rest of their state lives.
	///     <para>
	///         <c>metaType</c> is an id into LDB category 55200, the client's spell-effect metatype catalog - the same
	///         namespace NanoResistance's first slot uses (docs/rdb/spell-tooltip.md). The corpus carries exactly two, both
	///         at 100%, which is immunity: 427 "Knockback" and 2005 "Criminal Team".
	///     </para>
	/// </remarks>
	public static class SpellResistanceCodec
	{
		public static readonly GameCharInterfaceMethods[] HandledMethods =
		[
			GameCharInterfaceMethods.DispatchAddSpellResistance,
			GameCharInterfaceMethods.DispatchRemoveSpellResistance
		];

		public static byte[] Build(SpellResistance value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint metaType, uint percentage)
		{
			return Build(new SpellResistance(metaType, percentage));
		}

		public static SpellResistance Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SpellResistanceCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static SpellResistance Read(ref SpanReader r)
		{
			var metaType = r.ReadU32Be();

			return new SpellResistance(metaType, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, SpellResistance v)
		{
			s.WriteUInt32(v.MetaType).WriteUInt32(v.Percentage);
		}
	}

	public sealed record SpellResistance(uint MetaType, uint Percentage);
}
