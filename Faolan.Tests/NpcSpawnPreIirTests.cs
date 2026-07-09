using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the typed NPC pre-IIR / post-IIR scaffold codecs against real capture bodies. The pre-IIR scaffold is
	// the "last un-lifted part" of the from-DB NPC spawn (Gitea #185): the fixed read sequence between the
	// transform and the embedded CoreCharacterIIR (u8 version + 6 u8 + 53 u32 + a count==0 sublist,
	// docs/fa015807-spawn-reader.md). These are the byte-exact round-trip gates - decoding a captured scaffold and
	// re-encoding it reproduces the bytes, so the from-DB path owns the grammar in code instead of slicing the blob.
	public class NpcSpawnPreIirTests
	{
		[Fact]
		public void PostIir_BuildMinimal_MatchesCapturedMinimalNpc()
		{
			var body = Golden.Bytes("SpawnNpcMinimalPanther");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var captured = body[(read.Offset + read.Length)..];

			Assert.Equal(captured, NpcSpawnScaffold.BuildMinimalPostIir());
		}

		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void PostIir_DecodeEncode_IsByteIdentical(string golden)
		{
			var body = Golden.Bytes(golden);

			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var captured = body[(read.Offset + read.Length)..];

			Assert.True(NpcSpawnPostIir.TryDecode(body, out var post));
			Assert.Equal(0u, post.Word0); // word0 == 0 across the NPC corpus (the player spawn has 2)
			Assert.Equal(captured, post.Encode());
		}

		// The from-scratch FRESH-SPAWN scaffold, built from NOTHING (no captured Data, no DB columns, no session
		// re-addressing), reproduces a real captured minimal NPC byte-for-byte. SpawnNpcMinimalPanther is
		// Kier-Dheed's Panther: its pre-IIR has all 53 words 0 except the category word (0x6f) and all flags 0, and
		// its post-IIR is the recordCount==1 minimal form. This is the proof that the cross-capture sweep's
		// conclusion holds in code - the "last un-lifted part" of the NPC spawn (Gitea #185) is a constant, so the
		// remaining work is only the in-client flip (re-run once the world-entry path emits these).
		[Fact]
		public void PreIir_BuildMinimal_MatchesCapturedMinimalNpc()
		{
			var body = Golden.Bytes("SpawnNpcMinimalPanther");
			Assert.True(NpcSpawnPreIirCodec.TryDecode(body, out _, out var offset));

			var captured = body[offset..(offset + NpcSpawnPreIir.FixedLength)];
			Assert.Equal(captured, NpcSpawnPreIirCodec.Encode(NpcSpawnPreIir.BuildMinimal()));
		}

		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void PreIir_DecodeEncode_IsByteIdentical(string golden)
		{
			var body = Golden.Bytes(golden);

			Assert.True(NpcSpawnPreIirCodec.TryDecode(body, out var scaffold, out var offset));
			Assert.Equal(NpcSpawnPreIir.ExpectedVersion, scaffold.Version);

			var captured = body[offset..(offset + NpcSpawnPreIir.FixedLength)];
			Assert.Equal(captured, NpcSpawnPreIirCodec.Encode(scaffold));
		}

		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void PreIir_HasTheDecodedShape(string golden)
		{
			var body = Golden.Bytes(golden);
			Assert.True(NpcSpawnPreIirCodec.TryDecode(body, out var scaffold, out _));

			Assert.Equal(NpcSpawnPreIir.WordCount, scaffold.Words.Length);
			Assert.Equal(NpcSpawnPreIir.ByteCount, scaffold.Flags.Length);
			Assert.Equal(new byte[] { 0x00, 0x00, 0x03, 0xF1 }, scaffold.Sublist); // count==0 framing word
		}

		// The pre-IIR scaffold CAN carry the NPC's RDB script-binding diagnostic string (the script-bound PvP
		// festival NPCs in the login capture do, docs/fa015807-spawn-reader.md), but Crocodile and Casilda - both
		// confirmed to render in-client - carry NO script string: their 53 scaffold words are scalars / floats /
		// session-volatile handles, not text, so DecodeScriptText recovers nothing. This is the evidence that an
		// empty script buffer is a valid, rendering form (Gitea #185 construction route b).
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void PreIir_ScriptText_IsEmptyForBeachNpcs(string golden)
		{
			var body = Golden.Bytes(golden);
			Assert.True(NpcSpawnPreIirCodec.TryDecode(body, out var scaffold, out _));

			Assert.Equal("", scaffold.DecodeScriptText());
		}
	}
}
