using System;
using System.Globalization;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// The LOCAL TRAVEL action (#514) - GameAction type 0xD00D, carried as the single GameActionData element of a
	// GameActionListIIR addressed to the travelling player. It is what retail sends the instant a wagoneer's
	// destination is picked, and it spells the whole trip out in one wire string:
	//
	//     xpos=1136;ypos=133;zpos=223;PlayFieldID=1020;Local=1
	//
	// which the player's re-stream then confirms to the centimetre (SimpleCharFullUpdate at (1136.0, 132.8, 223.0)).
	// `ypos` is the HEIGHT - the same axis order the rest of the wire uses - and `Local=1` marks a same-playfield
	// relocate, which is why both captured wagoner trips carry no redirect and no loading screen.
	//
	// ⚠️ The action is NOT travel-only, and the trailing flag is what says which use it is. All four captured actions
	// share the `xpos/ypos/zpos/PlayFieldID` head and then differ:
	//
	//     ...;PlayFieldID=1020;Local=1           the three wagoner trips (a same-playfield relocate)
	//     ...;PlayFieldID=2070;HeroicEnabled=0   an INSTANCE entry, carrying the difficulty mode instead (#511)
	//
	// So the flag is modelled as a (key, value) pair rather than as a bool called Local: collapsing the two would make
	// an instance entry read as a non-local trip, and re-building it would emit the wrong key. An unrecognised key is a
	// parse failure, not a default - a third use of this action must be looked at, not guessed at.
	//
	// A class-(c) sub-record codec (#530 rule 5c): it decodes the ActionTail GameActionListCodec hands back, is never
	// sent alone, and so declares no opcode. The tail's layout, continuing GameActionDataCodec's element grammar from
	// the 0x2d6 word (GameActionListCodec's own header has already written typeId/field/version/subCount/0x2d5):
	//
	//     [u32 0x2d6 = 0][u32 0x2f2 = 1][u32 0x2f5 = 0][u32 descriptor key value][u16 len][latin-1 string]
	//     [u8 0][u32 0]                                                       <- GameActionListCodec.ReadSubClassTrailer
	//
	// ⚠️ This codec DECODES; nothing sends it. Building the trip's own GameActionList would need the element's `Field`
	// (0x1A18 for Cordelia's trip, 0x08FA for Treva's - per-trip and not derivable from anything we hold), so the
	// server drives the relocate through the capture-verified revive teleport pair instead. Parsing it is what turns
	// two retail trips into imported data. Open on #514.
	public static class TravelActionCodec
	{
		// The GameAction type the client dispatches this payload to. Inside the client's asserted GameAction range
		// (0xCF09..0xD026), so it is a real type rather than a mis-split field.
		public const uint TypeId = 0xD00D;

		// The trailing flag keys observed across the corpus - see the class comment. Kept as a set rather than as a bool
		// so an unknown third one fails loudly.
		public const string LocalFlag = "Local";
		public const string HeroicEnabledFlag = "HeroicEnabled";

		private const uint Fixed0x2d6 = 0;
		private const uint Fixed0x2f2 = 1;
		private const uint Fixed0x2f5 = 0;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(TravelAction value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static TravelAction Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TravelActionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static TravelAction Read(ref SpanReader r)
		{
			r.ExpectU32Be(Fixed0x2d6, "fixed int 0x2d6");
			r.ExpectU32Be(Fixed0x2f2, "fixed int 0x2f2");
			r.ExpectU32Be(Fixed0x2f5, "fixed int 0x2f5");
			var descriptorKey = r.ReadU32Be();
			var spec = r.ReadStringU16Length();

			// The trailer GameActionListIIR_t::ReadSubClass consumes after the element list. Read rather than skipped:
			// a body that does not end here is a body we have mis-split.
			foreach (var expected in GameActionListCodec.ReadSubClassTrailer)
				r.ExpectU8(expected, "ReadSubClass trailer");

			return Parse(spec, descriptorKey);
		}

		internal static void Write(FaolanStream s, TravelAction v)
		{
			s.WriteUInt32(Fixed0x2d6)
				.WriteUInt32(Fixed0x2f2)
				.WriteUInt32(Fixed0x2f5)
				.WriteUInt32(v.DescriptorKey)
				.WriteString(Compose(v))
				.WriteArray(GameActionListCodec.ReadSubClassTrailer);
		}

		// The `key=value;...` spec on its own. Strict like every Parse: an unknown key or a missing coordinate means we
		// have mis-read the action, not that a default should be assumed.
		internal static TravelAction Parse(string spec, uint descriptorKey)
		{
			ArgumentNullException.ThrowIfNull(spec);

			float? x = null, y = null, z = null;
			uint? map = null;
			string? flagKey = null;
			uint flagValue = 0;

			foreach (var pair in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var split = pair.IndexOf('=');
				if (split <= 0)
					throw CodecParseException.Unexpected(nameof(TravelActionCodec), 0, "a key=value pair", pair);

				var key = pair[..split];
				var raw = pair[(split + 1)..];
				switch (key)
				{
					case "xpos":
					{
						x = ParseFloat(key, raw);
						break;
					}

					case "ypos":
					{
						y = ParseFloat(key, raw);
						break;
					}

					case "zpos":
					{
						z = ParseFloat(key, raw);
						break;
					}

					case "PlayFieldID":
					{
						map = ParseUInt(key, raw);
						break;
					}

					case LocalFlag:
					case HeroicEnabledFlag:
					{
						flagKey = key;
						flagValue = ParseUInt(key, raw);
						break;
					}

					default:
					{
						throw CodecParseException.Unexpected(nameof(TravelActionCodec), 0, "a known travel key", key);
					}
				}
			}

			if (x == null || y == null || z == null || map == null || flagKey == null)
				throw CodecParseException.Unexpected(nameof(TravelActionCodec), 0, $"xpos/ypos/zpos/PlayFieldID/{LocalFlag}|{HeroicEnabledFlag}", spec);

			return new TravelAction(map.Value, x.Value, y.Value, z.Value, flagKey, flagValue, descriptorKey);
		}

		// Re-composes the spec in retail's own key order, which is what makes the round-trip byte-exact.
		internal static string Compose(TravelAction v)
		{
			ArgumentNullException.ThrowIfNull(v);
			return string.Create(CultureInfo.InvariantCulture,
				$"xpos={Number(v.X)};ypos={Number(v.Y)};zpos={Number(v.Z)};PlayFieldID={v.DestinationMapId};{v.FlagKey}={v.FlagValue}");
		}

		// Retail writes whole coordinates with no decimal point; keep that form so a re-build matches byte for byte,
		// and only fall back to a decimal when the value genuinely has one.
		private static string Number(float value)
		{
			return value == MathF.Truncate(value)
				? ((long)value).ToString(CultureInfo.InvariantCulture)
				: value.ToString(CultureInfo.InvariantCulture);
		}

		private static float ParseFloat(string key, string raw)
		{
			return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
				? value
				: throw CodecParseException.Unexpected(nameof(TravelActionCodec), 0, $"a number for {key}", raw);
		}

		private static uint ParseUInt(string key, string raw)
		{
			return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
				? value
				: throw CodecParseException.Unexpected(nameof(TravelActionCodec), 0, $"an integer for {key}", raw);
		}
	}

	// One 0xD00D move-the-player action as the wire states it. `FlagKey`/`FlagValue` are the trailing pair verbatim -
	// `Local=1` on a wagoner trip, `HeroicEnabled=0` on an instance entry - and `DescriptorKey` is the action's leading
	// u32 key value (0x0037A487 in every captured action), carried so a re-build is byte-exact rather than being
	// asserted as a constant off four samples.
	public sealed record TravelAction(uint DestinationMapId, float X, float Y, float Z, string FlagKey, uint FlagValue, uint DescriptorKey)
	{
		// A same-playfield relocate: the wagoner trips. An instance entry carries the difficulty flag instead and is
		// NOT local, which is exactly the distinction a single bool would have lost.
		public bool IsLocal => FlagKey == TravelActionCodec.LocalFlag && FlagValue != 0;
	}
}
