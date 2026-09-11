using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Corpus-breadth gate for the pre-IIR scaffold's grammar. The frame was carried as an opaque
	// byte[] whose "constants" were poked at literal offsets; SpawnPreIirCodec walks it as the client's own
	// SimpleCharFullUpdateIIR scaffold instead. Reading the corpus is the point: the frame has six length variants
	// and the two that would expose a wrong grammar (a non-empty sublist, a many-feat frame) are a minority, so a
	// fixture-sized gate would pass on a mis-decode.
	public class SpawnPreIirCorpusTests
	{
		// A frame that framed "close enough" is a mis-decode, so the assertion is that the walk lands exactly on the
		// frame's last byte - the same consume-to-end oracle that broke the 0x3F1-as-a-constant readings. It has to
		// hold for every captured frame, not most: the three "landmark constants" this replaces (0x07E2, 0x03F1 and
		// the "41 00 00 82 08 closer") all read as constants across the majority of captures.
		[Fact]
		public void EveryCapturedPreFrame_ParsesToTheExactByte_AndRebuilds()
		{
			var mismatches = new List<string>();
			var shapes = new Dictionary<string, int>();

			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || !SpawnBodyReader.IsPlayer(spawn))
					continue;

				var frame = spawn.PreIir;
				var rebuilt = Rebuild(frame);
				if (rebuilt.Length != frame.ByteCount)
					mismatches.Add($"{source}: rebuilt {rebuilt.Length} bytes, ByteCount says {frame.ByteCount}");

				var shape = $"sublist={frame.Sublist.Count} named={frame.Sublist.Count(e => e.Name.Length > 0)}";
				shapes[shape] = shapes.GetValueOrDefault(shape) + 1;

				// The frame's whole width is accounted for: nothing is skipped, padded over, or trailing. 223 is the
				// fixed scaffold (1 + 6 u8 + 53 u32 + the sublist's own count word) = SpawnPreIir.FixedLength. The
				// embedded CoreCharacter's version byte + IirHeader are the message's, not the scaffold's, which is
				// what a frame cut at the IIR's length prefix gets wrong.
				var expected = SpawnPreIir.FixedLength + frame.Sublist.Sum(e => 4 + 1 + 2 + e.Name.Length + 4 + 4 * e.FeatIds.Count);
				Assert.Equal(expected, rebuilt.Length);
			}

			Assert.True(mismatches.Count == 0, $"{mismatches.Count} captured pre-IIR frames did not rebuild byte-exact:\n  " + string.Join("\n  ", mismatches.Take(10)));

			// Pinned to the full corpus. The 25 captured frame lengths are one shape parameterised by the sublist:
			// 228 is the create-editor frame (empty sublist), 286 frames carry the single unnamed spec every normal
			// login has, and the 10 that carry more are the whole reason this reads the corpus - they are what proved
			// the entry has a name and the sublist a count. A regeneration against a partial corpus, or a grammar that
			// quietly stopped finding the sublist, lands here rather than passing with fewer frames.
			Assert.Equal(new Dictionary<string, int>
			{
				["sublist=0 named=0"] = 21,
				["sublist=1 named=0"] = 286,
				["sublist=2 named=0"] = 7,
				["sublist=2 named=2"] = 2,
				["sublist=4 named=4"] = 1
			}, shapes);
		}

		// The named entries are what identify the sublist as the character's saved feat specs rather than a lead-in
		// to a bare feat list. Pinned by name: a grammar that read the name length wrong would still frame (the
		// following word is a list count, which self-checks), but would produce garbage here.
		[Fact]
		public void TheNamedSublistEntries_AreSavedFeatSpecs()
		{
			var specs = new List<string>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || !SpawnBodyReader.IsPlayer(spawn))
					continue;

				var frame = spawn.PreIir;

				foreach (var entry in frame.Sublist)
				{
					if (entry.Name.Length > 0)
						specs.Add($"{entry.Name} ({entry.FeatIds.Count})");

					// Key and flag are the entry's own index on every captured frame, single- and multi-spec alike.
					Assert.Equal((uint)frame.Sublist.IndexOf(entry), entry.Key);
					Assert.Equal((byte)frame.Sublist.IndexOf(entry), entry.Flag);
				}
			}

			Assert.Equal([
				"Berserker: No Decap (26)", "Hybrid (27)", "PvE - Lance (23)", "PvE - Lance (23)", "PvP - Decap/Armor (28)", "PvP - Decap/Protection (28)", "PvP (27)", "PvP (27)"
			], specs.Order().ToArray());
		}

		// The scaffold is one grammar carrying one meaning - a PvP/minigame stat block (SpawnPreIir.WordStatIds) -
		// and what an NPC carries in it is uninitialised retail server memory, not content of its own.
		//
		// This gate is the measurement that settles it, and it has to read the corpus: the claim is about how the
		// same NPC varies between sightings, which a single fixture cannot express. If the block held per-type
		// content - the "RDB script-binding text, keyed per NPC type" reading this replaces - then every Pillager
		// would carry the same 53 words. 591 captured Pillagers carry 551 distinct blocks.
		[Fact]
		public void AnNpcsScaffold_IsPerSighting_NotPerType()
		{
			var byName = new Dictionary<string, (HashSet<string> Blocks, int Sightings)>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || SpawnBodyReader.IsPlayer(spawn))
					continue;

				var name = spawn.Character.Name;
				if (name.Length == 0)
					continue;

				var block = string.Join(",", spawn.PreIir.Words.Select(w => w.ToString("x8")));
				var e = byName.GetValueOrDefault(name, (Blocks: [], Sightings: 0));
				e.Blocks.Add(block);
				byName[name] = (e.Blocks, e.Sightings + 1);
			}

			// Only names actually seen more than once can say anything about stability.
			var repeats = byName.Values.Where(v => v.Sightings > 1).ToList();
			var varies = repeats.Count(v => v.Blocks.Count > 1);

			Assert.Equal(705, repeats.Count);
			Assert.Equal(700, varies);
		}

		// The other half of the same measurement, from the player side, and the reason "it is server memory" is a
		// finding rather than a guess: the leaked text only ever appears where the server has nothing to write.
		//
		// A player's block is the character's real PvP career, so it is numeric and stable - the same character in
		// the same session carries the same words. An NPC has no career, the server leaves the buffer alone, and
		// what goes out is whatever was in it: script-engine error strings, `<localized>` LDB markup, and unrelated
		// binary. Not one of the 317 captured player frames carries a printable run.
		[Fact]
		public void OnlyNpcScaffolds_CarryLeakedServerText()
		{
			int playersWithText = 0, players = 0, npcsWithText = 0, npcs = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn))
					continue;

				var hasText = spawn.PreIir.DecodeScriptText().Length > 3;
				if (SpawnBodyReader.IsPlayer(spawn))
				{
					players++;
					if (hasText) playersWithText++;
				}
				else
				{
					npcs++;
					if (hasText) npcsWithText++;
				}
			}

			Assert.Equal(317, players);
			Assert.Equal(0, playersWithText);
			Assert.Equal(17400, npcs);
			Assert.Equal(5438, npcsWithText);
		}

		// Which slots the server actually writes on an NPC spawn - the measurement that turns "the NPC block is
		// leaked server data" from an inference about content into a statement about mechanism.
		//
		// Take only the frames that are visibly carrying leaked text, and ask per word: is this slot ever the
		// interior of a string (all four bytes printable, no zero padding - a zero-padded small integer like 111
		// is 'o' followed by nulls and must not count, which is what a laxer test gets wrong). Every slot but
		// three lands at 70-90%. Exactly w17, w18 and w38 land at 0.0% - the leak never touches them because the
		// server writes them. That is 3 of 53, and SpawnPreIirs.Minimal already emits exactly that shape: zeros
		// everywhere except the category word.
		[Fact]
		public void OnlyThreeScaffoldSlots_AreWrittenOnAnNpcSpawn()
		{
			var stringInterior = new int[SpawnPreIir.WordCount];
			var textHeavy = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || SpawnBodyReader.IsPlayer(spawn))
					continue;

				var words = spawn.PreIir.Words;
				if (words.Count(IsStringInterior) < 30)
					continue;

				textHeavy++;
				for (var i = 0; i < words.Length; i++)
					if (IsStringInterior(words[i]))
						stringInterior[i]++;
			}

			Assert.Equal(1171, textHeavy);

			var never = Enumerable.Range(0, SpawnPreIir.WordCount).Where(i => stringInterior[i] == 0).ToArray();
			Assert.Equal([SpawnPreIir.CategoryWordIndex, 18, 38], never);

			// ...and every other slot is text in the clear majority of them, so the three are a real gap and not
			// three slots that merely happened to hold a number.
			foreach (var i in Enumerable.Range(0, SpawnPreIir.WordCount).Except(never))
				Assert.True(stringInterior[i] > textHeavy * 2 / 3, $"w{i} is string-interior in only {stringInterior[i]}/{textHeavy} frames");
		}

		// All four bytes printable. A word must not qualify on zero padding - see the test above.
		private static bool IsStringInterior(uint word)
		{
			for (var shift = 0; shift < 32; shift += 8)
			{
				var b = (byte)(word >> shift);
				if (b is < 0x20 or >= 0x7f)
					return false;
			}

			return true;
		}

		// The scaffold's fields are the client's, not ours, and three of them are independently corroborated: the
		// category word carries the same modal value the NPC sweep found, the vanity word sits where a completely
		// different route found a u32, and the frame's last two fields are the embedded CoreCharacter's
		// version byte + IirHeader rather than a closing constant.
		[Fact]
		public void EveryCapturedPreFrame_CarriesTheClientsFieldGrid()
		{
			var categories = new Dictionary<uint, int>();
			var constants = new Dictionary<uint, int>();
			var iirHeaders = new Dictionary<uint, int>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || !SpawnBodyReader.IsPlayer(spawn))
					continue;

				var frame = spawn.PreIir;

				categories[frame.Words[SpawnPreIir.CategoryWordIndex]] = categories.GetValueOrDefault(frame.Words[SpawnPreIir.CategoryWordIndex]) + 1;
				constants[frame.Words[SpawnPreIir.ConstantWordIndex]] = constants.GetValueOrDefault(frame.Words[SpawnPreIir.ConstantWordIndex]) + 1;
				iirHeaders[spawn.CoreCharIirHeader] = iirHeaders.GetValueOrDefault(spawn.CoreCharIirHeader) + 1;
				Assert.Equal(SimpleCharFullUpdateCodec.CoreCharacterVersion, spawn.CoreCharVersion);
				Assert.Equal(SpawnPreIir.ExpectedVersion, frame.Version);
			}

			// Both hold on every captured frame, create-editor previews included - the category is the same modal
			// value the NPC sweep found, from an entirely separate direction.
			Assert.Equal(new Dictionary<uint, int> { [SpawnPreIir.DefaultCategory] = 317 }, categories);
			Assert.Equal(new Dictionary<uint, int> { [SpawnPreIir.ConstantWordValue] = 317 }, constants);

			// The CoreCharacter's IirHeader is one of two values and is not per-character (the same character carries
			// both across sessions), so the generated frame reproduces the modal one rather than deriving it.
			Assert.Equal([SimpleCharFullUpdateIIR.DefaultCoreCharIirHeader, 0x820Au], iirHeaders.Keys.Order().ToArray());
			Assert.Equal(317, iirHeaders.Values.Sum());
		}

		// The generated fresh frame is the captured shape, not a lookalike: it walks under the same grammar and its
		// feat list round-trips through the sublist.
		[Theory]
		[InlineData(ConanClass.Barbarian, 239 - 5)] // the 5 the old player model counted in are the CoreCharacter's
		[InlineData(ConanClass.Necromancer, 239 - 5)]
		public void FreshPreFrame_IsAValidScaffold(ConanClass @class, int expectedLength)
		{
			var frame = PlayerSpawnScaffoldRows.FreshPreFrame(@class);
			Assert.Equal(expectedLength, frame.ByteCount);

			var bytes = Rebuild(frame);
			var reader = new SpanReader(bytes);
			var parsed = SpawnPreIirCodec.Read(ref reader);
			reader.EnsureAtEnd();

			Assert.Equal(PlayerSpawnScaffoldRows.FreshPreFrame(@class, 0, [111, 222, 333]).ByteCount, expectedLength + 12);
			Assert.Single(parsed.Sublist);
			Assert.Empty(parsed.Sublist[0].FeatIds);
			Assert.Equal((byte)@class, parsed.Flags[SpawnPreIir.ClassFlagIndex]);
		}

		// The scaffold composes into a spawn record rather than standing alone, so its bytes are reached through the
		// composition pair rather than a Build of its own.
		private static byte[] Rebuild(SpawnPreIir frame)
		{
			using var s = new FaolanStream();
			SpawnPreIirCodec.Write(s, frame);
			return s.ToArray();
		}
	}
}
