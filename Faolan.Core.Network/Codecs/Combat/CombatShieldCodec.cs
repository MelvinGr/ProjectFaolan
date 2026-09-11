using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     CombatShieldIIR - the directional-combat guard broadcast, an entity's blue shield-arc distribution
	///     (client reader FUN_0113e000).
	/// </summary>
	/// <remarks>
	///     Semantics from the client's Activate (FUN_0113de90, vtable 0x02041910 slot 2): the u32 is stored as the entity's
	///     stat 0xA5 (165), which the shield UI (CombatRoseView_c / ShieldDirection_n) reads to draw the arcs, and it is
	///     decimal-packed per direction - hundreds Left, tens Mid, units Right, so 210 is two left, one mid, none right. The
	///     client clamps a digit sum above 3 back to 111.
	///     <para>
	///         Capture-confirmed as well: all 86 captured records are 21 bytes
	///         ([u32 len=17][subop][class][handle][u8 env=0][u32 value]) and carry real distributions - 111, 102, 210, 21,
	///         12, 30, 120, 3 - each decoding to three slices summing to exactly 3.
	///     </para>
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class CombatShieldCodec
	{
		/// <summary>
		///     The default the client's own overflow clamp resets stat 0xA5 to: one shield per arc. It is also the most common
		///     captured value, 17 of 86.
		/// </summary>
		public const uint DefaultDistribution = 111;
		public const uint Opcode = (uint)IirTypes.CombatShieldIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(CombatShieldIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, uint value, byte env = 0x00)
		{
			return Build(new CombatShieldIIR(handle, env, value));
		}

		public static CombatShieldIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CombatShieldCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CombatShieldIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CombatShieldIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new CombatShieldIIR(handle, env, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, CombatShieldIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(v.Value);
		}
	}

	/// <summary>The CombatShieldIIR body.</summary>
	public sealed record CombatShieldIIR(uint Handle, byte Env, uint Value);

	/// <summary>
	///     The three guard arcs the client's shield rose draws, in the digit order the packed value uses. Named for the
	///     client's own ShieldDirection_e / Combat_Shield_{Left,Mid,Right}; the LDB renders them ShieldLeft%1% /
	///     ShieldUp%1% / ShieldRight%1%, so Mid is the overhead arc.
	/// </summary>
	public enum ShieldArc
	{
		Left,
		Mid,
		Right
	}

	/// <summary>
	///     The five zones a swing comes from - a finer alphabet than the three arcs that guard them. This is the client's
	///     own enum, in its own order.
	/// </summary>
	/// <remarks>
	///     Recovered three ways from AgeOfConan.exe:
	///     <list type="bullet">
	///         <item>
	///             FUN_005e3b30 maps an internal 1..5 direction to the wire code - 1 to 0xC9, 2 to 0xCA, default to 0xCB, 4 to
	///             0xCC, 5 to 0xCD. A direction of 10 means "pick one" (<c>rand() % 6 + 1</c>), so overhead, being cases 3 and
	///             6, draws double.
	///         </item>
	///         <item>
	///             The config keys it registers are attack_dir_bm_upper_left / _upper_right / _head / _lower_left /
	///             _lower_right, in that block order (FUN_0152f740) - the same 1..5.
	///         </item>
	///         <item>
	///             FUN_005edd90, the combo step-list parser, rewrites 0xCC to 0xC9 and 0xCD to 0xCA when the definition's
	///             level is below 40, folding each lower zone onto the upper one on the same side. That is the retail mechanic
	///             - three directions to start with, the two lower ones unlocked later - and it pins which side each lower
	///             code is on independently of the order argument.
	///         </item>
	///     </list>
	///     The first three are independently pinned to their Abilities rows by the capture corpus, with no exceptions over
	///     499 directed player swings.
	/// </remarks>
	public enum SwingDirection
	{
		UpperLeft,
		UpperRight,
		Overhead,
		LowerLeft,
		LowerRight
	}

	/// <summary>
	///     The decimal packing of the guard value. Not encoding but the client's own convention for how one field's digits
	///     mean three arcs, so it stays off the codec's surface.
	/// </summary>
	public static class CombatShieldDistribution
	{
		/// <summary>
		///     Packs three arc counts into the wire value. The digit sum must not exceed 3, or the client clamps the whole
		///     value back to <see cref="CombatShieldCodec.DefaultDistribution" />.
		/// </summary>
		public static uint Pack(uint left, uint mid, uint right)
		{
			return left * 100 + mid * 10 + right;
		}

		public static (uint Left, uint Mid, uint Right) Unpack(uint value)
		{
			return (value / 100 % 10, value / 10 % 10, value % 10);
		}

		/// <summary>How many shields cover one arc.</summary>
		public static uint Count(uint value, ShieldArc arc)
		{
			var (left, mid, right) = Unpack(value);
			return arc switch
			{
				ShieldArc.Left => left,
				ShieldArc.Mid => mid,
				_ => right
			};
		}
	}
}
