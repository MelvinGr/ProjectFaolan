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
	// Corpus-BREADTH gate for the pre-IIR scaffold's grammar (Gitea #466). The frame was carried as an opaque
	// byte[] whose "constants" were poked at literal offsets; SpawnPreIirCodec walks it as the client's own
	// SimpleCharFullUpdateIIR scaffold instead. Reading the corpus is the point: the frame has six length variants
	// and the two that would expose a wrong grammar (a non-empty sublist, a many-feat frame) are a minority, so a
	// fixture-sized gate would pass on a mis-decode.
	public class SpawnPreIirCorpusTests
	{
		// A frame that framed "close enough" is a mis-decode, so the assertion is that the walk lands EXACTLY on the
		// frame's last byte - the same consume-to-end oracle that broke the 0x3F1-as-a-constant readings. It has to
		// hold for every captured frame, not most: the three "landmark constants" this replaces (0x07E2, 0x03F1 and
		// the "41 00 00 82 08 closer") all read as constants across the majority of captures.
		[Fact]
		public void EveryCapturedPreFrame_ParsesToTheExactByte_AndRebuilds()
		{
			var mismatches = new List<string>();
			var shapes = new Dictionary<string, int>();

			foreach (var (source, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
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
				// embedded CoreCharacter's version byte + IirHeader are the MESSAGE's, not the scaffold's, which is
				// what the player-only model used to get wrong by cutting the frame at the IIR's length prefix.
				var expected = SpawnPreIir.FixedLength + frame.Sublist.Sum(e => 4 + 1 + 2 + e.Name.Length + 4 + 4 * e.FeatIds.Count);
				Assert.Equal(expected, rebuilt.Length);
			}

			Assert.True(mismatches.Count == 0, $"{mismatches.Count} captured pre-IIR frames did not rebuild byte-exact:\n  " + string.Join("\n  ", mismatches.Take(10)));

			// Pinned to the FULL corpus. The 25 captured frame LENGTHS are one shape parameterised by the sublist:
			// 228 is the create-editor frame (empty sublist), 232 frames carry the single unnamed spec every normal
			// login has, and the 6 that carry MORE are the whole reason this reads the corpus - they are what proved
			// the entry has a name and the sublist a count. A regeneration against a partial corpus, or a grammar that
			// quietly stopped finding the sublist, lands here rather than passing with fewer frames.
			Assert.Equal(new Dictionary<string, int>
			{
				["sublist=0 named=0"] = 21,
				["sublist=1 named=0"] = 232,
				["sublist=2 named=0"] = 3,
				["sublist=2 named=2"] = 2,
				["sublist=4 named=4"] = 1
			}, shapes);
		}

		// The named entries are what identify the sublist as the character's saved FEAT SPECS rather than a lead-in
		// to a bare feat list. Pinned by name: a grammar that read the name length wrong would still frame (the
		// following word is a list count, which self-checks), but would produce garbage here.
		[Fact]
		public void TheNamedSublistEntries_AreSavedFeatSpecs()
		{
			var specs = new List<string>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
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

		// The scaffold's fields are the client's, not ours, and three of them are independently corroborated: the
		// category word carries the SAME modal value the NPC sweep found, the vanity word sits where #336 found a
		// u32 by a completely different route, and the frame's last two fields are the embedded CoreCharacter's
		// version byte + IirHeader rather than a closing constant.
		[Fact]
		public void EveryCapturedPreFrame_CarriesTheClientsFieldGrid()
		{
			var categories = new Dictionary<uint, int>();
			var constants = new Dictionary<uint, int>();
			var iirHeaders = new Dictionary<uint, int>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
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

			// Both hold on EVERY captured frame, create-editor previews included - the category is the same modal
			// value the NPC sweep found, from an entirely separate direction.
			Assert.Equal(new Dictionary<uint, int> { [SpawnPreIir.DefaultCategory] = 259 }, categories);
			Assert.Equal(new Dictionary<uint, int> { [SpawnPreIir.ConstantWordValue] = 259 }, constants);

			// The CoreCharacter's IirHeader is one of two values and is NOT per-character (the same character carries
			// both across sessions), so the generated frame reproduces the modal one rather than deriving it.
			Assert.Equal([SimpleCharFullUpdateIIR.DefaultCoreCharIirHeader, 0x820Au], iirHeaders.Keys.Order().ToArray());
			Assert.Equal(259, iirHeaders.Values.Sum());
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
