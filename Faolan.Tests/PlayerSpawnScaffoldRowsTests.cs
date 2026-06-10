using System;
using System.IO;
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
		private static string GoldenDir([CallerFilePath] string thisFile = "") => Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");

		private static byte[] Golden(string name) => File.ReadAllBytes(Path.Combine(GoldenDir(), name + ".bin"));

		private static byte[] RoundTripThroughRows(byte[] body)
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out var error), $"parse failed: {error}");

			var character = new Character { Id = 0x02000005 };
			PlayerSpawnScaffoldRows.ExtractTo(character, scaffold);

			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var rebuilt, out _)); // fresh instance, then rows overwrite
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, rebuilt));
			return PlayerSpawnScaffoldCodec.Generate(rebuilt);
		}

		[Theory]
		[InlineData("SpawnDikketestLogin")]
		[InlineData("SpawnNecroL80KingsCastle")]
		[InlineData("SpawnDemoL80RandomActions")]
		public void ExtractThenApply_RegeneratesByteIdentically(string name)
		{
			var body = Golden(name);

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(RoundTripThroughRows(body)));
		}

		[Fact]
		public void Template_ExtractThenApply_RegeneratesByteIdentically()
		{
			var body = SpawnPacketBuilder.PlayerTemplateBytes;

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(RoundTripThroughRows(body)));
		}

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

		// The fresh-character defaults ARE Dikketest's extracted scaffold, so seeding a blank character and
		// generating over Dikketest's transform/CharacterIIR must reproduce his captured spawn byte-for-byte.
		[Fact]
		public void FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture()
		{
			var body = Golden("SpawnDikketestLogin");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _));

			var character = new Character { Id = 0x02000005 };
			Assert.True(PlayerSpawnScaffoldRows.EnsureFreshRows(character));
			Assert.False(PlayerSpawnScaffoldRows.EnsureFreshRows(character)); // idempotent
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, scaffold));

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(PlayerSpawnScaffoldCodec.Generate(scaffold)));
		}

		[Fact]
		public void TryApplyFrom_WithoutRows_ReturnsFalse()
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Golden("SpawnDikketestLogin"), out var scaffold, out _));

			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(new Character { Id = 1 }, scaffold));
			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(null, scaffold));
		}
	}
}
