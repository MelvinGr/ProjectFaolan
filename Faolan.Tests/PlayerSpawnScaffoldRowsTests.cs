using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;
using Xunit;

namespace Faolan.Tests
{
	// Locks the scaffold <-> DB-rows mapping: extracting a captured spawn into CharacterAttribute /
	// CharacterSkill / CharacterScaffoldPair / CharacterSpawnFrame rows and regenerating from those rows must
	// reproduce the captured body byte-for-byte (the DB-generated player spawn oracle). Buff records are
	// transient and not stored, so a buffed capture round-trips to its unbuffed form.
	public class PlayerSpawnScaffoldRowsTests
	{
		// Buff records are session state, not character data: rows from a buffed capture regenerate the same
		// spawn WITHOUT the buff records (= the parsed scaffold with BuffRecords cleared).
		[Fact]
		public void BuffedSpawn_RoundTripsToItsUnbuffedForm()
		{
			var body = Golden("SpawnHoxxlingBuffed");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var expected, out _));
			expected.BuffRecords.Clear();

			Assert.Equal(Convert.ToHexString(PlayerSpawnScaffoldCodec.Generate(expected)), Convert.ToHexString(RoundTripThroughRows(body)));
		}

		[Theory, InlineData("SpawnDikketestLogin"), InlineData("SpawnNecroL80KingsCastle"), InlineData("SpawnDemoL80RandomActions")]
		public void ExtractThenApply_RegeneratesByteIdentically(string name)
		{
			var body = Golden(name);

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(RoundTripThroughRows(body)));
		}

		// The fresh-character defaults are Dikketest's extracted scaffold, with the morph entries now GENERATED
		// from the character's editor sliders: seeding a character that carries Dikketest's CharacterBody (the
		// raw editor stream from his create capture, zigzag-decoded by CharacterMorphs.TryApply) and generating
		// over his transform/CharacterIIR must reproduce his captured spawn byte-for-byte.
		[Fact]
		public void FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture()
		{
			var body = Golden("SpawnDikketestLogin");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _));

			var character = new Character
			{
				Id = 0x02000005,
				Body = new CharacterBody()
			};
			foreach (var (morph, raw) in new (string, uint)[] { ("arm", 59), ("arse", 14), ("bosom", 30), ("chest", 59), ("leg", 59), ("stomach", 14), ("thigh", 59) })
				Assert.True(CharacterMorphs.TryApply(character.Body, morph, raw));

			Assert.True(PlayerSpawnScaffoldRows.EnsureFreshRows(character));
			Assert.False(PlayerSpawnScaffoldRows.EnsureFreshRows(character)); // idempotent
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, scaffold));

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(PlayerSpawnScaffoldCodec.Generate(scaffold)));
		}

		// A character that never touched a slider (no CharacterBody) gets no morph entries at all: 6 weighted
		// attributes + the closer at idx 7 - the exact entry-table shape of strickenbla's captured spawn.
		[Fact]
		public void FreshRows_WithoutBody_HaveNoMorphEntries()
		{
			var character = new Character { Id = 0x02000006 };
			Assert.True(PlayerSpawnScaffoldRows.EnsureFreshRows(character));

			Assert.Equal(7, character.Attributes.Count);
			var closer = character.Attributes.Single(a => a.Sequence == 6);
			Assert.Equal(7u, closer.StatId);
			Assert.Equal(0x56B6, closer.Tag);
		}

		[Fact]
		public void Template_ExtractThenApply_RegeneratesByteIdentically()
		{
			var body = Support.Golden.Bytes("SpawnStrieTemplate");

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(RoundTripThroughRows(body)));
		}

		[Fact]
		public void TryApplyFrom_WithoutRows_ReturnsFalse()
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Golden("SpawnDikketestLogin"), out var scaffold, out _));

			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(new Character { Id = 1 }, scaffold));
			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(null, scaffold));
		}

		private static byte[] Golden(string name)
		{
			return File.ReadAllBytes(Path.Combine(GoldenDir(), name + ".bin"));
		}

		private static string GoldenDir([CallerFilePath] string thisFile = "")
		{
			return Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");
		}

		private static byte[] RoundTripThroughRows(byte[] body)
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out var error), $"parse failed: {error}");

			var character = new Character { Id = 0x02000005 };
			PlayerSpawnScaffoldRows.ExtractTo(character, scaffold);

			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var rebuilt, out _)); // fresh instance, then rows overwrite
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, rebuilt));
			return PlayerSpawnScaffoldCodec.Generate(rebuilt);
		}
	}
}
