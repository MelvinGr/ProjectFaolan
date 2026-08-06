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
	//     [u32 0x2d6 = 0][u32 0x2f2 = 1][u32 0x2f5 = 0][u32 ScriptID][u16 len][latin-1 ScriptParams][u8 0][u32 0]
	//                                                                         ^ GameActionListCodec.ReadSubClassTrailer
	//
	// The two values are the type's own descriptor key list, in the client's order: 53261 declares `39;209` =
	// `ScriptID;ScriptParams` (docs/re/gameaction-keylists.tsv). So the leading word is the retail SCRIPT the server
	// ran, and the string is that script's parameters - which is what makes 0xD00D a general "the server ran a script"
	// action whose FAMILY is named by the params, never by the action id.
	//
	// The element's `field` is NOT part of this action: it is `GameActionData::s_nInstance`, the sending process's own
	// unique-instance counter (`GameActionInstanceId`, #636). It used to read as per-trip data we could not derive,
	// which is what kept this codec decode-only.
	public static class TravelActionCodec
	{
		// The GameAction type the client dispatches this payload to. Inside the client's asserted GameAction range
		// (0xCF09..0xD026), so it is a real type rather than a mis-split field.
		public const uint TypeId = 0xD00D;

		// The `ScriptID` retail runs for a wagoner trip - identical on all four captured trips (Cordelia, Treva,
		// Hakotep's Conall's Valley ride and the Poitain instance entry), across two characters and three servers.
		// A fifth 0xD00D in the corpus carries a different script (0x004984B0) and a `PF=11040` param string, which is
		// some other server-side script entirely and is deliberately NOT modelled here.
		public const uint TripScriptId = 0x0037A487;

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
			var scriptId = r.ReadU32Be();
			var spec = r.ReadStringU16Length();

			// The trailer GameActionListIIR_t::ReadSubClass consumes after the element list. Read rather than skipped:
			// a body that does not end here is a body we have mis-split.
			foreach (var expected in GameActionListCodec.ReadSubClassTrailer)
				r.ExpectU8(expected, "ReadSubClass trailer");

			return Parse(spec, scriptId);
		}

		internal static void Write(FaolanStream s, TravelAction v)
		{
			s.WriteUInt32(Fixed0x2d6)
				.WriteUInt32(Fixed0x2f2)
				.WriteUInt32(Fixed0x2f5)
				.WriteUInt32(v.ScriptId)
				.WriteString(Compose(v))
				.WriteArray(GameActionListCodec.ReadSubClassTrailer);
		}

		// The `key=value;...` spec on its own. Strict like every Parse: an unknown key or a missing coordinate means we
		// have mis-read the action, not that a default should be assumed.
		internal static TravelAction Parse(string spec, uint scriptId)
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

			return new TravelAction(map.Value, x.Value, y.Value, z.Value, flagKey, flagValue, scriptId);
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
	// `Local=1` on a wagoner trip, `HeroicEnabled=0` on a cross-playfield one - and `ScriptId` is the action's
	// `ScriptID` descriptor value, carried so a re-build is byte-exact rather than the constant being asserted off
	// four samples.
	public sealed record TravelAction(uint DestinationMapId, float X, float Y, float Z, string FlagKey, uint FlagValue, uint ScriptId)
	{
		// A same-playfield relocate: the wagoner trips. A cross-playfield trip carries the difficulty flag instead and
		// is NOT local, which is exactly the distinction a single bool would have lost.
		public bool IsLocal => FlagKey == TravelActionCodec.LocalFlag && FlagValue != 0;
	}

	// The two semantic forms of a trip action, as VALUE factories (#530 rule 6): both take the same
	// (map, x, y, z) parameter list, so choosing between them by `Build` overload is exactly the ambiguity that rule
	// removes. Which one a trip is comes from the DESTINATION PLAYFIELD, and the corpus says so on all four captured
	// trips: the three whose destination is the playfield the player was already on carry `Local=1` (Cordelia -> 1020,
	// Treva -> 1090, Hakotep -> 1010), and the one that crosses (Wagoner Mycus -> Poitain 2070) carries
	// `HeroicEnabled=0` instead. That is the same split `TravelService` already makes to choose between relocating in
	// place and running the changemap handoff.
	public static class TravelActions
	{
		// A SAME-playfield relocate - no loading screen. `Local=1` on every captured wagoner trip.
		public static TravelAction Local(uint mapId, float x, float y, float z)
		{
			return new TravelAction(mapId, x, y, z, TravelActionCodec.LocalFlag, 1, TravelActionCodec.TripScriptId);
		}

		// A CROSS-playfield trip. The flag carries the instance difficulty mode (#511) rather than a locality: retail
		// sent `HeroicEnabled=0` for the normal-mode entry, which is the only value the corpus has, so anything else
		// would be invented.
		public static TravelAction CrossPlayfield(uint mapId, float x, float y, float z, bool heroic = false)
		{
			return new TravelAction(mapId, x, y, z, TravelActionCodec.HeroicEnabledFlag, heroic
				? 1u
				: 0u, TravelActionCodec.TripScriptId);
		}
	}
}
