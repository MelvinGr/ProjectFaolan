using System;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Combat
{
	// Construct-side codecs for the server->client combat-state records (#216/#218, docs/iir-readers-combat-world.md):
	// the damage result, the combat shield, the threat level and the hate-list update. These are the records the
	// combat FSM broadcasts so onlookers see damage numbers, shields, and aggro. All share the IIR carrier
	// `[u32 len][u32 sub][u32 0xC350][u32 handle][u8 env][reader]` and are big-endian. Golden tests in
	// CombatRecordCodecTests prove byte-exact construction against captured records.

	// CombatShieldIIR_c (0xCFFD0519) - a single u32 shield value on the target (FUN_0113e000).
	public static class CombatShieldCodec
	{
		public const uint SubOpcode = (uint)GameServer0X2000RespondsOpcodes.CombatShieldIIR;
		private const uint IirMarker = EntityClass.Character;

		public static byte[] Build(uint handle, uint value, byte env = 0x00)
		{
			return CharDCMoveCodec.WithLength(new FaolanStream().WriteUInt32(SubOpcode).WriteUInt32(IirMarker).WriteUInt32(handle).WriteByte(env).WriteUInt32(value).ToArray());
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32(); // len, subop, marker
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			return new Parsed(handle, env, s.ReadUInt32());
		}

		public sealed record Parsed(uint Handle, byte Env, uint Value);
	}

	// ThreatLevelIIR_c (0x1745EF6C) - a threat/aggro level (u32) plus a bool (FUN_007a8fd0).
	public static class ThreatLevelCodec
	{
		public const uint SubOpcode = (uint)GameServer0X2000RespondsOpcodes.ThreatLevelIIR;
		private const uint IirMarker = EntityClass.Character;

		public static byte[] Build(uint handle, uint threat, byte flag, byte env = 0x00)
		{
			return CharDCMoveCodec.WithLength(new FaolanStream().WriteUInt32(SubOpcode).WriteUInt32(IirMarker).WriteUInt32(handle).WriteByte(env).WriteUInt32(threat).WriteByte(flag).ToArray());
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var threat = s.ReadUInt32();
			return new Parsed(handle, env, threat, s.ReadByte());
		}

		public sealed record Parsed(uint Handle, byte Env, uint Threat, byte Flag);
	}

	// HateListUpdateIIR (0x8A01E5A1) - a hate/aggro entry: a flag byte then three u32s (FUN_007a93a0); in the capture
	// the trio is [0xC350 marker][target handle][count]. The carrier handle is the hater (the NPC); Target is who it
	// hates.
	public static class HateListCodec
	{
		public const uint SubOpcode = (uint)GameServer0X2000RespondsOpcodes.HateListUpdateIIR;
		private const uint IirMarker = EntityClass.Character;

		public static byte[] Build(uint handle, uint target, uint count = 1, byte flag = 0x00, byte env = 0x00)
		{
			return CharDCMoveCodec.WithLength(new FaolanStream()
				.WriteUInt32(SubOpcode)
				.WriteUInt32(IirMarker)
				.WriteUInt32(handle)
				.WriteByte(env)
				.WriteByte(flag)
				.WriteUInt32(IirMarker)
				.WriteUInt32(target)
				.WriteUInt32(count)
				.ToArray());
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var flag = s.ReadByte();
			return new Parsed(handle, env, flag, s.ReadUInt32(), s.ReadUInt32(), s.ReadUInt32());
		}

		public sealed record Parsed(uint Handle, byte Env, byte Flag, uint Marker, uint Target, uint Count);
	}

	// StatDamageIIR_t (0xE9F24184) - the buff/debuff stat effect on the target (FUN_008d57c0 + the FUN_00897b60 tail).
	// The 17-u32 long form is DECODED: it is the SAME record CombatEffectBuilder.BuildStatDamage field-names and the
	// combat FSM sends at runtime -
	//   [statValue][0][0][0][0x1b][2][i32 delta][statId][0xC350 marker][caster][idx][0][spellBaseId][1][0][0][0]  (#274)
	// This codec keeps them as raw words ONLY for its own byte-exact round-trip test; the runtime path constructs from
	// the named fields via CombatEffectBuilder, so nothing ships verbatim here.
	public static class StatDamageCodec
	{
		public const int WordCount = 17;
		public const uint SubOpcode = (uint)GameServer0X2000RespondsOpcodes.StatDamageIIR;
		private const uint IirMarker = EntityClass.Character;

		public static byte[] Build(uint handle, uint[] words, byte env = 0x00)
		{
			if (words.Length != WordCount)
				throw new ArgumentException($"StatDamage long form is {WordCount} words", nameof(words));

			var c = new FaolanStream().WriteUInt32(SubOpcode).WriteUInt32(IirMarker).WriteUInt32(handle).WriteByte(env);
			foreach (var w in words)
				c.WriteUInt32(w);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var words = new uint[WordCount];
			for (var i = 0; i < WordCount; i++)
				words[i] = s.ReadUInt32();
			return new Parsed(handle, env, words);
		}

		public sealed record Parsed(uint Handle, byte Env, uint[] Words);
	}
}
