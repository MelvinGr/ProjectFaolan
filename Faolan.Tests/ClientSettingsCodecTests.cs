using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.PlayerAgent;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The mid-session settings/preview upload family (Gitea #611), against the captured bodies.
	//
	// Oracles, all RPC bodies lifted straight out of the corpus:
	//   assets/playeragent-update-character-settings.bin      Strie_rogue-assassin_login_randomActions frame 7031
	//   assets/playeragent-update-character-preview.bin       ... frame 7032 (the same mid-session socket)
	//   assets/playeragent-update-subscription-settings.bin   AssassinL23_..._randomActions-resurect frame 5439
	//   assets/playeragent-send-character-preview.bin         Faolanx_mage-necromancer_login frame 8 (s2c)
	//
	// What makes these binding is the ROUND TRIP: the buffer is a zlib stream, and a deflate stream is not a function
	// of its contents, so the only way Build can reproduce retail's bytes is by carrying the payload verbatim - which
	// is the same reason the md5 below can be recomputed at all.
	public class ClientSettingsCodecTests
	{
		[Fact]
		public void CharacterSettings_RoundTripsTheCapturedUpload()
		{
			var body = TestPaths.AssetBytes("playeragent-update-character-settings.bin");

			var value = UpdateCharacterSettingsCodec.Parse(body);

			Assert.Equal(0x02165EFFu, value.CharacterId);
			Assert.Equal(["Chat\\Windows.xml", "DockAreas\\SideBarLeft.xml", "DockAreas\\SideBarRight.xml", "Prefs_2.xml"], value.Buffer.Files.Select(f => f.Name));
			Assert.Equal(32571, value.Buffer.Files.Sum(f => f.Data.Length + f.Name.Length + 8));
			Assert.StartsWith("<Archive>", Encoding.Latin1.GetString(value.Buffer.Files[0].Data));

			// The client's own md5 is over the COMPRESSED payload - marker and inflated-size word included.
			Assert.Equal("78d5cd1bc7fc9820df6cc6a94281e42c", value.CheckSum);
			Assert.Equal(value.CheckSum, value.Buffer.CheckSum);

			Assert.Equal(body, UpdateCharacterSettingsCodec.Build(value));
		}

		[Fact]
		public void SubscriptionSettings_RoundTripsTheCapturedUpload()
		{
			var body = TestPaths.AssetBytes("playeragent-update-subscription-settings.bin");

			var value = UpdateSubscriptionSettingsCodec.Parse(body);

			// Account-wide, so no character id at all - the socket's authentication is the addressing.
			Assert.Equal(["hotkeys.xml", "Prefs_2.xml"], value.Buffer.Files.Select(f => f.Name));
			Assert.Contains("AudioBackgroundMusicFreq", Encoding.Latin1.GetString(value.Buffer.Files[1].Data));
			Assert.Equal("8e4de0bd2cbe51411d952e08dc6cc101", value.CheckSum);
			Assert.Equal(value.CheckSum, value.Buffer.CheckSum);

			Assert.Equal(body, UpdateSubscriptionSettingsCodec.Build(value));
		}

		[Fact]
		public void CharacterPreview_RoundTripsBothDirections()
		{
			var upload = TestPaths.AssetBytes("playeragent-update-character-preview.bin");
			var served = TestPaths.AssetBytes("playeragent-send-character-preview.bin");

			var uploaded = UpdateCharacterPreviewCodec.Parse(upload);
			Assert.Equal(0x02165EFFu, uploaded.CharacterId);
			Assert.Equal(["preview.bin"], uploaded.Buffer.Files.Select(f => f.Name));
			Assert.Equal(upload, UpdateCharacterPreviewCodec.Build(uploaded));

			// The s2c body is the SAME layout - which is what lets the server keep the upload verbatim and serve it
			// straight back rather than re-authoring an appearance record it cannot yet decode.
			var sent = SendCharacterPreviewCodec.Parse(served);
			Assert.Equal(0x110099BDu, sent.CharacterId);
			Assert.Equal(["preview.bin"], sent.Buffer.Files.Select(f => f.Name));
			Assert.Equal(served, SendCharacterPreviewCodec.Build(sent));

			// ... and the two codecs really do agree on it: serving the UPLOADED buffer under the s2c method reproduces
			// the upload's bytes exactly, which is what the store-and-echo path does at login.
			Assert.Equal(upload, SendCharacterPreviewCodec.Build(new CharacterPreview(uploaded.CharacterId, uploaded.Buffer)));
		}

		// #788: the archive member itself. The grammar was derived over all FOUR corpus payloads (408 / 527 / 606 /
		// 618 bytes, three characters); the two that ride committed fixtures are the 408-byte upload and the 527-byte
		// served one, and both re-encode byte-exact - which is what a count-prefixed grammar buys. Nothing is skipped:
		// Parse ends at the last byte or throws.
		[Fact]
		public void PreviewRecord_RoundTripsTheCapturedPayloads()
		{
			var upload = UpdateCharacterPreviewCodec.Parse(TestPaths.AssetBytes("playeragent-update-character-preview.bin")).Buffer.Files[0].Data;
			var served = SendCharacterPreviewCodec.Parse(TestPaths.AssetBytes("playeragent-send-character-preview.bin")).Buffer.Files[0].Data;

			Assert.Equal(upload, CharacterPreviewSetCodec.Build(CharacterPreviewSetCodec.Parse(upload)));
			Assert.Equal(served, CharacterPreviewSetCodec.Build(CharacterPreviewSetCodec.Parse(served)));

			// The upload: a body (RDB 1010201), a head hung off attractor 33 and a weapon off attractor 11 carrying
			// its item template, four worn mesh-index files at CharacterEquipmentSlot positions, five texscripts.
			var preview = CharacterPreviewSetCodec.Parse(upload);
			Assert.Equal(1091336u, preview.MonsterDataId);
			Assert.Equal([(1010201u, 1139362u, 33u, 0u), (1010030u, 215943u, 11u, 3735601u)],
				preview.Resources.Select(r => (r.ResourceType, r.ResourceId, r.AttractorType, r.ItemTemplateId)));
			Assert.All(preview.Resources, r => Assert.Equal(Matrix4x4.Identity, r.Transform));
			Assert.Equal([(uint)CharacterEquipmentSlot.Chest, (uint)CharacterEquipmentSlot.Legs, (uint)CharacterEquipmentSlot.Wrist, 0u], preview.Meshes.Select(m => m.Slot));
			Assert.Equal(["*texscript_1108153", "*texscript_198086", "*texscript_202378", "*texscript_214018", "*texscript_3770785"], preview.Morphs.Select(m => m.Name));
			Assert.All(preview.Morphs, m => Assert.Equal(1f, m.Weight));

			// The served one is the same grammar with the parts that vary actually varying: a different body, a
			// non-identity attachment transform (a -0.06 Z offset, which is what makes the matrix a real field), a
			// vanity slot alongside the worn set, and body sliders at small signed weights next to the texscripts.
			var other = CharacterPreviewSetCodec.Parse(served);
			Assert.Equal(1091339u, other.MonsterDataId);
			Assert.Equal(-0.06f, other.Resources[1].Transform.M43, 4);
			Assert.Contains((uint)CharacterEquipmentSlot.VanityBelt, other.Meshes.Select(m => m.Slot));
			Assert.Equal([1f, 1f, 1f, -0.032f, -0.014f, -0.032f, 1f], other.Morphs.Select(m => MathF.Round(m.Weight, 3)));
			Assert.Equal("*beardmesh_3181884", other.Morphs[^1].Name);
		}

		// #788: the other direction - a preview AUTHORED here, packed into a fresh IOz1 archive and served under the
		// s2c method. The bytes are deliberately NOT retail's (a deflate stream is not a function of its contents), so
		// what is asserted is that the record survives the round trip intact.
		[Fact]
		public void ConstructedPreview_SurvivesTheArchiveRoundTrip()
		{
			var source = CharacterPreviewSetCodec.Parse(SendCharacterPreviewCodec.Parse(TestPaths.AssetBytes("playeragent-send-character-preview.bin")).Buffer.Files[0].Data);

			var served = SendCharacterPreviewCodec.Parse(SendCharacterPreviewCodec.Build(0x110099BD, source));

			Assert.Equal(0x110099BDu, served.CharacterId);
			Assert.Equal(["preview.bin"], served.Buffer.Files.Select(f => f.Name));
			Assert.Equal(CharacterPreviewSetCodec.Build(source), served.Buffer.Files[0].Data);
		}

		// Retail's empty forms, which are what a character with nothing stored gets: a 12-byte SendCharacterSettings
		// (`0000C350 <id> 00000000`) and a one-entry checksum map with an EMPTY md5 (`000007E2 <id> 0000`). Both are
		// verbatim from the corpus - Barbarian-login frames 51 and 24 - and both are the case our server hits today.
		[Fact]
		public void EmptySettingsAndCheckSumMap_MatchRetailBytes()
		{
			var settings = SendCharacterSettingsCodec.Build(new CharacterSettings(0x021664F9, ClientSettingsBuffer.Empty));
			Assert.Equal(Convert.FromHexString("0000c350021664f900000000"), settings);
			Assert.True(SendCharacterSettingsCodec.Parse(settings).Buffer.IsEmpty);

			var map = SendCharacterSettingsCheckSumMapCodec.Build(new CharacterSettingsCheckSumMap([new CharacterSettingsCheckSum(0x021664F8, "")]));
			Assert.Equal(Convert.FromHexString("000007e2021664f80000"), map);

			// And the populated form retail sends for an account whose characters DO have settings (NecroL80_..._login
			// frame 8): five entries, the first empty, framed by the ordinary 0x3F1 list word (0x17A6 = 6 * 0x3F1).
			var populated = Convert.FromHexString(
				"000017a601317cf8000008006f3100203866353861386133636436366562356335653230633135643465633666393738" +
				"0802e5d400203037383365353537303039623436336265326337376632636639613838393664110099bd002064363663" +
				"396266373937623134653764323434366262383161366561333731361101b4f500206139353336653836306537396137" +
				"333738663065363131623032653336643961");
			var map5 = SendCharacterSettingsCheckSumMapCodec.Parse(populated);
			Assert.Equal(5, map5.Entries.Count);
			Assert.Equal("", map5.Entries[0].CheckSum);
			Assert.Equal("8f58a8a3cd66eb5c5e20c15d4ec6f978", map5.Entries[1].CheckSum);
			Assert.Equal(populated, SendCharacterSettingsCheckSumMapCodec.Build(map5));
		}
	}
}
