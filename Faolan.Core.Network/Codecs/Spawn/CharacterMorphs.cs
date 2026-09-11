using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     The editor-slider to spawn-morph mapping, decoded against the Hoxxling all-sliders create capture and confirmed
	///     on two others.
	/// </summary>
	/// <remarks>
	///     <list type="bullet">
	///         <item>
	///             The editor's SetMorphValue numeric value is a zigzag-encoded signed slider value, so "arse" 31 means -16
	///             and "arm" 200 means +100. CharacterBody stores the decoded signed value.
	///         </item>
	///         <item>
	///             The spawn scaffold's morph entry float is signedValue / 1000; all 35 captured slider values reproduce
	///             bit-exact.
	///         </item>
	///         <item>
	///             Face sliders arrive slot-prefixed ("33_NoseCrookedRight") and the slot number is the wire slider id of the
	///             type-0x00 entry. Body sliders arrive bare ("arm") and map to type-0x01 entries with fixed ids, except
	///             "ears", which rides as type 0x00 slider id 0xFF.
	///         </item>
	///         <item>
	///             The wire entry order is the editor's send order: face sliders by ascending slot, then body sliders
	///             alphabetically. Every capture shows this, and the generated sequence must reproduce it.
	///         </item>
	///     </list>
	/// </remarks>
	public static class CharacterMorphs
	{
		/// <summary>In wire order: face by slot ascending, then body alphabetical.</summary>
		private static readonly Slider[] Sliders =
		[
			new Slider("BrowLow", 0x00, 2, b => b.EyebrowScale, (b, v) => b.EyebrowScale = v), new Slider("CheekForward", 0x00, 4, b => b.CheekDepth, (b, v) => b.CheekDepth = v),
			new Slider("CheekLow", 0x00, 6, b => b.CheekHeight, (b, v) => b.CheekHeight = v), new Slider("CheekWide", 0x00, 8, b => b.CheekWidth, (b, v) => b.CheekWidth = v),
			new Slider("ChinShort", 0x00, 9, b => b.ChinLength, (b, v) => b.ChinLength = v), new Slider("ChinWide", 0x00, 12, b => b.ChinWidth, (b, v) => b.ChinWidth = v),
			new Slider("EarsAngle", 0x00, 13, b => b.EarAngle, (b, v) => b.EarAngle = v), new Slider("EyeAngle1", 0x00, 14, b => b.EyesAngle, (b, v) => b.EyesAngle = v),
			new Slider("EyesHigh", 0x00, 16, b => b.EyesVerticalPos, (b, v) => b.EyesVerticalPos = v),
			new Slider("EyesNarrow", 0x00, 19, b => b.EyesHorizontalPos, (b, v) => b.EyesHorizontalPos = v), new Slider("EyesBack", 0x00, 20, b => b.EyesDepth, (b, v) => b.EyesDepth = v),
			new Slider("JawNarrow", 0x00, 22, b => b.JawWidth, (b, v) => b.JawWidth = v), new Slider("FaceLenght", 0x00, 24, b => b.FaceLength, (b, v) => b.FaceLength = v),
			new Slider("MouthLow", 0x00, 26, b => b.MouthVerticalPos, (b, v) => b.MouthVerticalPos = v), new Slider("MouthNarrow", 0x00, 28, b => b.MouthWidth, (b, v) => b.MouthWidth = v),
			new Slider("NoseAngleDown", 0x00, 30, b => b.NoseAngle, (b, v) => b.NoseAngle = v), new Slider("NoseAngle1", 0x00, 31, b => b.NoseCurve, (b, v) => b.NoseCurve = v),
			new Slider("NoseCrookedRight", 0x00, 33, b => b.CrookedNose, (b, v) => b.CrookedNose = v), new Slider("NoseLenght", 0x00, 35, b => b.NoseLength, (b, v) => b.NoseLength = v),
			new Slider("NoseWide", 0x00, 36, b => b.NoseWidth, (b, v) => b.NoseWidth = v), new Slider("arm", 0x01, 0, b => b.Arm, (b, v) => b.Arm = v),
			new Slider("arse", 0x01, 4, b => b.Arse, (b, v) => b.Arse = v), new Slider("bosom", 0x01, 2, b => b.Bosom, (b, v) => b.Bosom = v),
			new Slider("chest", 0x01, 1, b => b.Chest, (b, v) => b.Chest = v), new Slider("ears", 0x00, 0xFF, b => b.Ears, (b, v) => b.Ears = v),
			new Slider("leg", 0x01, 6, b => b.Leg, (b, v) => b.Leg = v), new Slider("stomach", 0x01, 3, b => b.Stomach, (b, v) => b.Stomach = v),
			new Slider("thigh", 0x01, 5, b => b.Thigh, (b, v) => b.Thigh = v)
		];

		/// <summary>
		///     Generates the spawn-scaffold morph entries for the body's non-default sliders, in the retail wire order. A
		///     null body - a character that never touched a slider - yields none, which is the captured shape of an unmorphed
		///     character. The per-character slot ordinal is stamped by BuildAppearanceEntries over the whole table, so it is
		///     left 0 here.
		/// </summary>
		public static List<AppearanceEntry> BuildEntries(CharacterBody? body)
		{
			var entries = new List<AppearanceEntry>();
			if (body == null)
				return entries;

			foreach (var slider in Sliders)
			{
				var value = slider.Get(body);
				if (value == 0)
					continue;

				entries.Add(new AppearanceEntry
				{
					MorphId = (uint)((slider.Type << 8) | slider.SliderId),
					Value = (float)(value / 1000.0)
				});
			}

			return entries;
		}

		/// <summary>
		///     Whether an appearance element's key is one of the morph sliders rather than a weighted appearance entry. The
		///     type byte alone will not do: type 0x01 is shared with the weighted rows, which use slider ids outside the
		///     slider table, so the whole (type, id) pair has to match.
		/// </summary>
		public static bool IsMorphKey(uint key)
		{
			if (key > ushort.MaxValue)
				return false;

			var type = (byte)(key >> 8);
			var sliderId = (byte)(key & 0xFF);
			return Sliders.Any(s => s.Type == type && s.SliderId == sliderId);
		}

		/// <summary>
		///     The inverse of <see cref="BuildEntries" />: reads captured scaffold entries back into the body's columns, and
		///     returns how many landed. Elements that are not morph sliders - the weighted appearance rows - match no slider
		///     and are skipped.
		/// </summary>
		/// <remarks>
		///     An imported character whose morphs exist only as scaffold rows must have them lifted into CharacterBody, which
		///     is the model everything else reads. The spawn is replayed from the scaffold, so the character looks right in
		///     game either way, but Faolan.Web renders from CharacterBody and RefreshAppearance regenerates the scaffold's
		///     morph entries from it - so without this the first appearance edit silently wipes the captured morphs.
		/// </remarks>
		public static int ApplyEntries(CharacterBody body, IEnumerable<AppearanceEntry> entries)
		{
			var applied = 0;
			foreach (var entry in entries)
			{
				if (entry.MorphId > ushort.MaxValue)
					continue;

				var type = (byte)(entry.MorphId >> 8);
				var sliderId = (byte)(entry.MorphId & 0xFF);

				foreach (var slider in Sliders)
				{
					if (slider.Type != type || slider.SliderId != sliderId)
						continue;

					// BuildEntries stores value / 1000; undo exactly that.
					slider.Set(body, entry.Value * 1000f);
					applied++;
					break;
				}
			}

			return applied;
		}

		/// <summary>
		///     Stores one editor SetMorphValue into the body: strips the face slot prefix ("33_NoseCrookedRight") and writes
		///     the signed slider value to the matching column. <paramref name="value" /> is the already-decoded field-3 int,
		///     the caller zigzag-decoding once at read. Returns false for names not in the table - resource references, or
		///     sliders not yet modelled - which the caller logs.
		/// </summary>
		public static bool TryApply(CharacterBody body, string morphName, int value)
		{
			var name = morphName.Length > 3 && morphName[0] is >= '0' and <= '9' && morphName[1] is >= '0' and <= '9' && morphName[2] == '_'
				? morphName[3..]
				: morphName;

			foreach (var slider in Sliders)
			{
				if (!string.Equals(slider.Name, name, StringComparison.Ordinal))
					continue;

				slider.Set(body, value);
				return true;
			}

			return false;
		}

		private sealed record Slider(string Name, byte Type, byte SliderId, Func<CharacterBody, float> Get, Action<CharacterBody, float> Set);
	}
}
