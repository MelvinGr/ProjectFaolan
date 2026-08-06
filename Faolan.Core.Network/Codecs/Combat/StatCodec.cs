using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// StatIIR (0xF98E10B3) - sets ONE stat to ONE value on ONE entity, whatever that entity is.
	//
	//   [subop][u32 entityClass][u32 handle][u8 env][u32 statId][u32 value]
	//
	// ⚠️ **This used to be modelled as a spellweave "fire burst" stage** - `[class=Character][caster][env=1][fireId]
	// [trailer=0x00007BD4]`, with the class pinned to Character and the last word written off as a constant. Every
	// part of that is one record misread (#678): the "fire ids" 0x398 / 0x39A / 0x39B / 0x39D are `AttackRating` /
	// `DefenseRating` / `MagicRating` / `ResistRating` in the client's own stat-name table, and the "constant
	// trailer" 31700 is what that level-80 necromancer's four combat ratings happened to BE. Across the corpus the
	// same word takes 100 / 500 / 900 / 1300 / 1700 / 31700 on those very stats, so it is a value; the capture just
	// held one character.
	//
	// The class word is not Character either: 4,087 of the 33,111 captured records target a SimpleDynel (0xC878), a
	// Destructible (0xC879) or a ZoneLabel (0xC748) - a harvest node restating its `HealthCurrent` (0x1B) or a prop
	// its `CarsGroup` (0x41A) as its visual state flips. Pinning the class made every one of those a parse failure.
	//
	// `Env` is the byte the BASE record carries ahead of the subclass payload - the same slot
	// `SimpleDynelVisualChangeCodec` skips (its `BaseTail`). It is 0 on every captured world-object record and 1 on
	// all but 1,136 of the character ones, and nothing separates those two groups, so it is carried rather than
	// derived. [V0: what the base record's byte selects.]
	public static class StatCodec
	{
		public const uint Opcode = (uint)IirTypes.StatIIR;

		// CANONICAL - the only method here that writes bytes.
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

	// StatIIR (0xF98E10B3). `StatId` is a `CharacterStatId` - the same id space on a world object as on a character.
	public sealed record StatIIR(uint EntityClass, uint Handle, byte Env, uint StatId, uint Value);

	// The semantic forms of a stat set, named rather than told apart by their argument list (#530 rule 8).
	public static class StatIirs
	{
		// The base record's byte on a character record, and on a world-object one.
		public const byte CharacterEnv = 0x01;
		public const byte WorldObjectEnv = 0x00;

		// A stat on a player / NPC / creature. The id is a raw uint rather than a `CharacterStatId` because the
		// create-editor bursts walk RUNS of ids (0x0F4285..0x0F428C), most of which the enum does not name.
		public static StatIIR ForCharacter(uint characterId, uint statId, uint value, byte env = CharacterEnv)
		{
			return new StatIIR(EntityClass.Character, characterId, env, statId, value);
		}

		// A stat on a static world object - a dynel, a destructible harvest node, a zone label. `entityClass` is the
		// object's own class word (the one its spawn and its visual changes are addressed by), never Character.
		public static StatIIR ForWorldObject(uint entityClass, uint handle, CharacterStatId stat, uint value)
		{
			return new StatIIR(entityClass, handle, WorldObjectEnv, (uint)stat, value);
		}
	}
}
