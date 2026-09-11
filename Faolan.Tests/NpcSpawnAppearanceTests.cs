using System;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the humanoid NPC post-IIR appearance / morph table codec against a real capture (Casilda). Decoding her
	// post-IIR into morph rows and re-encoding them reproduces the captured bytes exactly - the construction oracle
	// that lets the appearance table generate from the DB rather than from a captured blob.
	public class NpcSpawnAppearanceTests
	{
		// Animals (the 41-byte minimal post-IIR) carry no table under either decoder.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcMinimalPanther")]
		public void DecodeAppearanceLenient_Animal_ReturnsFalse(string golden)
		{
			Assert.False(NpcSpawnAppearance.TryLiftLenient(Golden.Bytes(golden), out var morphs));
			Assert.Empty(morphs);
		}

		// A clean-shape body decodes identically under the lenient path (firstRecordLen == 5, record1 == the empty
		// record) - the lenient decode is a strict superset, so it never regresses the 582 clean captures.
		[Fact]
		public void DecodeAppearanceLenient_CleanShape_MatchesStrict()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(NpcSpawnAppearance.TryLift(body, out var strict));
			Assert.True(NpcSpawnAppearance.TryLiftLenient(body, out var lenient));
			Assert.Equal(AppearancePostIir(strict), AppearancePostIir(lenient));
		}

		// The lenient decode: a mid-session humanoid capture carries the same morph table behind a large runtime
		// first record instead of the fresh 5-byte one. Strict TryDecodeAppearance declines it; the lenient decode
		// recovers the identical table. Synthesized from Casilda's clean capture by swapping her fresh first record
		// for a runtime block - which, like every captured first record, ends in the buff list's framing word.
		[Fact]
		public void DecodeAppearanceLenient_RuntimeBearing_RecoversSameMorphs()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(body, out var read));
			var postStart = read.Offset + read.Length;

			Assert.True(NpcSpawnAppearance.TryLift(body, out var cleanMorphs));

			var runtimeBody0 = BuildRuntimeBearing(body, postStart, cleanMorphs, buffs: 0);

			Assert.False(NpcSpawnAppearance.TryLift(runtimeBody0, out _));
			Assert.True(NpcSpawnAppearance.TryLiftLenient(runtimeBody0, out var lenientMorphs));
			Assert.Equal(cleanMorphs.Count, lenientMorphs.Count);
			// Both lift the identical appearance table (BuildPostIir is the canonical fresh form for both).
			Assert.Equal(AppearancePostIir(cleanMorphs), AppearancePostIir(lenientMorphs));
		}

		// The same body with an active buff on it. Finding the table by arithmetic - at 12 + firstRecordLen - puts a
		// buff record between that offset and the table, so a buffed humanoid is classified no-morph and spawns bald.
		// Reading the stream finds the table wherever the buffs leave it: over the corpus that is 269 more captured
		// bodies, and no lost ones.
		[Fact]
		public void DecodeAppearanceLenient_WithAnActiveBuff_StillRecoversTheTable()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(body, out var read));
			var postStart = read.Offset + read.Length;

			Assert.True(NpcSpawnAppearance.TryLift(body, out var cleanMorphs));

			var buffed = BuildRuntimeBearing(body, postStart, cleanMorphs, buffs: 1);
			Assert.True(NpcSpawnAppearance.TryLiftLenient(buffed, out var morphs));
			Assert.Equal(AppearancePostIir(cleanMorphs), AppearancePostIir(morphs));

			// And the whole stream still walks: the buff is read, not skipped past.
			Assert.True(SpawnBodyReader.TryParse(buffed, out var spawn));
			Assert.Single(spawn.Post.BuffRecords);
		}

		// A mid-session body: Casilda's morph table behind a runtime first record (the entity's current move task,
		// its move task) and `buffs` active buff records - the shape a capture taken in play carries, where a fresh spawn
		// drops both.
		private static byte[] BuildRuntimeBearing(byte[] body, int postStart, System.Collections.Generic.List<AppearanceEntry> morphs, int buffs)
		{
			// The runtime state is a move task, and the reader requires the slot to be one rather than accepting any
			// bytes of the right length. A mode-3 Jump node is the shortest real task.
			var runtime = SpawnPostIirs.Npc(SpawnPostIirs.MovementModeRun, morphs);
			runtime.MoveTask = new MoveTaskNode
			{
				Mode = 3,
				Flag = 1
			};
			for (var n = 0; n < buffs; n++)
			{
				var group = new ScaffoldBuffGroup { SpellId = 1148789 };
				group.Records.Add(new ScaffoldBuff { TargetHandle = 0x1310001 });
				runtime.BuffRecords.Add(group);
			}

			using var post = new FaolanStream();
			SpawnPostIirCodec.Write(post, runtime);

			var bytes = post.ToArray();
			var runtimeBody = new byte[postStart + bytes.Length];
			Array.Copy(body, runtimeBody, postStart);
			bytes.CopyTo(runtimeBody, postStart);
			runtimeBody.WriteU32Be(0, (uint)(runtimeBody.Length - 4));
			return runtimeBody;
		}

		// The canonical fresh appearance post-IIR for a set of morph rows: what the from-DB build emits, and the
		// form both lift paths are compared through.
		private static byte[] AppearancePostIir(System.Collections.Generic.IReadOnlyList<AppearanceEntry> morphs)
		{
			using var s = new FaolanStream();
			SpawnPostIirCodec.Write(s, SpawnPostIirs.Npc(SpawnPostIirs.MovementModeRun, morphs));
			return s.ToArray();
		}

		// Animals carry no appearance table (the 41-byte minimal post-IIR), so the decoder declines them - they
		// stay on the minimal-scaffold path.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcMinimalPanther")]
		public void DecodeAppearance_Animal_ReturnsFalse(string golden)
		{
			Assert.False(NpcSpawnAppearance.TryLift(Golden.Bytes(golden), out var morphs));
			Assert.Empty(morphs);
		}

		[Fact]
		public void DecodeAppearance_Casilda_RoundTripsBuildPostIir()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(body, out var read));
			var capturedPostIir = body[(read.Offset + read.Length)..];

			Assert.True(NpcSpawnAppearance.TryLift(body, out var morphs));
			Assert.Equal(14, morphs.Count);

			// The texscript colour ref rides the id 0x10D entry; the rest are plain [morphId][float] morph values.
			var texscript = morphs.Single(m => m.Text.Length > 0);
			Assert.Equal(0x10Du, texscript.MorphId);
			Assert.Equal("*ii=-1;*bl=-1", texscript.Text);

			// Re-encoding the decoded rows reproduces the captured post-IIR byte-for-byte.
			Assert.Equal(capturedPostIir, AppearancePostIir(morphs));
		}
	}
}
