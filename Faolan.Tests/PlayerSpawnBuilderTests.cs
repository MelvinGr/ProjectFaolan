using System;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the from-scratch player-spawn BODY builder (#210, the player analog of #185): assembled entirely from a
	// typed PlayerSpawnScaffold (session/transform fields + the DB scaffold rows + the CharacterIIR), with NO captured
	// template. Proven by decomposing SpawnStrieTemplate.bin into its parts (transform, CharacterIIR, scaffold rows)
	// and rebuilding it byte-for-byte - so the fresh-char constants (FieldA=0, no Scale, the transform encoding) and
	// the rows->wire assembly reproduce the capture.
	public class PlayerSpawnBuilderTests
	{
		[Fact]
		public void BuildFromScratch_NoScaffoldRows_Throws()
		{
			var template = Golden.Bytes("SpawnStrieTemplate");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(template, out var scaffold, out _));

			// A character with no scaffold rows cannot be described, and there is no longer a captured-splice path to
			// fall back to: the rows are seeded at character creation, so their absence is corruption and it throws.
			// Returning null instead used to mean a player nobody could see, reported only as a log warning.
			var ex = Assert.Throws<InvalidOperationException>(() => PlayerSpawnBuilder.BuildFromScratch(scaffold.Handle, scaffold.Playfield, Vector3.Zero, Quaternion.Identity,
				scaffold.CharacterIir, new Character
				{
					Id = 1
				}));
			Assert.Contains("no spawn-scaffold rows", ex.Message);
		}

		[Fact]
		public void BuildFromScratch_ReproducesStrieTemplate()
		{
			var template = Golden.Bytes("SpawnStrieTemplate");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(template, out var scaffold, out var error), error);

			// Decompose the captured scaffold into the from-scratch builder's inputs.
			var position = scaffold.Position;
			var rotation = scaffold.Rotation;

			// The scaffold rows (entry table / id list) on a Character. Strie is an Assassin; the pre-IIR frame is now
			// reconstructed from that class (not stored), so the from-scratch body equals the template's CONSTRUCTED
			// form - same transform/rows/IIR, canonical FreshPreFrame(Assassin), EntryTableX 0xB7, no buff records.
			var scaffoldCharacter = new Character
			{
				Id = scaffold.Handle,
				Class = ConanClass.Assassin
			};
			PlayerSpawnScaffoldRows.ExtractTo(scaffoldCharacter, scaffold);

			var built = PlayerSpawnBuilder.BuildFromScratch(scaffold.Handle, scaffold.Playfield, position, rotation, scaffold.CharacterIir, scaffoldCharacter);

			Assert.NotNull(built);
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(template, out var expected, out _));
			expected.PreFrame = PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Assassin);
			expected.EntryTableX = 0xB7;
			expected.BuffRecords.Clear();
			Assert.Equal(Convert.ToHexString(PlayerSpawnScaffoldCodec.Generate(expected)), Convert.ToHexString(built));
		}

		// The burst-less composition (#210): the from-code CharacterIIR built for a NEW character (PlayerCharacterIir.
		// Build) + assembled into a player body (PlayerSpawnBuilder) must produce a body that re-parses with that
		// character's name - i.e. the build -> re-encode -> scaffold-assemble chain fits together.
		[Fact]
		public void BuildFromScratch_WithOverriddenIdentity_ParsesBackWithNewName()
		{
			var template = Golden.Bytes("SpawnStrieTemplate");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(template, out var scaffold, out _));

			var character = new Character
			{
				Name = "Newcomer",
				Race = Race.Cimmerian,
				Sex = Sex.Male,
				Level = 1,
				MaxStats = new CharacterMaxStats
				{
					Health = 100,
					Mana = 0,
					Stamina = 200
				}
			};
			var message = PlayerCharacterIir.Build(character, new CharacterEquipment());

			var scaffoldCharacter = new Character
			{
				Id = 0x0A1B2C3D
			};
			PlayerSpawnScaffoldRows.ExtractTo(scaffoldCharacter, scaffold);

			var body = PlayerSpawnBuilder.BuildFromScratch(0x0A1B2C3D, scaffold.Playfield, new Vector3(1, 2, 3), Quaternion.Identity, message.ToProtobuf(), scaffoldCharacter);

			Assert.NotNull(body);
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var rebuilt, out var err), err);
			var back = rebuilt.CharacterIir.FromProtobuf<ProtoCoreCharacterIIR>();
			Assert.Equal("Newcomer", back.Name);
			Assert.Equal(0x0A1B2C3Du, rebuilt.Handle);
		}
	}
}
