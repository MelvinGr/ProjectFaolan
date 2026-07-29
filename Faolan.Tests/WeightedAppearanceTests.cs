using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// Locks the weighted-appearance pipeline: the create editor's resource refs (SetMorphValue "*..." values),
	// resolved through the client registry (RDB 1000076 -> MorphTarget, wire tag = registry id - 1), must
	// reproduce the weighted (1.0) entries of the captured spawn. faolanx is the reference case: his 7
	// weighted entries resolve 7/7 bit-exact against this client's registry (the other captured characters
	// have 1-2 entries of live-server data-version drift; see the plan doc).
	public class WeightedAppearanceTests
	{
		private static readonly (string Ref, ushort Tag, byte Category)[] Registry =
		[
			("*beardmesh_3181870", 0x02BB, 4), ("*hairmesh_566206", 0x012C, 3), ("*texscript_1136960", 0x0193, 10), ("*texscript_183875", 0x01B3, 2), // face paint - never emitted in spawns
			("*texscript_202393", 0x00E3, 8), ("*texscript_213934", 0x0112, 7), ("*texscript_3113027", 0x01CF, 6), ("*texscript_3770894", 0x0323, 9)
		];

		// faolanx's editor resource-ref stream (Faolanx_mage-necromancer_create.json, in send order) and his spawn's
		// weighted entry tags (Faolanx_mage-necromancer_login, idx 1-7, all weight 1.0).
		private static readonly string[] FaolanxRefs =
		[
			"*beardmesh_3181870", "*hairmesh_566206", "*texscript_1136960", "*texscript_183875", "*texscript_202393", "*texscript_213934", "*texscript_3113027", "*texscript_3770894"
		];

		private static readonly ushort[] FaolanxWire = [0x02BB, 0x012C, 0x0193, 0x00E3, 0x0112, 0x01CF, 0x0323];

		// There is NO substitute face. Each of these used to return null, and the caller then spliced in Dikketest's
		// captured 6 weighted attributes - a hair the player never chose plus two face-marking texscripts - and
		// rendered it as that character's own appearance. A character now wears what it chose, nothing, or the build
		// fails loudly; it never wears somebody else's.
		[Fact]
		public void BuildWeightedEntries_ChoosingNothingBuildsNothing()
		{
			Assert.Empty(PlayerSpawnScaffoldRows.BuildWeightedEntries(null, RegistryDict()));

			// Only the never-emitted face-paint category (2) resolved: captured spawns never carry it, so an empty
			// entry list is the CORRECT wire, not a missing one.
			Assert.Empty(PlayerSpawnScaffoldRows.BuildWeightedEntries(Resources(["*texscript_183875"]), RegistryDict()));
		}

		// The registry is 1072 rows of static seed content that every spawn resolves through, so its absence is seed
		// corruption - not a cue to dress the character in the capture's face.
		[Fact]
		public void BuildWeightedEntries_WithoutTheRegistry_Throws()
		{
			Assert.Throws<ArgumentNullException>(() => PlayerSpawnScaffoldRows.BuildWeightedEntries(Resources(FaolanxRefs), null!));
		}

		// A ref the registry does not know is content the importer never took in. Dropping it silently cost the
		// character a chosen resource (and, when it was the only one, its whole face).
		[Fact]
		public void BuildWeightedEntries_WithAnUnimportedRef_ThrowsNamingIt()
		{
			var ex = Assert.Throws<InvalidOperationException>(() => PlayerSpawnScaffoldRows.BuildWeightedEntries(Resources(["*texscript_999999"]), RegistryDict()));
			Assert.Contains("*texscript_999999", ex.Message);
		}

		[Fact]
		public void BuildAppearanceEntries_UsesTheResolvedWeightedEntries()
		{
			var character = new Character
			{
				Id = 0x02000007,
				Class = ConanClass.Necromancer,
				BodyResources = Resources(FaolanxRefs)
			}; // Faolanx is a mage-necromancer

			var entries = PlayerSpawnScaffoldRows.BuildAppearanceEntries(character, PlayerSpawnScaffoldRows.BuildWeightedEntries(character.BodyResources, RegistryDict()));

			// 7 weighted + no morphs + the closer at idx 8.
			Assert.Equal(8, entries.Count);
			Assert.Equal(FaolanxWire, entries.Take(7).Select(e => e.Tag));
			Assert.Equal(8u, entries[7].Index);
			Assert.Equal(0x4301, entries[7].Tag); // Necromancer's per-class closer (#250); was the old hardcoded 0x56B6
		}

		[Fact]
		public void FaolanxRefs_ResolveToHisCapturedWeightedEntries()
		{
			var entries = PlayerSpawnScaffoldRows.BuildWeightedEntries(Resources(FaolanxRefs), RegistryDict());

			Assert.Equal(FaolanxWire.Length, entries!.Count);
			for (var i = 0; i < entries.Count; i++)
			{
				Assert.Equal((uint)i + 1, entries[i].Index);
				Assert.Equal(FaolanxWire[i], entries[i].Tag);
				Assert.Equal(0x3F800000u, entries[i].Value); // weight 1.0
			}
		}

		[Fact]
		public void ParseRegistry_ReadsRecordsAndAssignsWireTags()
		{
			// Synthetic registry payload: 24-byte header + 2 records + a truncated tail (as in the shipped client).
			var payload = new List<byte>();
			payload.AddRange(new byte[24]);

			Record("02_BrowLow", 0, 1f, 3);
			Record("*texscript_1108153", 6, 1f, 0x148);
			payload.AddRange(BitConverter.GetBytes(5u));
			payload.AddRange(Encoding.ASCII.GetBytes("trunc"));

			var targets = RdbMorphTargetImporter.ParseRegistry(payload.ToArray());

			Assert.Equal(2, targets.Count);
			Assert.Equal(2u, targets[0].Id); // wire tag = id - 1
			Assert.Equal("02_BrowLow", targets[0].Name);
			Assert.Equal(0x147u, targets[1].Id);
			Assert.Equal(6, targets[1].Category);
			return;

			void Record(string name, uint category, float weight, uint id)
			{
				payload.AddRange(BitConverter.GetBytes((uint)name.Length));
				payload.AddRange(Encoding.ASCII.GetBytes(name));
				payload.AddRange(BitConverter.GetBytes(category));
				payload.AddRange(BitConverter.GetBytes(0u));
				payload.AddRange(BitConverter.GetBytes(weight));
				payload.AddRange(BitConverter.GetBytes(id));
			}
		}

		private static Dictionary<string, MorphTarget> RegistryDict()
		{
			return Registry.ToDictionary(r => r.Ref, r => new MorphTarget
			{
				Id = r.Tag,
				Name = r.Ref,
				Category = r.Category,
				Weight = 1f
			});
		}

		private static List<CharacterBodyResource> Resources(IEnumerable<string> refs)
		{
			return refs
				.Select((r, i) => new CharacterBodyResource
				{
					Ref = r,
					Sequence = i
				})
				.ToList();
		}
	}
}
