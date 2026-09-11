using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     StatIIR - sets one stat to one value on one entity, whatever that entity is:
	///     <c>[subop][u32 entityClass][u32 handle][u8 env][u32 statId][u32 value]</c>.
	/// </summary>
	/// <remarks>
	///     Neither the class word nor the final word may be pinned. 4,087 of the 33,111 captured records target a
	///     SimpleDynel, a Destructible or a ZoneLabel - a harvest node restating its HealthCurrent (0x1B) or a prop its
	///     CarsGroup (0x41A) as its visual state flips - so pinning the class to Character turns every one of those into a
	///     parse failure. The value word is likewise a value: on the four combat ratings (AttackRating 0x398, DefenseRating
	///     0x39A, MagicRating 0x39B, ResistRating 0x39D) it takes 100 / 500 / 900 / 1300 / 1700 / 31700 across the corpus,
	///     and a single capture holding one character makes it look constant.
	///     <para>
	///         <c>Env</c> is the byte the base record carries ahead of the subclass payload - the same slot
	///         <see cref="World.SimpleDynelVisualChangeCodec" /> skips. It is 0 on every captured world-object record and 1
	///         on all but 1,136 of the character ones, and nothing separates those two groups, so it is carried rather than
	///         derived. [V0: what the base record's byte selects]
	///     </para>
	/// </remarks>
	public static class StatCodec
	{
		public const uint Opcode = (uint)IirTypes.StatIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(StatIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static StatIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(StatCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static StatIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "StatIIR subop");
			var entityClass = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var statId = r.ReadU32Be();

			return new StatIIR(entityClass, handle, env, statId, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, StatIIR v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(Opcode).WriteUInt32(v.EntityClass).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(v.StatId).WriteUInt32(v.Value);
		}
	}

	/// <summary>
	///     The StatIIR body. <c>StatId</c> is a <see cref="CharacterStatId" />: the same id space on a world object as on a
	///     character.
	/// </summary>
	public sealed record StatIIR(uint EntityClass, uint Handle, byte Env, uint StatId, uint Value);

	/// <summary>The semantic forms of a stat set, named rather than told apart by their argument list.</summary>
	public static class StatIirs
	{
		/// <summary>The base record's byte on a character record, and on a world-object one.</summary>
		public const byte CharacterEnv = 0x01;
		public const byte WorldObjectEnv = 0x00;

		/// <summary>
		///     A stat on a player, NPC or creature. The id is a raw uint rather than a <see cref="CharacterStatId" /> because
		///     the create-editor bursts walk runs of ids (0x0F4285..0x0F428C), most of which the enum does not name.
		/// </summary>
		public static StatIIR ForCharacter(uint characterId, uint statId, uint value, byte env = CharacterEnv)
		{
			return new StatIIR(EntityClass.Character, characterId, env, statId, value);
		}

		/// <summary>
		///     A stat on a static world object - a dynel, a destructible harvest node, a zone label.
		///     <paramref name="entityClass" /> is the object's own class word, the one its spawn and its visual changes are
		///     addressed by, and never Character.
		/// </summary>
		public static StatIIR ForWorldObject(uint entityClass, uint handle, CharacterStatId stat, uint value)
		{
			return new StatIIR(entityClass, handle, WorldObjectEnv, (uint)stat, value);
		}
	}
}
