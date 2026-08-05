using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// #341: a zone-line door names the playfield behind it, so the client's playfield table resolves the doors no
	// capture walked through. Locks the three decisions that pass makes - what the name asks for, when it is not a
	// zone-line at all, and which of several same-named playfields it means.
	public class ZoneLineNamesTests
	{
		// The level designers prefix some zone-lines with "To "; both spellings ask for the same playfield.
		[Theory, InlineData("Conall's Valley", "Conall's Valley"), InlineData("To Lacheish Plains", "Lacheish Plains"),
		 InlineData("  To Ymir's Pass  ", "Ymir's Pass"), InlineData("Tarantia Common District ", "Tarantia Common District")]
		public void Key_StripsTheToPrefixAndSurroundingSpace(string doorName, string expected)
			=> Assert.Equal(expected, ZoneLineNames.Key(doorName));

		[Theory, InlineData(null), InlineData(""), InlineData("   ")]
		public void Key_IsNullWhenTheDoorCarriesNoUsableName(string? doorName)
			=> Assert.Null(ZoneLineNames.Key(doorName));

		// "Torch" is not a playfield, so it never reaches a lookup - Key only normalises, it does not judge.
		[Fact]
		public void Key_DoesNotJudgeWhetherTheNameIsAPlayfield()
			=> Assert.Equal("Lever", ZoneLineNames.Key("Lever"));

		// The City of Tortage doors INSIDE City of Tortage (day 4020 and night 4022) relocate the player within the
		// playfield they stand in. The night map is a different id, so only the base name catches it.
		[Theory, InlineData("City of Tortage", "City of Tortage"), InlineData("City of Tortage", "City of Tortage Night"),
		 InlineData("Old Tarantia", "Old Tarantia Day")]
		public void LeadsBackIntoOwnPlayfield_MatchesTheDayNightVariantsOfItsOwnMap(string key, string sourceMapName)
			=> Assert.True(ZoneLineNames.LeadsBackIntoOwnPlayfield(key, sourceMapName));

		[Theory, InlineData("Conall's Valley", "Conarch Village"), InlineData("Ymir's Pass", null), InlineData("Ymir's Pass", "")]
		public void LeadsBackIntoOwnPlayfield_IsFalseForARealZoneLine(string key, string? sourceMapName)
			=> Assert.False(ZoneLineNames.LeadsBackIntoOwnPlayfield(key, sourceMapName));

		[Fact]
		public void PickDestination_TakesTheOnlyCandidate()
			=> Assert.Equal(1020u, ZoneLineNames.PickDestination([new ZoneLineNames.MapCandidate(1020, true)]));

		// "Conall's Valley" is playfields 508 and 1010; only 1010 defines respawn points, and 1010 is what the
		// captured door actually teleported to.
		[Fact]
		public void PickDestination_DropsTheContentFreeDuplicatePlayfields()
			=> Assert.Equal(1010u, ZoneLineNames.PickDestination([
				new ZoneLineNames.MapCandidate(508, false),
				new ZoneLineNames.MapCandidate(1010, true)
			]));

		// Outflow Tunnels 2026/2027 both carry a respawn point: a coin flip, so it stays unresolved and the server
		// reports the gap on use instead of sending the player to the wrong one.
		[Fact]
		public void PickDestination_RefusesAGenuineTie()
			=> Assert.Null(ZoneLineNames.PickDestination([
				new ZoneLineNames.MapCandidate(2026, true),
				new ZoneLineNames.MapCandidate(2027, true)
			]));

		// Thirsty Dog Inn 4533/4534 define nothing at all: with no landable candidate the tie-break has nothing to
		// break, and two candidates remain two candidates.
		[Fact]
		public void PickDestination_RefusesATieBetweenPlayfieldsThatDefineNothing()
			=> Assert.Null(ZoneLineNames.PickDestination([
				new ZoneLineNames.MapCandidate(4533, false),
				new ZoneLineNames.MapCandidate(4534, false)
			]));

		// A lone playfield with no respawn point is still the answer - the runtime reports "no spawn point defined
		// for map X", which names the gap far better than a silent door-open animation.
		[Fact]
		public void PickDestination_TakesALoneCandidateEvenWithNoSpawnPoint()
			=> Assert.Equal(2033u, ZoneLineNames.PickDestination([new ZoneLineNames.MapCandidate(2033, false)]));

		[Fact]
		public void PickDestination_IsNullWhenTheNameMatchesNoPlayfield()
			=> Assert.Null(ZoneLineNames.PickDestination([]));
	}
}
