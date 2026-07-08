using System;
using System.IO;
using System.Runtime.CompilerServices;
using Faolan.Core.Network.Codecs;
using Xunit;

namespace Faolan.Tests
{
	// Locks the PlayerSpawnScaffoldCodec round-trip: a player FA015807 body decomposed into the structured
	// scaffold (transform / pre-IIR frame / CharacterIIR / entry table / ID list / end pairs / buff records)
	// and regenerated must be byte-for-byte identical. The golden fixtures are real captured player spawns
	// covering every layout variant the captures contain (the Python prototype proved the grammar over all 37
	// player-own spawns, including captures added after the codec was written; these pin the C# port):
	//   SpawnEditorPreview      - in-editor preview, 228-byte constant pre frame, empty scaffold
	//   SpawnDikketestLogin     - fresh char at world entry (the fresh-character generation reference)
	//   SpawnNecroL80KingsCastle  - geared char, 339-byte pre frame with the 26-slot table, end pairs
	//   SpawnDemoL80RandomActions- both ending pair lists non-empty
	//   SpawnHoxxlingBuffed     - active buffs (two 60-byte buff records)
	public class PlayerSpawnScaffoldCodecTests
	{
		// The generated CharacterIIR component slot must agree with the existing splice path: re-encoding the
		// decoded scaffold of the template and feeding its CharacterIir through SetCharacterMessage's oracle.
		[Fact]
		public void Generate_AfterCharacterIirSwap_StaysParseable()
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Golden("SpawnDikketestLogin"), out var s, out _));
			var donor = Support.Golden.Bytes("SpawnStrieTemplate");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(donor, out var strie));

			s.CharacterIir = donor.AsSpan(strie.Offset, strie.Length).ToArray(); // different length than Dikketest's
			var rebuilt = PlayerSpawnScaffoldCodec.Generate(s);

			Assert.True(PlayerSpawnScaffoldCodec.TryParse(rebuilt, out var back, out var error), $"reparse failed: {error}");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(rebuilt, out var name));
			Assert.Equal("Strie", name.Message.Name);
			Assert.Equal(s.Entries.Count, back.Entries.Count);
		}

		[Fact]
		public void Parse_BuffedSpawn_LiftsBuffRecords()
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Golden("SpawnHoxxlingBuffed"), out var s, out _));

			Assert.Equal(2, s.BuffRecords.Count);
			Assert.All(s.BuffRecords, r => Assert.Equal(60, r.Length));
			Assert.Equal(35, s.Entries.Count);
		}

		[Fact]
		public void Parse_GearedSpawn_LiftsEndPairsAndPreTable()
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Golden("SpawnNecroL80KingsCastle"), out var s, out _));

			Assert.Equal(339, s.PreFrame.Length); // the geared variant with the 26-slot table
			Assert.Equal(14, s.Entries.Count);
			Assert.Equal(0x2B6u, s.EntryTableX);
			Assert.Equal(8, s.EndPairs.Count);
			Assert.Equal(new ScaffoldPair(10, -14375), s.EndPairs[0]);
		}

		[Fact]
		public void Parse_RejectsNonSpawnBodies()
		{
			Assert.False(PlayerSpawnScaffoldCodec.TryParse([1, 2, 3], out _, out _));
			Assert.False(PlayerSpawnScaffoldCodec.TryParse(new byte[64], out _, out _));
		}

		[Fact]
		public void Parse_Template_LiftsTheDocumentedScaffold()
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Support.Golden.Bytes("SpawnStrieTemplate"), out var s, out _));

			Assert.Equal(0x02165EFFu, s.Handle);
			Assert.Equal(5235752UL, s.Playfield);
			Assert.Equal(239, s.PreFrame.Length);
			Assert.Empty(s.BuffRecords);

			// Strie: 7 entries (the weighted attributes idx 1..6 with weight 1.0 + the [u16][u32] closer at 7).
			Assert.Equal(7, s.Entries.Count);
			Assert.Equal(new ScaffoldEntry(1, 0x0311, 0x3F800000), s.Entries[0]);
			Assert.Equal(new ScaffoldEntry(7, 0x3F10, 0x12C), s.Entries[6]);

			// 14 ability/feat ids, ascending, all rank 0; no ending pairs.
			Assert.Equal(14, s.IdList.Count);
			Assert.Equal(0xD48u, s.IdList[0].Id);
			Assert.Equal(0x50E24u, s.IdList[^1].Id);
			Assert.All(s.IdList, id => Assert.Equal(0, id.Rank));
			Assert.Empty(s.EndPairs);
			Assert.Empty(s.EndPairs2);
		}

		[Theory, InlineData("SpawnEditorPreview"), InlineData("SpawnDikketestLogin"), InlineData("SpawnNecroL80KingsCastle"), InlineData("SpawnDemoL80RandomActions"), InlineData("SpawnHoxxlingBuffed")]
		public void ParseThenGenerate_IsByteIdentical(string name)
		{
			var body = Golden(name);

			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out var error), $"parse failed: {error}");
			var rebuilt = PlayerSpawnScaffoldCodec.Generate(scaffold);

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(rebuilt));
		}

		[Fact]
		public void ParseThenGenerate_Template_IsByteIdentical()
		{
			var body = Support.Golden.Bytes("SpawnStrieTemplate");

			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out var error), $"parse failed: {error}");

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(PlayerSpawnScaffoldCodec.Generate(scaffold)));
		}

		private static byte[] Golden(string name)
		{
			return File.ReadAllBytes(Path.Combine(GoldenDir(), name + ".bin"));
		}

		private static string GoldenDir([CallerFilePath] string thisFile = "")
		{
			return Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");
		}
	}
}
