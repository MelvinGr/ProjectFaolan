using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// StatUpdateIIR (0x96B8DC59) - the moment-to-moment counterpart of the big 0x737A6DF9 snapshot's stat block,
	// pushed whenever an entity's stats/vitals change (combat, the regen tick, the world-entry replay, GM commands).
	// Confirmed wire layout (docs/re/quests-and-combat.md):
	//   [subop 96B8DC59][0000C350][entityHandle:4][00000000:4][CharacterStatBlock]
	// This is the pure IIR body only - no RPC header / length prefix (#530 rule 2). The host frames it per recipient
	// (VitalsPacket for a single addressed send, SendWorldIir when broadcasting to nearby viewers). Proven
	// byte-exact by the round-trip in CharacterStatBlockTests.
	public static class StatUpdateCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.StatUpdateIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(StatUpdateIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint entityHandle, CharacterStatBlock stats)
		{
			return Build(new StatUpdateIIR(entityHandle, stats));
		}

		public static StatUpdateIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(StatUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static StatUpdateIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "StatUpdateIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var entityHandle = r.ReadU32Be();
			r.ExpectU32Be(0, "padding word");

			return new StatUpdateIIR(entityHandle, CharacterStatBlockCodec.Read(ref r));
		}

		internal static void Write(FaolanStream s, StatUpdateIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.EntityHandle).WriteUInt32(0x00000000);
			CharacterStatBlockCodec.Write(s, v.Stats);
		}
	}

	// StatUpdateIIR (0x96B8DC59).
	public sealed record StatUpdateIIR(uint EntityHandle, CharacterStatBlock Stats);
}
