using System;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the humanoid NPC post-IIR APPEARANCE / morph table codec against a real capture (Casilda). Decoding her
	// post-IIR into morph rows and re-encoding them reproduces the captured bytes exactly - the construction oracle
	// that lets the appearance table generate from the DB so NpcSpawn.Data can be retired (Gitea #185 / #243).
	public class NpcSpawnAppearanceTests
	{
		// Animals (the 41-byte minimal post-IIR) carry no table under either decoder.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcMinimalPanther")]
		public void DecodeAppearanceLenient_Animal_ReturnsFalse(string golden)
		{
			Assert.False(NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(Golden.Bytes(golden), out var morphs));
			Assert.Empty(morphs);
		}

		// A clean-shape body decodes identically under the lenient path (firstRecordLen == 5, record1 == the empty
		// record) - the lenient decode is a strict superset, so it never regresses the 582 clean captures.
		[Fact]
		public void DecodeAppearanceLenient_CleanShape_MatchesStrict()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var strict));
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(body, out var lenient));
			Assert.Equal(NpcSpawnAppearanceCodec.BuildPostIir(strict), NpcSpawnAppearanceCodec.BuildPostIir(lenient));
		}

		// #243 lenient decode: a mid-session humanoid capture carries the SAME morph table behind a large RUNTIME
		// first record instead of the fresh 5-byte one. Strict TryDecodeAppearance declines it; the lenient decode
		// recovers the identical table. Synthesized from Casilda's clean capture by swapping her fresh first record
		// for a runtime block - which, like every captured first record, ENDS in the buff list's framing word (#475).
		[Fact]
		public void DecodeAppearanceLenient_RuntimeBearing_RecoversSameMorphs()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var postStart = read.Offset + read.Length;

			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var cleanMorphs));
			// Clean post-IIR: [u32 0][u32 5][u32 2][5-byte record1][table + terminator]. Keep everything after the
			// 12-byte header + 5-byte record1 (the morph table + terminator) unchanged.
			var tableAndTerminator = body[(postStart + 12 + 5)..];

			var runtimeBody0 = BuildRuntimeBearing(body, postStart, tableAndTerminator, buffs: 0);

			Assert.False(NpcSpawnAppearanceCodec.TryDecodeAppearance(runtimeBody0, out _));
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(runtimeBody0, out var lenientMorphs));
			Assert.Equal(cleanMorphs.Count, lenientMorphs.Count);
			// Both lift the identical appearance table (BuildPostIir is the canonical fresh form for both).
			Assert.Equal(NpcSpawnAppearanceCodec.BuildPostIir(cleanMorphs), NpcSpawnAppearanceCodec.BuildPostIir(lenientMorphs));
		}

		// The same body with an ACTIVE BUFF on it, which is the case the lenient decode used to lose (#475). It found
		// the table by arithmetic - at 12 + firstRecordLen - and a buff record sits between that offset and the table,
		// so a buffed humanoid was classified no-morph and would spawn BALD. Reading the stream finds the table
		// wherever the buffs leave it. Over the corpus this is 269 more captured bodies, and no lost ones.
		[Fact]
		public void DecodeAppearanceLenient_WithAnActiveBuff_StillRecoversTheTable()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var postStart = read.Offset + read.Length;

			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var cleanMorphs));
			var tableAndTerminator = body[(postStart + 12 + 5)..];

			var buffed = BuildRuntimeBearing(body, postStart, tableAndTerminator, buffs: 1);
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(buffed, out var morphs));
			Assert.Equal(NpcSpawnAppearanceCodec.BuildPostIir(cleanMorphs), NpcSpawnAppearanceCodec.BuildPostIir(morphs));

			// And the whole stream still walks: the buff is READ, not skipped past.
			Assert.True(NpcSpawnPostIir.TryDecode(buffed, out var post));
			Assert.Single(post.Common.BuffRecords);
		}

		// A mid-session body: Casilda's morph table behind a 40-byte runtime first record (which ends in the buff
		// list's framing word, as every captured one does) and `buffs` active buff records.
		private static byte[] BuildRuntimeBearing(byte[] body, int postStart, byte[] tableAndTerminator, int buffs)
		{
			// The runtime state is a MOVE TASK - decoded in #477, and the reader now requires the slot to BE one
			// rather than accepting any bytes of the right length. A mode-3 Jump node is the shortest real task.
			var task = new FaolanStream().WriteByte(1); // the move-task presence byte
			new MoveTaskNode
			{
				Mode = 3,
				Flag = 1
			}.Write(task);

			var slot = task.ToArray();
			var post = new FaolanStream()
				.WriteUInt32(0u) // no head words
				.WriteUInt32((uint)slot.Length + 4) // the first record: the move task + the buff list's frame
				.WriteUInt32(1u)
				.WriteArray(slot)
				.WriteUInt32(ListFrame.ForCount(buffs));

			for (var n = 0; n < buffs; n++)
				new ScaffoldBuff { SpellId = 1148789, TargetHandle = 0x1310001 }.Write(post);

			var bytes = post.WriteArray(tableAndTerminator).ToArray();
			var runtimeBody = new byte[postStart + bytes.Length];
			Array.Copy(body, runtimeBody, postStart);
			bytes.CopyTo(runtimeBody, postStart);
			runtimeBody.WriteU32Be(0, (uint)(runtimeBody.Length - 4));
			return runtimeBody;
		}

		// Animals carry no appearance table (the 41-byte minimal post-IIR), so the decoder declines them - they
		// stay on the minimal-scaffold path.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcMinimalPanther")]
		public void DecodeAppearance_Animal_ReturnsFalse(string golden)
		{
			Assert.False(NpcSpawnAppearanceCodec.TryDecodeAppearance(Golden.Bytes(golden), out var morphs));
			Assert.Empty(morphs);
		}

		[Fact]
		public void DecodeAppearance_Casilda_RoundTripsBuildPostIir()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var capturedPostIir = body[(read.Offset + read.Length)..];

			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var morphs));
			Assert.Equal(14, morphs.Count);

			// The texscript colour ref rides the id 0x10D entry; the rest are plain [morphId][float] morph values.
			var texscript = morphs.Single(m => m.Text.Length > 0);
			Assert.Equal(0x10Du, texscript.MorphId);
			Assert.Equal("*ii=-1;*bl=-1", texscript.Text);

			// Re-encoding the decoded rows reproduces the captured post-IIR byte-for-byte.
			Assert.Equal(capturedPostIir, NpcSpawnAppearanceCodec.BuildPostIir(morphs));
		}
	}
}
