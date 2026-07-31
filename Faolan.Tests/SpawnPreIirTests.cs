using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the typed pre-IIR scaffold codec against real NPC capture bodies. The scaffold is the "last un-lifted
	// part" of the from-DB NPC spawn (Gitea #185): the fixed read sequence between the transform and the embedded
	// CoreCharacterIIR (u8 version + 6 u8 + 53 u32 + a count==0 sublist, docs/re/fa015807-spawn-reader.md). These
	// are the byte-exact round-trip gates - decoding a captured scaffold and re-encoding it reproduces the bytes, so
	// the from-DB path owns the grammar in code instead of slicing the blob.
	public class SpawnPreIirTests
	{
		// The from-scratch FRESH-SPAWN scaffold, built from NOTHING (no captured Data, no DB columns, no session
		// re-addressing), reproduces a real captured minimal NPC byte-for-byte. SpawnNpcMinimalPanther is
		// Kier-Dheed's Panther: its pre-IIR has all 53 words 0 except the category word (0x6f) and all flags 0.
		[Fact]
		public void Minimal_MatchesCapturedMinimalNpc()
		{
			Assert.True(SpawnBodyReader.TryParse(Golden.Bytes("SpawnNpcMinimalPanther"), out var spawn));

			Assert.Equal(Rebuild(SpawnPreIirs.Minimal()), Rebuild(spawn.PreIir));
		}

		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void HasTheDecodedShape(string golden)
		{
			Assert.True(SpawnBodyReader.TryParse(Golden.Bytes(golden), out var spawn));

			Assert.Equal(SpawnPreIir.ExpectedVersion, spawn.PreIir.Version);
			Assert.Equal(SpawnPreIir.WordCount, spawn.PreIir.Words.Length);
			Assert.Equal(SpawnPreIir.FlagCount, spawn.PreIir.Flags.Length);
			// The sublist is EMPTY on these two - which is what the four bytes 00 00 03 F1 used to be asserted as, a
			// "count==0 framing word" written down as a constant. It is ForCount(0), and six captured spawns carry an
			// entry in it (SpawnPreIirSublistTests).
			Assert.Empty(spawn.PreIir.Sublist);
			Assert.Equal(SpawnPreIir.FixedLength, spawn.PreIir.ByteCount);
		}

		// The pre-IIR scaffold CAN carry the NPC's RDB script-binding diagnostic string (the script-bound PvP
		// festival NPCs in the login capture do, docs/re/fa015807-spawn-reader.md), but Crocodile and Casilda - both
		// confirmed to render in-client - carry NO script string: their 53 scaffold words are scalars / floats /
		// session-volatile handles, not text, so DecodeScriptText recovers nothing. This is the evidence that an
		// empty script buffer is a valid, rendering form (Gitea #185 construction route b).
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void ScriptText_IsEmptyForBeachNpcs(string golden)
		{
			Assert.True(SpawnBodyReader.TryParse(Golden.Bytes(golden), out var spawn));

			Assert.Equal("", spawn.PreIir.DecodeScriptText());
		}

		// The scaffold composes into a spawn record rather than standing alone (#530 rule 5c), so its bytes are
		// reached through the composition pair rather than a Build of its own.
		private static byte[] Rebuild(SpawnPreIir frame)
		{
			using var s = new FaolanStream();
			SpawnPreIirCodec.Write(s, frame);
			return s.ToArray();
		}
	}
}
