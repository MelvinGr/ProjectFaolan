using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Corpus-BREADTH gate for the player pre-IIR frame's grammar (Gitea #466). The frame was carried as an opaque
	// byte[] whose "constants" were poked at literal offsets; PlayerSpawnPreFrameCodec walks it as the client's own
	// SimpleCharFullUpdateIIR scaffold instead. Reading the corpus is the point: the frame has six length variants
	// and the two that would expose a wrong grammar (a non-empty sublist, a many-feat frame) are a minority, so a
	// fixture-sized gate would pass on a mis-decode.
	public class PlayerSpawnPreFrameTests
	{
		// A frame that framed "close enough" is a mis-decode, so the assertion is that the walk lands EXACTLY on the
		// frame's last byte - the same consume-to-end oracle that broke the 0x3F1-as-a-constant readings. It has to
		// hold for every captured frame, not most: the three "landmark constants" this replaces (0x07E2, 0x03F1 and
		// the "41 00 00 82 08 closer") all read as constants across the majority of captures.
		[Fact]
		public void EveryCapturedPreFrame_ParsesToTheExactByte_AndRebuilds()
		{
			var unparsed = new List<string>();
			var mismatches = new List<string>();
			var shapes = new Dictionary<string, int>();

			foreach (var (source, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _))
					continue;

				if (!PlayerSpawnPreFrameCodec.TryParse(scaffold.PreFrame, out var frame))
				{
					unparsed.Add($"{source}: {scaffold.PreFrame.Length}-byte frame");
					continue;
				}

				var rebuilt = PlayerSpawnPreFrameCodec.Build(frame);
				if (!rebuilt.SequenceEqual(scaffold.PreFrame))
					mismatches.Add($"{source}: {scaffold.PreFrame.Length} bytes in, {rebuilt.Length} out");

				var shape = $"sublist={frame.Sublist.Count} named={frame.Sublist.Count(e => e.Name.Length > 0)}";
				shapes[shape] = shapes.GetValueOrDefault(shape) + 1;

				// The frame's whole width is accounted for: nothing is skipped, padded over, or trailing. 223 is the
				// fixed scaffold (1 + 6 u8 + 53 u32 + the sublist's own count word) = NpcSpawnPreIir.FixedLength, and
				// the 5 are the embedded CoreCharacter's version byte + IirHeader.
				var expected = NpcSpawnPreIir.FixedLength + 5 + frame.Sublist.Sum(e => 4 + 1 + 2 + e.Name.Length + 4 + 4 * e.FeatIds.Count);
				Assert.Equal(expected, scaffold.PreFrame.Length);
			}

			Assert.True(unparsed.Count == 0, $"{unparsed.Count} captured pre-IIR frames did not walk to their exact end:\n  " + string.Join("\n  ", unparsed.Take(10)));
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
				if (!PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _) || !PlayerSpawnPreFrameCodec.TryParse(scaffold.PreFrame, out var frame))
					continue;

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
		// category word carries the SAME modal value NpcSpawnPreIir found on NPCs, the vanity word sits where #336
		// found a u32 by a completely different route, and the frame's last two fields are the embedded
		// CoreCharacter's version byte + IirHeader rather than a closing constant.
		[Fact]
		public void EveryCapturedPreFrame_CarriesTheClientsFieldGrid()
		{
			var categories = new Dictionary<uint, int>();
			var constants = new Dictionary<uint, int>();
			var iirHeaders = new Dictionary<uint, int>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _) || !PlayerSpawnPreFrameCodec.TryParse(scaffold.PreFrame, out var frame))
					continue;

				categories[frame.Words[PlayerSpawnPreFrame.CategoryWordIndex]] = categories.GetValueOrDefault(frame.Words[PlayerSpawnPreFrame.CategoryWordIndex]) + 1;
				constants[frame.Words[PlayerSpawnPreFrame.ConstantWordIndex]] = constants.GetValueOrDefault(frame.Words[PlayerSpawnPreFrame.ConstantWordIndex]) + 1;
				iirHeaders[frame.CoreCharIirHeader] = iirHeaders.GetValueOrDefault(frame.CoreCharIirHeader) + 1;
				Assert.Equal(PlayerSpawnPreFrame.CoreCharacterVersion, frame.CoreCharVersion);
				Assert.Equal(NpcSpawnPreIir.ExpectedVersion, frame.Version);
			}

			// Both hold on EVERY captured frame, create-editor previews included - the category is the same modal
			// value NpcSpawnPreIir found on NPCs, from an entirely separate sweep.
			Assert.Equal(new Dictionary<uint, int> { [NpcSpawnPreIir.DefaultCategory] = 259 }, categories);
			Assert.Equal(new Dictionary<uint, int> { [PlayerSpawnPreFrame.ConstantWordValue] = 259 }, constants);

			// The CoreCharacter's IirHeader is one of two values and is NOT per-character (the same character carries
			// both across sessions), so the generated frame reproduces the modal one rather than deriving it.
			Assert.Equal([PlayerSpawnPreFrame.DefaultCoreCharIirHeader, 0x820Au], iirHeaders.Keys.Order().ToArray());
			Assert.Equal(259, iirHeaders.Values.Sum());
		}

		// The generated fresh frame is the captured shape, not a lookalike: it walks under the same grammar and its
		// feat list round-trips through the sublist.
		[Theory]
		[InlineData(ConanClass.Barbarian, 239)]
		[InlineData(ConanClass.Necromancer, 239)]
		public void FreshPreFrame_IsAValidScaffold(ConanClass @class, int expectedLength)
		{
			var frame = PlayerSpawnScaffoldRows.FreshPreFrame(@class);
			Assert.Equal(expectedLength, frame.Length);
			Assert.True(PlayerSpawnPreFrameCodec.TryParse(frame, out var parsed));
			Assert.Equal(PlayerSpawnScaffoldRows.FreshPreFrame(@class, 0, [111, 222, 333]).Length, expectedLength + 12);
			Assert.Single(parsed.Sublist);
			Assert.Empty(parsed.Sublist[0].FeatIds);
			Assert.Equal((byte)@class, parsed.Flags[PlayerSpawnPreFrame.ClassFlagIndex]);
		}
	}
}
