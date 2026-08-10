using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Progression;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The achievement VISTA (#740 decoded it, #780 wired it): every record of the one captured scene, byte-exact,
	// built from what `VistaCatalog` holds. The whole sequence is one 50-second window of
	// `NecroL80_mage-necromancer_login_gameplay_annotated_2` (frames 4021-4299), on the Old Tarantia Docks viewpoint -
	// object 0xC878:0xD970, which is `WorldObjects` 1546994545, the map-2000 row at 676.58 / 92.69 / 655.83 whose
	// display id is the 4998751 keyed below.
	//
	// The player's handle in that capture is 0x0802E5D4.
	public class AchievementVistaTests
	{
		private const uint Player = 0x0802E5D4;
		private const uint ObjectClass = 0x0000C878;
		private const uint ObjectHandle = 0x0000D970;

		// The one row the corpus states. Everything else in the family is unmeasured - see the catalog.
		private static AchievementVista Docks => VistaCatalog.Vistas[4998751];

		// f4029 17:41:04.184 - the lid opens.
		[Fact]
		public void TheLidRecords_MatchTheCapture()
		{
			Assert.Equal(CapturedRecord.Hex("00000020e7f4f8f80000c8780000d9700000000000000d6f626a6563745f6f70656e6564"),
				Hex(SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetBaseAnim(ObjectClass, ObjectHandle, "object_opened"))));

			// f4069 17:41:10.885 - and shuts behind the camera.
			Assert.Equal(CapturedRecord.Hex("00000020e7f4f8f80000c8780000d9700000000000000d6f626a6563745f636c6f736564"),
				Hex(SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetBaseAnim(ObjectClass, ObjectHandle, "object_closed"))));
		}

		// f4029 - the player's five-second gesture at the viewpoint, carrying the feedback remoteformat that NAMES the
		// mechanism. The key is the object's localized action key, which we do not import - the capture's is passed in
		// here so the golden is the whole record rather than the record minus its longest field (the same treatment
		// BehaviorAnimationsTests gives the bench sit).
		[Fact]
		public void TheObjectActionRecord_MatchesTheCapture()
		{
			const string capturedKey = "MZ5&#39;P3&amp;)jBD8&quot;]Ylzx2A";
			var remoteformat = BehaviorAnimations.VistaRemoteformat(Docks.ObjectActionIndex, capturedKey);

			Assert.Equal(
				"<remoteformat token=\"startobjectaction(7)|interaction(2)|GenericAchievementVista\" category=\"50026\" " +
				$"key=\"{capturedKey}\" knubot=\"0\"  ><parameter value=\"7;\" name=\"String\" /></remoteformat>", remoteformat);

			Assert.Equal(CapturedRecord.Hex(
					"00000123642cd3d60000c3500802e5d400040000002a0000001408c4c802180528083000520808f8900310f0b203000000020014736f6369616c5f69" +
					"74656d5f6163636570745f320000000040a000000000000000cd3c72656d6f7465666f726d617420746f6b656e3d2273746172746f626a6563746163" +
					"74696f6e2837297c696e746572616374696f6e2832297c47656e65726963416368696576656d656e745669737461222063617465676f72793d223530" +
					"30323622206b65793d224d5a35262333393b503326616d703b296a4244382671756f743b5d596c7a78324122206b6e75626f743d22302220203e3c70" +
					"6172616d657465722076616c75653d22373b22206e616d653d22537472696e6722202f3e3c2f72656d6f7465666f726d61743e00000001"),
				Hex(BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerViewVista(Player, ObjectHandle, remoteformat))));
		}

		// f4057 17:41:09.221 - the casterless scene buff, and f4299 its END twin. `ApplyNowNoCaster` sources the expiry
		// stamp from the live server clock, so the stamp is the one field this compares around rather than to.
		[Fact]
		public void TheSceneBuff_MatchesTheCapturedShape()
		{
			var data = MagicBuffDatas.ApplyNoCaster(Docks.BuffSpellBaseId, Docks.BuffDurationCentiseconds, 0x15723466, 80);
			var apply = MagicBuffCodec.Build(new MagicBuffIIR(Player, 0x00, Docks.BuffSpellBaseId, MagicBuffIds.FormApply, null, MagicBuffDataCodec.Build(data)));

			Assert.Equal(CapturedRecord.Hex(
					"000000506b87e3160000c3500802e5d400004c45fa000000000000000000000000000000000000000fa000000000000100000000004c45fa000000000000000000000000000000000000" +
					"00500000000015723466"),
				Hex(apply));

			Assert.Equal(CapturedRecord.Hex("0000001a6b87e3160000c3500802e5d400004c45fa010000000000000000"),
				Hex(MagicBuffCodec.Build(new MagicBuffIIR(Player, 0x00, Docks.BuffSpellBaseId, MagicBuffIds.FormEnd, null, []))));
		}

		// f4059 - the fly-through itself. The `Object` param is the used object's LIVE handle in decimal, which is why
		// it is composed per send and never stored: 51320:55664 IS 0xC878:0xD970.
		[Fact]
		public void TheCameraStartAction_MatchesTheCapture()
		{
			var start = VistaScryActions.Start(Docks.ScryScriptId, Docks.CameraPath, Docks.PathDurationSeconds, ObjectClass, ObjectHandle);

			Assert.Equal("PathName=Vista_Path_6;PathDuration=36;Object=51320:55664", VistaScryActionCodec.Compose(start));
			Assert.Equal(VistaScryActionCodec.StartTypeId, start.TypeId);

			Assert.Equal(CapturedRecord.Hex(
					"00000074f508f4c10000c3500802e5d400000007e20000d00c00000000000000040000000000000001000000000000000100000009004c461a0038506174684e616d653d5669737461" +
					"5f506174685f363b506174684475726174696f6e3d33363b4f626a6563743d35313332303a35353636340000000000"),
				Hex(GameActionListCodec.Build(new GameActionListIIR(Player, start.TypeId, 0, VistaScryActionCodec.Build(start)))));
		}

		// f4295 - and the stop. ⚠️ Its tail is TWO BYTES SHORTER than a start with an empty params string, because
		// 53231's descriptor key list is `ScriptID` alone: there is no `ScriptParams` behind it.
		[Fact]
		public void TheCameraStopAction_CarriesNoParams()
		{
			var stop = VistaScryActions.Stop(Docks.ScryScriptId);

			Assert.True(stop.IsStop);
			Assert.Equal(VistaScryActionCodec.StopTypeId, stop.TypeId);

			Assert.Equal(CapturedRecord.Hex("0000003af508f4c10000c3500802e5d400000007e20000cfef00000000000000040000000000000001000000000000000100000009004c461a0000000000"),
				Hex(GameActionListCodec.Build(new GameActionListIIR(Player, stop.TypeId, 0, VistaScryActionCodec.Build(stop)))));
		}

		// Both tails round-trip through the one Parse, which tells them apart by whether anything precedes the
		// ReadSubClass trailer - the length difference the two key lists produce.
		[Fact]
		public void BothTails_RoundTrip()
		{
			var start = VistaScryActions.Start(Docks.ScryScriptId, Docks.CameraPath, Docks.PathDurationSeconds, ObjectClass, ObjectHandle);
			var stop = VistaScryActions.Stop(Docks.ScryScriptId);

			Assert.Equal(start, VistaScryActionCodec.Parse(VistaScryActionCodec.Build(start)));
			Assert.Equal(stop, VistaScryActionCodec.Parse(VistaScryActionCodec.Build(stop)));
		}

		// Strict (#530 rule 4): a params string we cannot account for is a mis-read action, not a defaulted one.
		[Fact]
		public void UnknownOrMissingParams_Throw()
		{
			Assert.Throws<CodecParseException>(() => VistaScryActionCodec.Parse("PathName=X;PathDuration=1;Object=1:2;Extra=3", 1));
			Assert.Throws<CodecParseException>(() => VistaScryActionCodec.Parse("PathName=X;PathDuration=1", 1));
			Assert.Throws<CodecParseException>(() => VistaScryActionCodec.Parse("PathName=X;PathDuration=1;Object=1", 1));
		}

		// f4289 - the scry step retail sends once the client reports it has left the cinematic.
		[Fact]
		public void TheEndPathStep_MatchesTheCapture()
		{
			Assert.Equal(CapturedRecord.Hex("00000028a36d3b740000c3500802e5d400000000170805100218002207456e64506174682a00320408001000"),
				Convert.ToHexStringLower(ScryScriptCodec.Build(Player, "EndPath")));
		}

		// The tag the scene awards, and the catalog row behind it.
		[Fact]
		public void TheCatalogRow_IsWhatTheCaptureStates()
		{
			Assert.Equal(716u, Docks.AchievementTagId);
			Assert.Equal("Vista_Path_6", Docks.CameraPath);
			Assert.Equal(36u, Docks.PathDurationSeconds);
			Assert.Equal(0x004C461Au, Docks.ScryScriptId);
			Assert.Equal(0x004C45FAu, Docks.BuffSpellBaseId);

			// The lookup demands the CATEGORY as well: an LDB id is only unique inside one.
			Assert.Same(Docks, VistaCatalog.VistaFor(VistaCatalog.DisplayCategory, Docks.DisplayId));
			Assert.Null(VistaCatalog.VistaFor(50200, Docks.DisplayId));
			Assert.Null(VistaCatalog.VistaFor(VistaCatalog.DisplayCategory, 4998750));
		}

		private static string Hex(byte[] record)
		{
			return Convert.ToHexStringLower(record);
		}
	}
}
