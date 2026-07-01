using System;
using System.Collections.Generic;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Spawn
{
	// The editor-slider <-> spawn-morph mapping (cracked 2026-06-10 against the Hoxxling all-sliders create
	// capture and confirmed on Dikketest/Strickenen):
	//
	//  - The editor's SetMorphValue numeric value is a ZIGZAG-encoded signed slider value (so "arse"=31 means
	//    -16, "arm"=200 means +100). CharacterBody stores the DECODED signed value.
	//  - The spawn scaffold's morph entry float is simply signedValue / 1000 (all 35 captured slider values
	//    reproduce bit-exact).
	//  - Face sliders arrive slot-prefixed ("33_NoseCrookedRight"); the slot number IS the wire sliderId of
	//    the type-0x00 entry. Body sliders arrive bare ("arm") and map to type-0x01 entries with fixed ids -
	//    except "ears", which rides as type 0x00 sliderId 0xFF.
	//  - The wire entry order is the editor's send order: face sliders by ascending slot, then body sliders
	//    alphabetically (this is what every capture shows, and Sequence must reproduce it).
	public static class CharacterMorphs
	{
		// In wire order: face by slot ascending, then body alphabetical.
		private static readonly Slider[] Sliders =
		[
			new Slider("BrowLow", 0x00, 2, b => b.EyebrowScale, (b, v) => b.EyebrowScale = v), new Slider("CheekForward", 0x00, 4, b => b.CheekDepth, (b, v) => b.CheekDepth = v),
			new Slider("CheekLow", 0x00, 6, b => b.CheekHeight, (b, v) => b.CheekHeight = v), new Slider("CheekWide", 0x00, 8, b => b.CheekWidth, (b, v) => b.CheekWidth = v),
			new Slider("ChinShort", 0x00, 9, b => b.ChinLength, (b, v) => b.ChinLength = v), new Slider("ChinWide", 0x00, 12, b => b.ChinWidth, (b, v) => b.ChinWidth = v),
			new Slider("EarsAngle", 0x00, 13, b => b.EarAngle, (b, v) => b.EarAngle = v), new Slider("EyeAngle1", 0x00, 14, b => b.EyesAngle, (b, v) => b.EyesAngle = v),
			new Slider("EyesHigh", 0x00, 16, b => b.EyesVerticalPos, (b, v) => b.EyesVerticalPos = v), new Slider("EyesNarrow", 0x00, 19, b => b.EyesHorizontalPos, (b, v) => b.EyesHorizontalPos = v),
			new Slider("EyesBack", 0x00, 20, b => b.EyesDepth, (b, v) => b.EyesDepth = v), new Slider("JawNarrow", 0x00, 22, b => b.JawWidth, (b, v) => b.JawWidth = v),
			new Slider("FaceLenght", 0x00, 24, b => b.FaceLength, (b, v) => b.FaceLength = v), new Slider("MouthLow", 0x00, 26, b => b.MouthVerticalPos, (b, v) => b.MouthVerticalPos = v),
			new Slider("MouthNarrow", 0x00, 28, b => b.MouthWidth, (b, v) => b.MouthWidth = v), new Slider("NoseAngleDown", 0x00, 30, b => b.NoseAngle, (b, v) => b.NoseAngle = v),
			new Slider("NoseAngle1", 0x00, 31, b => b.NoseCurve, (b, v) => b.NoseCurve = v), new Slider("NoseCrookedRight", 0x00, 33, b => b.CrookedNose, (b, v) => b.CrookedNose = v),
			new Slider("NoseLenght", 0x00, 35, b => b.NoseLength, (b, v) => b.NoseLength = v), new Slider("NoseWide", 0x00, 36, b => b.NoseWidth, (b, v) => b.NoseWidth = v),
			new Slider("arm", 0x01, 0, b => b.Arm, (b, v) => b.Arm = v), new Slider("arse", 0x01, 4, b => b.Arse, (b, v) => b.Arse = v), new Slider("bosom", 0x01, 2, b => b.Bosom, (b, v) => b.Bosom = v),
			new Slider("chest", 0x01, 1, b => b.Chest, (b, v) => b.Chest = v), new Slider("ears", 0x00, 0xFF, b => b.Ears, (b, v) => b.Ears = v), new Slider("leg", 0x01, 6, b => b.Leg, (b, v) => b.Leg = v),
			new Slider("stomach", 0x01, 3, b => b.Stomach, (b, v) => b.Stomach = v), new Slider("thigh", 0x01, 5, b => b.Thigh, (b, v) => b.Thigh = v)
		];

		// Generates the spawn-scaffold morph entries for the body's non-default sliders, indexed from
		// startIndex, in the retail wire order. A null body (character never touched a slider) yields none -
		// exactly the captured shape of an unmorphed character.
		public static List<ScaffoldEntry> BuildEntries(CharacterBody body, uint startIndex)
		{
			var entries = new List<ScaffoldEntry>();
			if (body == null)
				return entries;

			foreach (var slider in Sliders)
			{
				var value = slider.Get(body);
				if (value == 0)
					continue;

				var bits = BitConverter.SingleToUInt32Bits((float)(value / 1000.0));
				entries.Add(new ScaffoldEntry(startIndex + (uint)entries.Count, (ushort)((slider.Type << 8) | slider.SliderId), bits));
			}

			return entries;
		}

		// Stores one editor SetMorphValue into the body: strips the face slot prefix ("33_NoseCrookedRight"),
		// zigzag-decodes the raw value and writes the signed slider value to the matching column. Returns false
		// for names not in the table (resource refs and unseen sliders - the caller logs those).
		public static bool TryApply(CharacterBody body, string morphName, uint rawValue)
		{
			var name = morphName.Length > 3 && morphName[0] is >= '0' and <= '9' && morphName[1] is >= '0' and <= '9' && morphName[2] == '_'
				? morphName[3..]
				: morphName;

			foreach (var slider in Sliders)
			{
				if (!string.Equals(slider.Name, name, StringComparison.Ordinal))
					continue;

				slider.Set(body, ZigzagDecode(rawValue));
				return true;
			}

			return false;
		}

		private static int ZigzagDecode(uint v)
		{
			return (int)(v >> 1) ^ -(int)(v & 1);
		}

		private sealed record Slider(string Name, byte Type, byte SliderId, Func<CharacterBody, float> Get, Action<CharacterBody, float> Set);
	}
}
