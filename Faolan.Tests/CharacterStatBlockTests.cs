using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// The stat-record block is a 1-byte count followed by N × [uint32 statId BE][uint32 value BE], with vitals
	// scaled ×100. Verified against a captured 0x96B8DC59 vitals packet (entity 0x0002324B, Health/Stamina/Mana
	// all 63.00 -> raw 6300 = 0x189C). See docs/re/quests-and-combat.md.
	public class CharacterStatBlockTests
	{
		[Fact]
		public void BuildStatBlockBody_NpcCombatMode_MatchesCapturedFrame()
		{
			// strickenbla combat capture frame 6424: the combat-mode (weapon-draw) update on beach mob 0x00002C56 the
			// tick before its first swing - stat 0x14 (CombatMode) = 4. The full IIR body (after the u32 length prefix)
			// is [96b8dc59][0000c350][handle][00000000][count=1][statId 0x14][value 4]. Decodes the empty-handed-swing
			// fix; see WorldBroadcaster.BroadcastNpcCombatMode.
			var stats = new CharacterStatBlock().Set(CharacterStatId.CombatMode, 4);
			var body = StatUpdateCodec.Build(0x00002C56u, stats);

			Assert.Equal("96b8dc590000c35000002c5600000000010000001400000004", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void Build_VitalsBlock_MatchesCapturedBytes()
		{
			// Captured packet sends the current ids (0x1B/0x1F8/0x1FA) - an entity announcing its full vitals on
			// spawn (current == max at full health; ids in-client verified, see CharacterStatId).
			var block = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63).Set(CharacterStatId.StaminaCurrent, 63).Set(CharacterStatId.ManaCurrent, 63);

			Assert.Equal(3, block.Count);

			// count(03) + HealthCurrent(001B)=189C + StaminaCurrent(01F8)=189C + ManaCurrent(01FA)=189C
			Assert.Equal("030000001b0000189c000001f80000189c000001fa0000189c", Convert.ToHexStringLower(CharacterStatBlockCodec.Build(block)));
		}

		[Fact]
		public void SetRaw_DoesNotApplyScale()
		{
			var block = new CharacterStatBlock().SetRaw(CharacterStatId.Level, 25);

			// count(01) + Level(0036) = 25 (0x19), unscaled
			Assert.Equal("010000003600000019", Convert.ToHexStringLower(CharacterStatBlockCodec.Build(block)));
		}

		[Fact]
		public void Set_AppliesVitalsScale()
		{
			// HealthCurrent (0x001B) is a ×100 vital: display 63 -> raw 6300 (0x189C).
			var block = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63);
			Assert.Equal("010000001b0000189c", Convert.ToHexStringLower(CharacterStatBlockCodec.Build(block)));
		}

		[Fact]
		public void StatUpdateCodec_Body_RejectsWrongSubOpcode()
		{
			var body = new byte[17];
			body.WriteU32Be(4, 0x0000C350u);
			Assert.Throws<CodecParseException>(() => StatUpdateCodec.Parse(body));
		}

		[Fact]
		public void StatUpdateCodec_Body_RoundTrips()
		{
			// Reconstruct the 0x96B8DC59 IIR body (the bytes wrapped by WriteArrayUInt32Length) and parse it back.
			var stats = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63).Set(CharacterStatId.StaminaCurrent, 63).Set(CharacterStatId.ManaCurrent, 63);
			const uint handle = 0x0002324B;

			var statBytes = CharacterStatBlockCodec.Build(stats);
			var body = new byte[16 + statBytes.Length];
			body.WriteU32Be(0, 0x96B8DC59u); // StatUpdateIIR
			body.WriteU32Be(4, 0x0000C350u); // VitalsMarker
			body.WriteU32Be(8, handle);
			// body[12..16] left zero (the constant padding word)
			statBytes.CopyTo(body, 16);

			var parsed = StatUpdateCodec.Parse(body);
			var (parsedHandle, parsedStats) = (parsed.EntityHandle, parsed.Stats);
			Assert.Equal(handle, parsedHandle);
			Assert.Equal(Convert.ToHexStringLower(statBytes), Convert.ToHexStringLower(CharacterStatBlockCodec.Build(parsedStats)));
		}

		[Fact]
		public void Parse_ReadsCapturedVitalsBlock()
		{
			// The exact captured block bytes from Build_VitalsBlock_MatchesCapturedBytes.
			var bytes = Convert.FromHexString("030000001b0000189c000001f80000189c000001fa0000189c");

			var block = CharacterStatBlockCodec.Parse(bytes);
			Assert.Equal(3, block.Count);
			Assert.Equal((CharacterStatId.HealthCurrent, 6300u), block.Records[0]);
			Assert.Equal((CharacterStatId.StaminaCurrent, 6300u), block.Records[1]);
			Assert.Equal((CharacterStatId.ManaCurrent, 6300u), block.Records[2]);
		}

		// Rule 4: a short body is a decode failure, not a `false` for the caller to drop on the floor.
		[Fact]
		public void Parse_ThrowsWhenTruncated()
		{
			// count says 2 records (needs 17 bytes) but only one is present.
			var bytes = Convert.FromHexString("020000001b0000189c");
			Assert.Throws<CodecParseException>(() => CharacterStatBlockCodec.Parse(bytes));
		}

		// Rule 3: the codec accounts for the whole body, so trailing bytes are a failure rather than a length to
		// report back. A caller that composes it inside a bigger body uses the internal reader instead.
		[Fact]
		public void Parse_ThrowsOnTrailingBytes()
		{
			var block = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63).SetRaw(CharacterStatId.Level, 25);
			var withTrailer = new byte[CharacterStatBlockCodec.Build(block).Length + 3];
			CharacterStatBlockCodec.Build(block).CopyTo(withTrailer, 0);

			Assert.Throws<CodecParseException>(() => CharacterStatBlockCodec.Parse(withTrailer));
		}

		[Fact]
		public void Parse_RoundTripsBuild()
		{
			var block = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63).SetRaw(CharacterStatId.Level, 25);
			var built = CharacterStatBlockCodec.Build(block);

			Assert.Equal(Convert.ToHexStringLower(built), Convert.ToHexStringLower(CharacterStatBlockCodec.Build(CharacterStatBlockCodec.Parse(built))));
		}

		// ApprenticeLevel (0x3C5) is the mentor system's effective level, not a copy of Level - but we do not model
		// mentoring, so the two are equal for every character we serve and a level change has to re-state both. The
		// over-head nameplate renders max(0x36, 0x3C5) (client nameplate text builder FUN_011d8b00), so a push that
		// moved only Level left a level-down showing the stale higher number over the health bar while the character
		// sheet (0x36 alone) showed the new one.
		[Fact]
		public void SetLevel_RestatesTheUnmentoredApprenticeLevelAlongsideLevel()
		{
			var block = new CharacterStatBlock().SetLevel(10);

			Assert.Equal(10u, block[CharacterStatId.Level]);
			Assert.Equal(10u, block[CharacterStatId.ApprenticeLevel]);
			// Both are scale-1 stats, so the wire values are the level itself - the client's max() of two equal
			// numbers is the level whichever way it resolves it.
			Assert.Equal([(CharacterStatId.Level, 10u), (CharacterStatId.ApprenticeLevel, 10u)], block.Records);
		}
	}
}
