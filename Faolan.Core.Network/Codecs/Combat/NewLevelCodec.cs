using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     NewLevelIIR - the level-up event the client processes to draw the "Congratulations, you have reached level N!"
	///     centre-screen banner and play the level-up visual and audio effect. The banner text is client-side: it appears in
	///     no capture, so the client's own level-up handler generates it, gated on this packet. Sending the Level stat and
	///     the AttributeChanged feedback lines alone does not fire it.
	/// </summary>
	/// <remarks>
	///     Carried as an IIR on the world conversation. Big-endian grammar, decoded from the combat-levelup capture
	///     (Blablabla_soldier-guardian..., frame 9937, the level 1 to 2 ding):
	///     <code>
	///     [u32 subop]        the NewLevelIIR sub-opcode
	///     [u32 class]        the Character marker
	///     [u32 instance]     the target GameInterface object, i.e. the receiver endpoint instance
	///     [u8  0x00]         lead/env flag, constant 0 in the capture
	///     [u32 newLevel]     the level just reached
	///     [u32 x6]           the XP summary: [Exp, ExpCurrentLevelBase, MaxExp, 1, 8, 0]
	///     </code>
	///     The XP summary is the same cumulative triple the stat sheet carries, for the level just reached, followed by
	///     three constants. Across the 14 dings in the corpus the first word is the player's own running total and varies
	///     (the level-2 ding reads 100 / 120 / 168 across seven captures) while the two thresholds behind it never do (100
	///     and 340 at every level-2 ding, 340 and 740 at every level-3 ding). Gated by LevelingCurveCorpusTests.
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class NewLevelCodec
	{
		public const int DeltaCount = 6;
		public const uint Opcode = (uint)IirTypes.NewLevelIIR;
		private const byte LeadFlag = 0x00;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(NewLevelIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     <paramref name="deltas" /> is padded or truncated to six words; the live path passes none, giving an all-zero
		///     summary.
		/// </summary>
		public static byte[] Build(uint instance, uint newLevel, ReadOnlySpan<uint> deltas = default)
		{
			var d = new uint[DeltaCount];
			for (var i = 0; i < DeltaCount && i < deltas.Length; i++)
				d[i] = deltas[i];

			return Build(new NewLevelIIR(instance, LeadFlag, newLevel, d));
		}

		public static NewLevelIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(NewLevelCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static NewLevelIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "NewLevelIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var instance = r.ReadU32Be();
			var env = r.ReadU8();
			var newLevel = r.ReadU32Be();

			var deltas = new uint[DeltaCount];
			for (var i = 0; i < DeltaCount; i++)
				deltas[i] = r.ReadU32Be();

			return new NewLevelIIR(instance, env, newLevel, deltas);
		}

		internal static void Write(FaolanStream s, NewLevelIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Instance).WriteByte(v.Env).WriteUInt32(v.NewLevel);
			for (var i = 0; i < DeltaCount; i++)
			{
				s.WriteUInt32(i < v.Deltas.Length
					? v.Deltas[i]
					: 0u);
			}
		}
	}

	/// <summary>The NewLevelIIR body. <c>Deltas</c> is always six words.</summary>
	public sealed record NewLevelIIR(uint Instance, byte Env, uint NewLevel, uint[] Deltas);
}
