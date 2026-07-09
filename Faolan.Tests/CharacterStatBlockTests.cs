using System;
using System.Buffers.Binary;
using Faolan.Core.Enums;
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
		public void Build_VitalsBlock_MatchesCapturedBytes()
		{
			// Captured packet sends the CURRENT ids (0x1B/0x1F8/0x1FA) - an entity announcing its full vitals on
			// spawn (current == max at full health; ids in-client verified, see CharacterStatId).
			var block = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63).Set(CharacterStatId.StaminaCurrent, 63).Set(CharacterStatId.ManaCurrent, 63);

			Assert.Equal(3, block.Count);

			// count(03) + HealthCurrent(001B)=189C + StaminaCurrent(01F8)=189C + ManaCurrent(01FA)=189C
			Assert.Equal("030000001b0000189c000001f80000189c000001fa0000189c", ToHex(block.ToArray()));
		}

		[Fact]
		public void BuildStatBlockBody_NpcCombatMode_MatchesCapturedFrame()
		{
			// strickenbla combat capture frame 6424: the combat-mode (weapon-draw) update on beach mob 0x00002C56 the
			// tick before its first swing - stat 0x14 (CombatMode) = 4. The full IIR body (after the u32 length prefix)
			// is [96b8dc59][0000c350][handle][00000000][count=1][statId 0x14][value 4]. Decodes the empty-handed-swing
			// fix (#218); see WorldBroadcaster.BroadcastNpcCombatMode.
			var stats = new CharacterStatBlock().Set(CharacterStatId.CombatMode, 4);
			var body = StatUpdateCodec.BuildBody(0x00002C56u, stats);

			Assert.Equal("96b8dc590000c35000002c5600000000010000001400000004", ToHex(body));
		}

		[Fact]
		public void Set_AppliesVitalsScale()
		{
			// HealthCurrent (0x001B) is a ×100 vital: display 63 -> raw 6300 (0x189C).
			var block = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63);
			Assert.Equal("010000001b0000189c", ToHex(block.ToArray()));
		}

		[Fact]
		public void SetRaw_DoesNotApplyScale()
		{
			var block = new CharacterStatBlock().SetRaw(CharacterStatId.Level, 25);

			// count(01) + Level(0036) = 25 (0x19), unscaled
			Assert.Equal("010000003600000019", ToHex(block.ToArray()));
		}

		[Fact]
		public void StatUpdateCodec_Body_RejectsWrongSubOpcode()
		{
			var body = new byte[17];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), 0x0000C350u);
			Assert.False(StatUpdateCodec.TryParseBody(body, out _, out _));
		}

		[Fact]
		public void StatUpdateCodec_Body_RoundTrips()
		{
			// Reconstruct the 0x96B8DC59 IIR body (the bytes wrapped by WriteArrayUInt32Length) and parse it back.
			var stats = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63).Set(CharacterStatId.StaminaCurrent, 63).Set(CharacterStatId.ManaCurrent, 63);
			const uint handle = 0x0002324B;

			var statBytes = stats.ToArray();
			var body = new byte[16 + statBytes.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), 0x96B8DC59u); // StatUpdateIIR
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), 0x0000C350u); // VitalsMarker
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), handle);
			// body[12..16] left zero (the constant padding word)
			statBytes.CopyTo(body, 16);

			Assert.True(StatUpdateCodec.TryParseBody(body, out var parsedHandle, out var parsedStats));
			Assert.Equal(handle, parsedHandle);
			Assert.Equal(ToHex(statBytes), ToHex(parsedStats.ToArray()));
		}

		[Fact]
		public void TryParse_ReadsCapturedVitalsBlock()
		{
			// The exact captured block bytes from Build_VitalsBlock_MatchesCapturedBytes.
			var bytes = FromHex("030000001b0000189c000001f80000189c000001fa0000189c");

			Assert.True(CharacterStatBlock.TryParse(bytes, out var block, out var consumed));
			Assert.Equal(bytes.Length, consumed);
			Assert.Equal(3, block.Count);
			Assert.Equal((CharacterStatId.HealthCurrent, 6300u), block.Records[0]);
			Assert.Equal((CharacterStatId.StaminaCurrent, 6300u), block.Records[1]);
			Assert.Equal((CharacterStatId.ManaCurrent, 6300u), block.Records[2]);
		}

		[Fact]
		public void TryParse_ReturnsFalseWhenTruncated()
		{
			// count says 2 records (needs 17 bytes) but only one is present.
			var bytes = FromHex("020000001b0000189c");
			Assert.False(CharacterStatBlock.TryParse(bytes, out _, out var consumed));
			Assert.Equal(0, consumed);
		}

		[Fact]
		public void TryParse_RoundTripsBuild_AndReportsConsumed()
		{
			var block = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, 63).SetRaw(CharacterStatId.Level, 25);
			var built = block.ToArray();

			// Append trailing bytes: TryParse must consume only the block and report the right length.
			var withTrailer = new byte[built.Length + 3];
			built.CopyTo(withTrailer, 0);

			Assert.True(CharacterStatBlock.TryParse(withTrailer, out var parsed, out var consumed));
			Assert.Equal(built.Length, consumed);
			Assert.Equal(ToHex(built), ToHex(parsed.ToArray()));
		}

		private static byte[] FromHex(string hex)
		{
			var bytes = new byte[hex.Length / 2];
			for (var i = 0; i < bytes.Length; i++)
				bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
			return bytes;
		}

		private static string ToHex(byte[] bytes)
		{
			var chars = new char[bytes.Length * 2];
			const string hex = "0123456789abcdef";
			for (var i = 0; i < bytes.Length; i++)
			{
				chars[i * 2] = hex[bytes[i] >> 4];
				chars[i * 2 + 1] = hex[bytes[i] & 0xF];
			}

			return new string(chars);
		}
	}
}
