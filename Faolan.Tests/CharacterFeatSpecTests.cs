using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Saved feat specs survive the round trip through the database rows.
	//
	// The gate that matters is the multi-spec one: a character with four named specs has to come back out as four
	// named specs with their own feat lists, and the pre-IIR frame regenerated from those rows has to be the frame
	// that went in. Before this, every spec's ids were flattened into one list and the names were dropped, so a
	// reimport of one of those characters silently merged four builds into a 109-feat blob.
	public class CharacterFeatSpecTests
	{
		[Fact]
		public void EveryCapturedSpawn_RoundTripsItsSpecsThroughTheRows()
		{
			var editorFrames = 0;
			var multiSpec = 0;
			var checkedSpawns = 0;
			var names = new List<string>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var scaffold) || !SpawnBodyReader.IsPlayer(scaffold))
					continue;

				var frame = scaffold.PreIir;

				// Extraction: the captured spawn -> CharacterFeatSpec + CharacterFeat rows.
				var character = new Character
				{
					Id = scaffold.Handle,
					Class = ConanClass.Necromancer
				};
				PlayerSpawnScaffoldRows.ExtractTo(character, scaffold, MorphRegistry.RefsByTag);
				checkedSpawns++;

				// Extraction is exact, the create-editor frame included: it has no sublist at all, so it yields no
				// spec rows.
				Assert.Equal(frame.Sublist.Count, character.FeatSpecs.Count);
				if (frame.Sublist.Count > 1)
					multiSpec++;

				if (frame.Sublist.Count == 0)
				{
					// The create-editor preview spawn. Nothing regenerates it from character rows - it is built by the
					// editor flow - and SpecsOf deliberately answers with the one spec every real character has, so
					// comparing the two here would be asserting that a preview is a character.
					editorFrames++;
					continue;
				}

				// Generation: the rows -> the wire form, which must be what was parsed out, spec for spec.
				var regenerated = PlayerSpawnScaffoldRows.SpecsOf(character);
				Assert.Equal(frame.Sublist.Count, regenerated.Count);
				for (var i = 0; i < regenerated.Count; i++)
				{
					Assert.Equal(frame.Sublist[i].Name, regenerated[i].Name);
					Assert.Equal(frame.Sublist[i].FeatIds, regenerated[i].FeatIds);
					Assert.Equal(frame.Sublist[i].Key, regenerated[i].Key);
					Assert.Equal(frame.Sublist[i].Flag, regenerated[i].Flag);
				}

				names.AddRange(character.FeatSpecs.Where(s => s.Name.Length > 0).Select(s => s.Name));
			}

			Assert.Equal(317, checkedSpawns);
			Assert.Equal(21, editorFrames);
			Assert.Equal(10, multiSpec);

			// The named specs reach the rows, not just the codec.
			// Ordinal, not the culture's collation: "PvP" sorts before "PvP - ..." only under an ordinal comparison,
			// and a culture-dependent gate would pass or fail depending on the machine that ran it.
			Assert.Equal([
				"Berserker: No Decap", "Hybrid", "PvE - Lance", "PvE - Lance", "PvP", "PvP", "PvP - Decap/Armor", "PvP - Decap/Protection"
			], names.Order(StringComparer.Ordinal).ToArray());
		}

		// The same feat id in two specs is a real shape - the captured multi-spec characters share most of their list
		// across builds - and a (CharacterId, FeatId) key would have collapsed those into one row.
		[Fact]
		public void TheSameFeatInTwoSpecs_IsTwoRows()
		{
			var character = new Character
			{
				Id = 42,
				Class = ConanClass.Barbarian
			};
			var scaffold = SpawnWithSpecs([("PvE", [110004u, 112700u]), ("PvP", [110004u, 118800u])]);

			PlayerSpawnScaffoldRows.ExtractTo(character, scaffold, MorphRegistry.RefsByTag);

			Assert.Equal(4, character.Feats.Count);
			Assert.Equal(2, character.Feats.Count(f => f.FeatId == 110004u));
			Assert.Equal([0, 0, 1, 1], character.Feats.Select(f => f.SpecIndex).Order().ToArray());
			Assert.Equal(["PvE", "PvP"], character.FeatSpecs.OrderBy(s => s.Index).Select(s => s.Name).ToArray());
		}

		// A character with no spec rows throws rather than being handed a synthesized one. Every character carries at
		// least the one unnamed spec, which TrySeedFreshRows writes, so an empty set means the rows are incomplete,
		// most likely loaded without
		// Include(c => c.FeatSpecs). Synthesizing one would spawn the character with an empty feat list, which renders
		// as "you have trained nothing" and is indistinguishable from the truth.
		[Fact]
		public void ACharacterWithNoSpecRows_Throws()
		{
			var character = new Character
			{
				Id = 7,
				Class = ConanClass.Guardian,
				Feats =
				[
					new CharacterFeat { CharacterId = 7, FeatId = 200000, Sequence = 0 },
					new CharacterFeat { CharacterId = 7, FeatId = 200100, Sequence = 1 }
				]
			};

			var error = Assert.Throws<InvalidOperationException>(() => PlayerSpawnScaffoldRows.SpecsOf(character));
			Assert.Contains("no CharacterFeatSpec rows", error.Message);
		}

		// With its one spec row present it generates exactly the frame the single-spec overload does.
		[Fact]
		public void ACharacterWithItsOneSpec_GeneratesTheSingleSpecFrame()
		{
			var character = new Character
			{
				Id = 7,
				Class = ConanClass.Guardian,
				FeatSpecs = [new CharacterFeatSpec { CharacterId = 7, Index = 0, Name = "" }],
				Feats =
				[
					new CharacterFeat { CharacterId = 7, FeatId = 200000, Sequence = 0 },
					new CharacterFeat { CharacterId = 7, FeatId = 200100, Sequence = 1 }
				]
			};

			var specs = PlayerSpawnScaffoldRows.SpecsOf(character);

			Assert.Single(specs);
			Assert.Equal("", specs[0].Name);
			Assert.Equal([200000u, 200100u], specs[0].FeatIds);
			// Compared as wire bytes: the frame is a typed object now, so equality here has to be the thing that
			// matters - that both construction paths put the same bytes on the wire.
			Assert.Equal(PreIirBytes(PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Guardian, 0, [200000u, 200100u])),
				PreIirBytes(PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Guardian, 0, specs)));
		}

		// A multi-spec frame built from rows is a valid scaffold, and it is 11 bytes + the name per extra spec longer
		// than the single-spec one - the sublist entry's own width, not a guess.
		[Fact]
		public void AMultiSpecFrame_IsTheSingleSpecFrameGrownByOneEntryPerSpec()
		{
			var single = PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Barbarian, 0, [110004u]);
			var two = PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Barbarian, 0, SpecEntries([("", [110004u]), ("PvP", [110004u, 118800u])]));

			// A sublist entry is 11 bytes of frame (key 4 + flag 1 + name length 2 + the feat list's count word 4)
			// plus its name plus 4 per feat.
			Assert.Equal(single.ByteCount + 11 + "PvP".Length + 4 * 2, two.ByteCount);

			var reader = new SpanReader(PreIirBytes(two));
			var parsed = SpawnPreIirCodec.Read(ref reader);
			reader.EnsureAtEnd();
			Assert.Equal(["", "PvP"], parsed.Sublist.Select(s => s.Name).ToArray());
		}

		// The scaffold composes into a spawn record rather than standing alone, so its bytes are reached through the
		// composition pair rather than a Build of its own.
		private static byte[] PreIirBytes(SpawnPreIir frame)
		{
			using var s = new FaolanStream();
			SpawnPreIirCodec.Write(s, frame);
			return s.ToArray();
		}

		private static IReadOnlyList<SpawnPreFrameSublistEntry> SpecEntries((string Name, uint[] Ids)[] specs)
		{
			return specs
				.Select((s, index) =>
				{
					var entry = new SpawnPreFrameSublistEntry
					{
						Key = (uint)index,
						Flag = (byte)index,
						Name = s.Name
					};
					entry.FeatIds.AddRange(s.Ids);
					return entry;
				})
				.ToList();
		}

		private static SimpleCharFullUpdateIIR SpawnWithSpecs((string Name, uint[] Ids)[] specs)
		{
			return new SimpleCharFullUpdateIIR
			{
				PreIir = PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Barbarian, 0, SpecEntries(specs)),
				Post = SpawnPostIirs.Player()
			};
		}
	}
}
