using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// The ambient NPC bark payload - the string a CharacterChat (0x0022) carries. The same parameter-less-root shape
	// as a stock phrase, except that its first attribute is `token=` rather than `id=`, and the token is structured:
	// `sayinchat(N)|<channel>(K)|<Group>`. The channel is not always a numbered conversation - the corpus has
	// `conversation1`, `conversation2` and `sendtell` - so it is captured as a name.
	//
	// A string codec. Note the double space before `>`: these barks carry no antitag, which is the same
	// empty-attribute slot RemoteFormatScreenCodec produces for a screen without one.
	//
	// A bark can carry more than one parameter slot (the Damsel in Distress line carries two), which is why the slots
	// are matched separately from the root rather than inlined into it.
	public static class NpcChatterCodec
	{
		private static readonly Regex ChatterParamRx = new Regex("<parameter value=\"7;(?<pvalue>[^\"]*)\" name=\"(?<pname>[^\"]*)\" />", RegexOptions.Compiled);

		// Anchored and fully specified so anything that is not a bark fails to match rather than being half-read.
		private static readonly Regex ChatterRootRx = new Regex(
			"^<remoteformat token=\"sayinchat\\((?<say>\\d+)\\)\\|(?<channel>[A-Za-z][A-Za-z0-9]*)\\((?<count>\\d+)\\)\\|(?<group>[^\"|]*)\" category=\"(?<cat>[^\"]*)\" key=\"(?<key>[^\"]*)\" knubot=\"(?<knubot>[^\"]*)\"  >"
			+ "(?<params>(?:<parameter value=\"7;[^\"]*\" name=\"[^\"]*\" />)*)</remoteformat>$",
			RegexOptions.Compiled);

		// Written out rather than routed through RemoteFormatScreenCodec.Build(): only the first attribute differs, but
		// patching `id=` to `token=` afterwards would be a blind string replace over a payload that also carries a
		// content hash. Slot values are emitted verbatim - they are already-escaped XML as captured.
		public static string Build(NpcChatterBark value)
		{
			ArgumentNullException.ThrowIfNull(value);
			var parameters = string.Concat(value.Parameters.Select(x => $"<parameter value=\"7;{x.InnerRaw}\" name=\"{x.Name}\" />"));

			return $"<remoteformat token=\"sayinchat({value.SayInChat})|{value.Channel}({value.ChannelCount})|{value.Group}\" category=\"{value.Category}\" key=\"{value.Key}\" " +
			       $"knubot=\"{value.Knubot}\"  >{parameters}</remoteformat>";
		}

		/// <summary>A stored row rebuilt for a live send.</summary>
		/// <param name="detectedCharName">
		///     Fills the row's session slot when it has one (the sendtell crier's `DetectedChar.name`, the player being
		///     addressed). Passing nothing renders the slot empty, which is what a broadcast bark does: the send path
		///     picks one line for everyone in range, so there is no single player to name.
		/// </param>
		public static string Build(NpcChatter row, string detectedCharName = "")
		{
			return Build(NpcChatterColumns.ToBark(row, detectedCharName));
		}

		/// <summary>Parses a bark payload.</summary>
		/// <exception cref="CodecParseException">
		///     The string is not a chatter root, so the importer reports it rather than storing something it cannot
		///     rebuild.
		/// </exception>
		public static NpcChatterBark Parse(string text)
		{
			var m = ChatterRootRx.Match(text ?? "");
			if (!m.Success)
				throw new CodecParseException($"NpcChatterCodec: not an ambient-bark remoteformat root: {text}");

			var slots = ChatterParamRx.Matches(m.Groups["params"].Value)
				.Select(pm => new RemoteFormatParameter(pm.Groups["pname"].Value, pm.Groups["pvalue"].Value))
				.ToList();

			return new NpcChatterBark(uint.Parse(m.Groups["say"].Value, CultureInfo.InvariantCulture), m.Groups["channel"].Value,
				uint.Parse(m.Groups["count"].Value, CultureInfo.InvariantCulture), m.Groups["group"].Value, uint.Parse(m.Groups["cat"].Value, CultureInfo.InvariantCulture),
				m.Groups["key"].Value, uint.Parse(m.Groups["knubot"].Value, CultureInfo.InvariantCulture), slots);
		}
	}

	// One bark exactly as it sits on the wire. `Key` is the escaped wire form of the content hash, and each
	// parameter's InnerRaw is the captured, already-escaped slot value.
	public sealed record NpcChatterBark(
		uint SayInChat,
		string Channel,
		uint ChannelCount,
		string Group,
		uint Category,
		string Key,
		uint Knubot,
		IReadOnlyList<RemoteFormatParameter> Parameters);

	// The bark mapped to and from its NpcChatter row. A mapper, not an encoder: the wire shape is NpcChatterCodec's,
	// the columns are the database's, and only this file knows both.
	public static class NpcChatterColumns
	{
		/// <summary>The live value of the bark's session slot, unescaped.</summary>
		/// <remarks>
		///     Never stored, but the importer needs it to prove the columns rebuild the captured string byte-for-byte
		///     before blanking it.
		/// </remarks>
		public static string SessionValueOf(NpcChatterBark bark)
		{
			ArgumentNullException.ThrowIfNull(bark);
			var slot = bark.Parameters.FirstOrDefault(p => RemoteFormatSession.IsSessionBarkSlot(p.Name));

			return slot == null
				? ""
				: RemoteFormatSession.Unescape(slot.InnerRaw);
		}

		public static NpcChatterBark ToBark(NpcChatter row, string detectedCharName = "")
		{
			ArgumentNullException.ThrowIfNull(row);

			// A session slot renders the live player; a content slot renders its stored value verbatim, escaped exactly
			// as captured (it is already-escaped XML - a `<localized>` element - so it is emitted as-is).
			var slots = row.Parameters.OrderBy(x => x.Ordinal)
				.Select(x => new RemoteFormatParameter(x.Name, RemoteFormatSession.IsSessionBarkSlot(x.Name)
					? RemoteFormatSession.Escape(detectedCharName)
					: x.Value ?? ""))
				.ToList();

			return new NpcChatterBark(row.SayInChat, row.Channel, row.ChannelCount, row.Group, row.Category, LdbKey.ToWire(row.FormatKey), row.Knubot, slots);
		}

		/// <summary>Lifts a captured bark into its columns.</summary>
		/// <remarks>
		///     The session slot's value is deliberately not carried over - it is the capturing player's name (see
		///     NpcChatter.ParameterName).
		/// </remarks>
		public static NpcChatter ToRow(NpcChatterBark bark)
		{
			ArgumentNullException.ThrowIfNull(bark);
			var row = new NpcChatter
			{
				SayInChat = bark.SayInChat,
				Channel = bark.Channel,
				ChannelCount = bark.ChannelCount,
				Group = bark.Group,
				Category = bark.Category,
				// A captured bark always carries a decodable `key=` - all 25 in the corpus do, escapes and all - so a
				// failed decode here is a parse defect and must throw rather than coerce to an all-zero key. Coercing
				// would still be caught by the importer's byte-exact round-trip, since a zero key rebuilds a different
				// `key=` string, but it would report "not round-trippable" instead of naming the real fault.
				FormatKey = LdbKey.Decode(bark.Key)
				            ?? throw new CodecParseException($"NpcChatter bark {bark.Group}/{bark.Channel}({bark.SayInChat}) carries a key that is not an LdbKey: \"{bark.Key}\""),
				Knubot = bark.Knubot
			};

			for (var i = 0; i < bark.Parameters.Count; i++)
			{
				row.Parameters.Add(new NpcChatterParameter
				{
					Ordinal = i,
					Name = bark.Parameters[i].Name,
					Value = RemoteFormatSession.IsSessionBarkSlot(bark.Parameters[i].Name)
						? null
						: bark.Parameters[i].InnerRaw
				});
			}

			return row;
		}
	}

	// Live-value policy for a bark that is about to be sent - not a codec, the same way `RemoteFormatSession` beside
	// it is not.
	public static class NpcChatterBarks
	{
		// What makes an NPC say a bark. A bark is not one thing on a timer - the captured set splits several ways on the
		// `channel` component the row already stores, and each channel names its own cue:
		//
		//   conversation1 / conversation2 -> Ambient. Idle bystander chatter (`RenownBystanderLovers`, `*Women`,
		//       `*Child`). No addressee, no cue - it is overheard, and a clock is the right model for it.
		//   sendtell                      -> Detection. The crier's `RenownCrier` line, and the only channel whose name
		//       states the delivery: a tell, i.e. one recipient. Its `DetectedChar.name` slot names the player the NPC
		//       has detected, so it fires on a player arriving and goes to that player alone. On a clock it would greet
		//       an empty street.
		//   broadcastcycle                -> Ambient, and the channel name says so outright. The Chronicler of Deeds'
		//       `AchievementsPetHeraldOfDeeds` lines - an achievements herald announcing to nobody in particular, on a
		//       clock. Same cue as the conversation channels, different publisher.
		//   eventactions                  -> Detection. The Cimmerian child's `CityLifeCimmerianChildReactToPlayer`. Two
		//       independent things say detection and nothing says ambient: the group name is literally *ReactToPlayer*,
		//       and the row carries a `PC.name` session slot, which by the one-way rule below makes it non-ambient
		//       outright since something had to pick that player. Same cue as `sendtell`, different delivery - it is
		//       said aloud rather than told.
		//   event                         -> EncounterScript. The Damsel's `RnBLadyInDistressBrainScript` cry for help,
		//       driven by the encounter's own proximity trigger, never by this tier.
		//
		// The session slot corroborates this one way only, which is why the channel is what gets switched on. Every bark
		// carrying a slot (`DetectedChar.name`, `Target.Name`) is non-ambient - it names a player, so something had to
		// pick that player - and no ambient bark carries one. The converse does not follow: a tell has exactly one
		// recipient whether or not it names them, so an unaddressed tell would still be said on detection. The channel
		// states the cue; the slot only decides whether the body has to be built per recipient (see Addressed).
		//
		// The corpus holds only these six channel values (25 rows / 8 NPC types). An unknown channel falls to Ambient
		// deliberately: a bark on a clock is a cosmetic line at the wrong moment, while a bark wired to a cue we have
		// not decoded would be a line that never fires at all.
		public static BarkTrigger TriggerOf(NpcChatterBark bark)
		{
			ArgumentNullException.ThrowIfNull(bark);

			return bark.Channel switch
			{
				"sendtell" => BarkTrigger.Detection,
				"eventactions" => BarkTrigger.Detection, // *ReactToPlayer*, and it carries a PC.name slot
				"event" => BarkTrigger.EncounterScript,
				"broadcastcycle" => BarkTrigger.Ambient, // explicit, not the default: the name states the cue

				_ => BarkTrigger.Ambient
			};
		}

		/// <summary>The bark as this recipient must see it: every session slot filled with their character name.</summary>
		/// <remarks>
		///     A bark with a session slot cannot be one body broadcast to everyone. The slot names the player the NPC is
		///     addressing - `DetectedChar.name` on the crier, `Target.Name` on the Damsel - so two players in earshot
		///     must receive two different bodies. The world layer caches each NPC type's barks once, at spawn, where no
		///     recipient exists yet; this is what turns one into a per-recipient copy at the point of send. Without it
		///     the client renders the macro literally, and the player reads "You should consider joining a guild,
		///     {DetectedChar.name}!".
		/// </remarks>
		public static NpcChatterBark Addressed(NpcChatterBark bark, string recipientName)
		{
			ArgumentNullException.ThrowIfNull(bark);

			if (!bark.Parameters.Any(p => RemoteFormatSession.IsSessionBarkSlot(p.Name)))
				return bark; // no session slot - the same body serves every recipient

			return bark with
			{
				Parameters = bark.Parameters
					.Select(p => RemoteFormatSession.IsSessionBarkSlot(p.Name)
						? new RemoteFormatParameter(p.Name, RemoteFormatSession.Escape(recipientName ?? ""))
						: p)
					.ToList()
			};
		}
	}

	// The cue an NPC bark fires on - see NpcChatterBarks.TriggerOf for how each is read off the row.
	public enum BarkTrigger
	{
		Ambient,
		Detection,
		EncounterScript
	}
}
