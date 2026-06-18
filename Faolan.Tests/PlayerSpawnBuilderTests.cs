using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
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
