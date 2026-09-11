using System;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the from-scratch player-spawn body builder: assembled entirely from a
	// typed SimpleCharFullUpdateIIR (session/transform fields + the DB scaffold rows + the CharacterIIR), with no captured
	// template. Proven by decomposing SpawnAssassinTemplate.bin into its parts (transform, CharacterIIR, scaffold rows)
	// and rebuilding it byte-for-byte - so the fresh-char constants (FieldA=0, no Scale, the transform encoding) and
	// the rows->wire assembly reproduce the capture.
	public class PlayerSpawnBuilderTests
	{
		[Fact]
		public void BuildFromScratch_NoScaffoldRows_Throws()
		{
			var scaffold = SimpleCharFullUpdateCodec.Parse(Golden.Bytes("SpawnAssassinTemplate").AsSpan(4));

			// A character with no scaffold rows cannot be described, and there is no captured-splice path to fall back
			// to: the rows are seeded at character creation, so their absence is corruption and it throws. Returning
			// null instead would mean a player nobody can see, reported only as a log warning.
			var ex = Assert.Throws<InvalidOperationException>(() => PlayerSpawnBuilder.BuildFromScratch(scaffold.Handle, scaffold.Playfield, Vector3.Zero, Quaternion.Identity,
				scaffold.Character, new Character
				{
					Id = 1
				}, MorphRegistry.ByName));
			Assert.Contains("no spawn-scaffold rows", ex.Message);
		}

		[Fact]
		public void BuildFromScratch_ReproducesStrieTemplate()
		{
			var template = Golden.Bytes("SpawnAssassinTemplate").AsSpan(4).ToArray();
			var scaffold = SimpleCharFullUpdateCodec.Parse(template);

			// Decompose the captured scaffold into the from-scratch builder's inputs.
			var position = scaffold.Position;
			var rotation = scaffold.Rotation;

			// The scaffold rows (id list / feats) plus the appearance model (Body morphs + BodyResources refs) on a
			// Character. Strie is an Assassin; the pre-IIR frame is reconstructed from that class rather than stored, so
			// the from-scratch body equals the template's constructed form - same transform/rows/IIR, canonical
			// FreshPreFrame(Assassin), no buff records.
			//
			// The entry table is not carried across as rows: extraction turns Strie's six weighted entries into resource
			// refs through the catalog, and generation resolves those refs back to the same six tags. So this proves the
			// derivation is lossless, not merely that a row copy round-trips.
			var scaffoldCharacter = new Character
			{
				Id = scaffold.Handle,
				Class = ConanClass.Assassin
			};
			PlayerSpawnScaffoldRows.ExtractTo(scaffoldCharacter, scaffold, MorphRegistry.RefsByTag);

			var built = PlayerSpawnBuilder.BuildFromScratch(scaffold.Handle, scaffold.Playfield, position, rotation, scaffold.Character, scaffoldCharacter, MorphRegistry.ByName);

			Assert.NotNull(built);
			var expected = SimpleCharFullUpdateCodec.Parse(template);
			expected.PreIir = PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Assassin);
			expected.Post.BuffRecords.Clear();
			Assert.Equal(Convert.ToHexString(SimpleCharFullUpdateCodec.Build(expected)), Convert.ToHexString(built));
		}

		// The burst-less composition: the from-code CharacterIIR built for a new character (PlayerCharacterIirs.
		// Build) + assembled into a player body (PlayerSpawnBuilder) must produce a body that re-parses with that
		// character's name - i.e. the build -> re-encode -> scaffold-assemble chain fits together.
		[Fact]
		public void BuildFromScratch_WithOverriddenIdentity_ParsesBackWithNewName()
		{
			var scaffold = SimpleCharFullUpdateCodec.Parse(Golden.Bytes("SpawnAssassinTemplate").AsSpan(4));

			var character = new Character
			{
				Name = "Newcomer",
				Race = Race.Cimmerian,
				Sex = Sex.Male,
				Level = 1,
				Class = ConanClass.Guardian
			};
			var message = PlayerCharacterIirs.ForCharacter(character, worn: null);

			var scaffoldCharacter = new Character
			{
				Id = 0x0A1B2C3D
			};
			PlayerSpawnScaffoldRows.ExtractTo(scaffoldCharacter, scaffold, MorphRegistry.RefsByTag);

			var body = PlayerSpawnBuilder.BuildFromScratch(0x0A1B2C3D, scaffold.Playfield, new Vector3(1, 2, 3), Quaternion.Identity, message, scaffoldCharacter, MorphRegistry.ByName);

			Assert.NotNull(body);
			var rebuilt = SimpleCharFullUpdateCodec.Parse(body);
			Assert.Equal("Newcomer", rebuilt.Character.Name);
			Assert.Equal(0x0A1B2C3Du, rebuilt.Handle);
		}
	}
}
