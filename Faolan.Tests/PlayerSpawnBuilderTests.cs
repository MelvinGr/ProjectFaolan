using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Spawn;
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
		public void BuildFromScratch_ReproducesStrieTemplate()
		{
			var template = Golden.Bytes("SpawnStrieTemplate");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(template, out var scaffold, out var error), error);

			// Decompose the captured scaffold into the from-scratch builder's inputs.
			var position = DecodeVec3(scaffold.Vec3);
			var rotation = DecodeQuat(scaffold.Quat);

			// The scaffold rows (entry table / id list / pre-IIR frame) on a Character, as EnsureSpawnScaffoldRows seeds.
			var scaffoldCharacter = new Character { Id = scaffold.Handle };
			PlayerSpawnScaffoldRows.ExtractTo(scaffoldCharacter, scaffold);

			var built = PlayerSpawnBuilder.BuildFromScratch(scaffold.Handle, scaffold.Playfield, position, rotation, scaffold.CharacterIir, scaffoldCharacter);

			Assert.NotNull(built);
			Assert.Equal(template.Length, built.Length);
			Assert.True(template.SequenceEqual(built), "from-scratch player body must be byte-identical to the Strie template");
		}

		[Fact]
		public void BuildFromScratch_NoScaffoldRows_ReturnsNull()
		{
			var template = Golden.Bytes("SpawnStrieTemplate");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(template, out var scaffold, out _));

			// A character with no scaffold rows -> the builder can't fill the frame and falls back (null).
			var built = PlayerSpawnBuilder.BuildFromScratch(scaffold.Handle, scaffold.Playfield, Vector3.Zero, Quaternion.Identity, scaffold.CharacterIir, new Character { Id = 1 });
			Assert.Null(built);
		}

		// The burst-less composition (#210): the server-owned CharacterIIR template overridden with a NEW character's
		// identity (PlayerSpawnMessage) + built into a player body (PlayerSpawnBuilder) must produce a body that
		// re-parses with the overridden name - i.e. the override -> re-encode -> scaffold-assemble chain fits together.
		[Fact]
		public void BuildFromScratch_WithOverriddenIdentity_ParsesBackWithNewName()
		{
			var template = Golden.Bytes("SpawnStrieTemplate");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(template, out var scaffold, out _));

			var message = ((ReadOnlySpan<byte>)scaffold.CharacterIir).FromProtobuf<ProtoCoreCharacterIIR>();
			var character = new Character
			{
				Name = "Newcomer",
				Race = Race.Cimmerian,
				Sex = Sex.Male,
				Level = 1,
				MaxStats = new CharacterMaxStats { Health = 100, Mana = 0, Stamina = 200 }
			};
			PlayerSpawnMessage.ApplyCharacter(message, character, new CharacterEquipment());

			var scaffoldCharacter = new Character { Id = 0x0A1B2C3D };
			PlayerSpawnScaffoldRows.ExtractTo(scaffoldCharacter, scaffold);

			var body = PlayerSpawnBuilder.BuildFromScratch(0x0A1B2C3D, scaffold.Playfield, new Vector3(1, 2, 3), Quaternion.Identity, message.ToProtobufBytes(), scaffoldCharacter);

			Assert.NotNull(body);
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var rebuilt, out var err), err);
			var back = ((ReadOnlySpan<byte>)rebuilt.CharacterIir).FromProtobuf<ProtoCoreCharacterIIR>();
			Assert.Equal("Newcomer", back.Name);
			Assert.Equal(0x0A1B2C3Du, rebuilt.Handle);
		}

		private static Vector3 DecodeVec3(byte[] v) => new(
			BinaryPrimitives.ReadSingleLittleEndian(v.AsSpan(1, 4)),
			BinaryPrimitives.ReadSingleLittleEndian(v.AsSpan(6, 4)),
			BinaryPrimitives.ReadSingleLittleEndian(v.AsSpan(11, 4)));

		private static Quaternion DecodeQuat(byte[] q) => new(
			BinaryPrimitives.ReadSingleLittleEndian(q.AsSpan(1, 4)),
			BinaryPrimitives.ReadSingleLittleEndian(q.AsSpan(6, 4)),
			BinaryPrimitives.ReadSingleLittleEndian(q.AsSpan(11, 4)),
			BinaryPrimitives.ReadSingleLittleEndian(q.AsSpan(16, 4)));
	}
}
