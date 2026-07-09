using System;
using System.IO;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using ProtoBuf;
using Xunit;

namespace Faolan.Tests
{
	// #244 gate: a captured stat-core sub-message (CharacterStats/NpcStats/CharacterBcc) must survive the
	// NpcStatCoreCodec decompose->compose round-trip and re-serialise byte-for-byte - the guarantee that lets NPC cores
	// be rebuilt from typed columns with zero wire drift. Driven from the golden captures (the DB no longer stores the
	// captured body - both NpcSpawn.Data and Npc.Data are retired), covering the animal + humanoid core shapes.
	public class NpcStatCoreCodecTests
	{
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcMinimalPanther"), InlineData("SpawnNpcCasilda")]
		// animal
		// animal (minimal)
		// humanoid (full core + equipment)
		public void StatCore_RoundTripsByteExact(string golden)
		{
			var body = Golden.Bytes(golden);
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));

			// Lift(msg) -> NpcStatCore -> To*(core) must reproduce each stat-core sub-message byte-for-byte, so a spawn
			// built from typed columns matches the capture.
			var core = NpcStatCoreCodec.Lift(read.Message);
			if (read.Message.Stats != null)
				Assert.True(Same(read.Message.Stats, NpcStatCoreCodec.ToStats(core)), "CharacterStats");
			if (read.Message.npc_stats != null)
				Assert.True(Same(read.Message.npc_stats, NpcStatCoreCodec.ToNpcStats(core)), "NpcStats");
			if (read.Message.Bcc != null)
				Assert.True(Same(read.Message.Bcc, NpcStatCoreCodec.ToBcc(core)), "CharacterBcc");
		}

		private static bool Same<T>(T a, T b)
		{
			if (a == null && b == null)
				return true;

			return a != null && b != null && Ser(a).AsSpan().SequenceEqual(Ser(b));
		}

		private static byte[] Ser<T>(T msg)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, msg);
			return ms.ToArray();
		}
	}
}
