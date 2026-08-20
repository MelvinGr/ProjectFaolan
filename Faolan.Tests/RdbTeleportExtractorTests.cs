using System.Globalization;
using System.Linq;
using System.Threading;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// The client's own cross-playfield teleport table, RDB 1000087 (Gitea #883). Driven from a literal rather than
	// from the client, so the grammar is gated in a plain clone - the shape below is copied from the real record.
	//
	// What the SHIPPED data looks like is deliberately not asserted here: that is a fact about `faolan.db` against a
	// client we may or may not hold, and it belongs in `--report teleport-match`, not in a test (CLAUDE.md).
	public class RdbTeleportExtractorTests
	{
		private const string Sample = """
			<?xml version="1.0" encoding="utf-8"?>
			<teleportdata>
				<teleports>
					<teleportplayfield ExportGenesisVersion="Genesis 1.10.82" ExportUser="pers" ExportDate="19/11/2008" ExportDataset="59862" SourcePlayfieldID="1">
						<teleport SourcePosX="25.000000" SourcePosY="10.089800" SourcePosZ="35.000000" DestinationPlayfieldID="2"/>
					</teleportplayfield>
					<teleportplayfield SourcePlayfieldID="45">
						<teleport SourcePosX="-118.500000" SourcePosY="0.000000" SourcePosZ="643.250000" DestinationPlayfieldID="4020"/>
						<teleport SourcePosX="640.000000" SourcePosY="12.500000" SourcePosZ="1.000000" DestinationPlayfieldID="45"/>
					</teleportplayfield>
				</teleports>
			</teleportdata>
			""";

		[Fact]
		public void Parse_ReadsEveryTeleportUnderItsOwnSourcePlayfield()
		{
			var rows = RdbTeleportExtractor.Parse(Sample);

			Assert.Equal(3, rows.Count);
			Assert.Equal([1u, 45u, 45u], rows.Select(r => r.SourceMapId));
			Assert.Equal([2u, 4020u, 45u], rows.Select(r => r.DestMapId));

			var first = rows[0];
			Assert.Equal(25f, first.X);
			Assert.Equal(10.0898f, first.Y, 4);
			Assert.Equal(35f, first.Z);
		}

		// The source position is the only key back to a `WorldObject` row - the table carries no door name - so a
		// dropped sign would silently match the wrong door rather than fail.
		[Fact]
		public void Parse_KeepsNegativeCoordinates()
		{
			var row = RdbTeleportExtractor.Parse(Sample)[1];

			Assert.Equal(-118.5f, row.X);
			Assert.Equal(643.25f, row.Z);
		}

		// A same-playfield row is legal and is what separates `Teleport` from `ProxyTeleport` downstream.
		[Fact]
		public void Parse_AllowsATeleportBackIntoItsOwnPlayfield()
		{
			var row = RdbTeleportExtractor.Parse(Sample)[2];

			Assert.Equal(row.SourceMapId, row.DestMapId);
		}

		// ⚠️ The record's floats are `.`-decimal regardless of the machine's locale. Parsed under a comma-decimal
		// culture, an invariant-less parse turns 25.000000 into 25000000 and puts the door 25 km away - a silent
		// mismatch, not an exception.
		[Fact]
		public void Parse_IsInvariantOfTheCurrentCulture()
		{
			var previous = Thread.CurrentThread.CurrentCulture;
			try
			{
				Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
				Assert.Equal(10.0898f, RdbTeleportExtractor.Parse(Sample)[0].Y, 4);
			}
			finally
			{
				Thread.CurrentThread.CurrentCulture = previous;
			}
		}

		// 230 of the record's 528 `<teleportplayfield>` blocks carry no teleport at all; they must contribute nothing
		// rather than throw or emit a zero row.
		[Fact]
		public void Parse_IgnoresAPlayfieldWithNoTeleports()
		{
			const string empty = """
				<teleportdata><teleports>
					<teleportplayfield SourcePlayfieldID="7"/>
					<teleportplayfield SourcePlayfieldID="8"></teleportplayfield>
					<teleportplayfield SourcePlayfieldID="9">
						<teleport SourcePosX="1.0" SourcePosY="2.0" SourcePosZ="3.0" DestinationPlayfieldID="10"/>
					</teleportplayfield>
				</teleports></teleportdata>
				""";

			var rows = RdbTeleportExtractor.Parse(empty);

			Assert.Single(rows);
			Assert.Equal(9u, rows[0].SourceMapId);
		}

		[Fact]
		public void Parse_OfAnEmptyTableIsEmptyRatherThanAThrow()
		{
			Assert.Empty(RdbTeleportExtractor.Parse("<teleportdata><teleports/></teleportdata>"));
		}
	}
}
