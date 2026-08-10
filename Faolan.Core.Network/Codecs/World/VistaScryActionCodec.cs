using System;
using System.Globalization;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// The achievement VISTA's camera actions (#780) - the two GameActionData tails carried by a GameActionListIIR
	// addressed to the player who used the vista object:
	//
	//     0xD00C  e_Type_GC_GameAction_StartClientSideScryScriptWithParams   keyList 39;209 = ScriptID;ScriptParams
	//     0xCFEF  e_Type_GC_GameAction_StopScryScript                        keyList 39     = ScriptID
	//
	// ⚠️ **The two tails are DIFFERENT LENGTHS and the key list is why.** StopScryScript declares only `ScriptID`, so
	// its tail ends at that word - there is no empty string behind it. Reading the stop as "the start with a zero
	// length prefix" consumes two bytes of the ReadSubClass trailer and re-builds a body two bytes too long; the
	// captured pair is 21 and 79 tail bytes, and 16 + 5 is exactly the stop.
	//
	// That the script is CLIENT-side is what shapes the whole flow: the client runs the camera itself and reports its
	// own `CinematicTransition` bracket, so the server's part is to start it, to stop it when the client says it is
	// out, and to award the tag. Nothing here times the scene.
	//
	//     PathName=Vista_Path_6;PathDuration=36;Object=51320:55664
	//
	// `Object` is the used object's LIVE handle in DECIMAL - `<entity class>:<instance>`, 0xC878:0xD970 - so it is
	// composed per send from the object the player actually clicked and is never stored. The camera path itself is
	// playfield-scoped content (RDB type 1000047 keyed by playfield id), which `VistaCatalog` holds.
	//
	// A class-(c) sub-record codec (#530 rule 5c), like `TravelActionCodec` next door: it decodes the ActionTail
	// `GameActionListCodec` hands back, is never sent alone, and so declares no opcode. The head it writes is
	// `GameActionData`'s, continuing that element's grammar from the 0x2d6 word - the list header has already written
	// typeId/field/version/subCount/0x2d5.
	//
	// ⚠️ Both captured records carry `field` 0 and seed 0x2f5 = 9, i.e. the TEMPLATE-LOADED pair
	// (`GameActionDataCodec.CapturedSeed0x2f5`), NOT a `GameActionInstanceId`. Retail's own vista is a piece of world
	// CONTENT rather than a constructed action, which is the opposite of the travel trip next door - so a vista built
	// with a live instance counter would not be what retail sends.
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_2.tshark.json f4059 / f4295]
	public static class VistaScryActionCodec
	{
		// e_Type_GC_GameAction_StartClientSideScryScriptWithParams - starts the fly-through.
		public const uint StartTypeId = 0xD00C;

		// e_Type_GC_GameAction_StopScryScript - closes it. Carries the script id and nothing else.
		public const uint StopTypeId = 0xCFEF;

		public const string PathNameKey = "PathName";
		public const string PathDurationKey = "PathDuration";
		public const string ObjectKey = "Object";

		private const uint Fixed0x2d6 = 0;
		private const uint Fixed0x2f2 = 1;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(VistaScryAction value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static VistaScryAction Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(VistaScryActionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static VistaScryAction Read(ref SpanReader r)
		{
			r.ExpectU32Be(Fixed0x2d6, "fixed int 0x2d6");
			r.ExpectU32Be(Fixed0x2f2, "fixed int 0x2f2");
			r.ExpectU32Be(GameActionDataCodec.CapturedSeed0x2f5, "fixed int 0x2f5");
			var scriptId = r.ReadU32Be();

			// Which of the two actions this is comes from the list header's TYPE word, which the tail does not carry -
			// so the tail is told apart by whether anything precedes the trailer. That is not a heuristic: the stop's
			// key list ends at `ScriptID`, so its tail is exactly the trailer, and the start's shortest possible tail
			// is two bytes longer (an empty `ScriptParams` still writes its u16 length).
			var action = r.Remaining == GameActionListCodec.ReadSubClassTrailer.Length
				? VistaScryActions.Stop(scriptId)
				: Parse(r.ReadStringU16Length(), scriptId);

			// Read rather than skipped: a body that does not end here is a body we have mis-split - which is exactly
			// how the stop's missing `ScriptParams` would have gone unnoticed.
			foreach (var expected in GameActionListCodec.ReadSubClassTrailer)
				r.ExpectU8(expected, "ReadSubClass trailer");

			return action;
		}

		internal static void Write(FaolanStream s, VistaScryAction v)
		{
			s.WriteUInt32(Fixed0x2d6)
				.WriteUInt32(Fixed0x2f2)
				.WriteUInt32(GameActionDataCodec.CapturedSeed0x2f5)
				.WriteUInt32(v.ScriptId);

			if (!v.IsStop)
				s.WriteString(Compose(v));

			s.WriteArray(GameActionListCodec.ReadSubClassTrailer);
		}

		// The `key=value;...` params on their own. Strict like every Parse: an unknown key or a missing one means we
		// have mis-read the action, not that a default should be assumed.
		internal static VistaScryAction Parse(string spec, uint scriptId)
		{
			ArgumentNullException.ThrowIfNull(spec);

			string? pathName = null;
			uint? duration = null, objectClass = null, objectInstance = null;

			foreach (var pair in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var split = pair.IndexOf('=');
				if (split <= 0)
					throw CodecParseException.Unexpected(nameof(VistaScryActionCodec), 0, "a key=value pair", pair);

				var key = pair[..split];
				var raw = pair[(split + 1)..];
				switch (key)
				{
					case PathNameKey:
					{
						pathName = raw;
						break;
					}

					case PathDurationKey:
					{
						duration = ParseUInt(key, raw);
						break;
					}

					case ObjectKey:
					{
						// `<class>:<instance>`, both DECIMAL - the used object's live handle.
						var colon = raw.IndexOf(':');
						if (colon <= 0)
							throw CodecParseException.Unexpected(nameof(VistaScryActionCodec), 0, "a <class>:<instance> object reference", raw);

						objectClass = ParseUInt(key, raw[..colon]);
						objectInstance = ParseUInt(key, raw[(colon + 1)..]);
						break;
					}

					default:
					{
						throw CodecParseException.Unexpected(nameof(VistaScryActionCodec), 0, "a known vista key", key);
					}
				}
			}

			if (pathName == null || duration == null || objectClass == null || objectInstance == null)
				throw CodecParseException.Unexpected(nameof(VistaScryActionCodec), 0, $"{PathNameKey}/{PathDurationKey}/{ObjectKey}", spec);

			return new VistaScryAction(scriptId, pathName, duration.Value, objectClass.Value, objectInstance.Value);
		}

		// Re-composes the params in retail's own key order, which is what makes the round-trip byte-exact.
		internal static string Compose(VistaScryAction v)
		{
			ArgumentNullException.ThrowIfNull(v);
			return string.Create(CultureInfo.InvariantCulture,
				$"{PathNameKey}={v.PathName};{PathDurationKey}={v.PathDurationSeconds};{ObjectKey}={v.ObjectClass}:{v.ObjectInstance}");
		}

		private static uint ParseUInt(string key, string raw)
		{
			return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
				? value
				: throw CodecParseException.Unexpected(nameof(VistaScryActionCodec), 0, $"an integer for {key}", raw);
		}
	}

	// One vista camera action. A STOP carries only the script id - the client's descriptor for 0xCFEF declares no
	// `ScriptParams` - so `PathName` is null there and the four param fields are absent rather than empty.
	public sealed record VistaScryAction(uint ScriptId, string? PathName, uint PathDurationSeconds, uint ObjectClass, uint ObjectInstance)
	{
		public bool IsStop => PathName == null;

		// Which GameAction this record IS - the word `GameActionListCodec` puts in its type slot.
		public uint TypeId => IsStop
			? VistaScryActionCodec.StopTypeId
			: VistaScryActionCodec.StartTypeId;
	}

	// The two semantic forms, as VALUE factories (#530 rule 6): they take different parameter lists, but naming them
	// is what keeps "start the fly-through" from being told apart from "stop it" by an argument count.
	public static class VistaScryActions
	{
		// START the client-side camera path over `objectClass`:`objectInstance` - the LIVE handle of the object the
		// player used, which is what the client attaches the camera to.
		public static VistaScryAction Start(uint scriptId, string pathName, uint pathDurationSeconds, uint objectClass, uint objectInstance)
		{
			return new VistaScryAction(scriptId, pathName, pathDurationSeconds, objectClass, objectInstance);
		}

		// STOP it. The script id is the only thing on the wire.
		public static VistaScryAction Stop(uint scriptId)
		{
			return new VistaScryAction(scriptId, null, 0, 0, 0);
		}
	}
}
