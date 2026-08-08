using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Text
{
	// The ambient NPC BARK payload - the string a CharacterChat (0x0022) carries. The same PARAMETER-LESS-root shape
	// as a stock phrase, except its first attribute is `token=` rather than `id=`, and the token is structured:
	// `sayinchat(N)|<channel>(K)|<Group>`. The channel is NOT always a numbered conversation - the corpus has
	// `conversation1`, `conversation2` and `sendtell` - so it is captured as a name.
	//
	// A rule-1a STRING codec (#530). Note the DOUBLE space before `>`: these barks carry no antitag, which is the
	// same empty-attribute slot RemoteFormatScreenCodec produces for a screen without one.
	//
	// A bark was assumed to carry at most one parameter slot; the Damsel in Distress line carries two (#493), which
	// is why the slots are matched separately from the root rather than inlined into it.
	public static class NpcChatterCodec
	{
		private static readonly Regex ChatterParamRx = new Regex("<parameter value=\"7;(?<pvalue>[^\"]*)\" name=\"(?<pname>[^\"]*)\" />", RegexOptions.Compiled);

		// Anchored and fully specified so anything that is NOT a bark fails to match rather than being half-read.
		private static readonly Regex ChatterRootRx = new Regex(
			"^<remoteformat token=\"sayinchat\\((?<say>\\d+)\\)\\|(?<channel>[A-Za-z][A-Za-z0-9]*)\\((?<count>\\d+)\\)\\|(?<group>[^\"|]*)\" category=\"(?<cat>[^\"]*)\" key=\"(?<key>[^\"]*)\" knubot=\"(?<knubot>[^\"]*)\"  >"
			+ "(?<params>(?:<parameter value=\"7;[^\"]*\" name=\"[^\"]*\" />)*)</remoteformat>$",
			RegexOptions.Compiled);

		// Written out rather than routed through RemoteFormatScreenCodec.Build(): only the first attribute differs,
		// but patching `id=` to `token=` afterwards would be a blind string replace over a payload that also carries
		// a content hash. Slot values are emitted VERBATIM - they are already-escaped XML as captured.
		public static string Build(NpcChatterBark value)
		{
			ArgumentNullException.ThrowIfNull(value);
			var parameters = string.Concat(value.Parameters.Select(x => $"<parameter value=\"7;{x.InnerRaw}\" name=\"{x.Name}\" />"));

			return $"<remoteformat token=\"sayinchat({value.SayInChat})|{value.Channel}({value.ChannelCount})|{value.Group}\" category=\"{value.Category}\" key=\"{value.Key}\" " +
			       $"knubot=\"{value.Knubot}\"  >{parameters}</remoteformat>";
		}

		// A stored row rebuilt for a live send. `detectedCharName` fills the row's session slot when it has one (the
		// sendtell crier's `DetectedChar.name` - the player being addressed). Passing nothing renders the slot empty,
		// which is what a broadcast bark does today: the send path picks one line for everyone in range, so there is
		// no single player to name. Per-recipient rendering is open work tracked in Gitea.
		public static string Build(NpcChatter row, string detectedCharName = "")
		{
			return Build(NpcChatterColumns.ToBark(row, detectedCharName));
		}

		// THROWS when the string is not a chatter root at all (rule 4), so the importer reports it rather than
		// storing something it cannot rebuild.
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

	// The bark <-> its NpcChatter row. A mapper, not an encoder (#530 §2 rule 1 / #533): the wire shape is
	// NpcChatterCodec's, the columns are the database's, and only this file knows both.
	public static class NpcChatterColumns
	{
		// The live value of the bark's SESSION slot, unescaped - not stored, but the importer needs it to prove the
		// columns rebuild the captured string byte-for-byte before blanking it.
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

			// A SESSION slot renders the live player; a CONTENT slot renders its stored value verbatim, escaped
			// exactly as captured (it is already-escaped XML - a `<localized>` element - so it is emitted as-is).
			var slots = row.Parameters.OrderBy(x => x.Ordinal)
				.Select(x => new RemoteFormatParameter(x.Name, RemoteFormatSession.IsSessionBarkSlot(x.Name)
					? RemoteFormatSession.Escape(detectedCharName)
					: x.Value ?? ""))
				.ToList();

			return new NpcChatterBark(row.SayInChat, row.Channel, row.ChannelCount, row.Group, row.Category, LdbKey.ToWire(row.FormatKey), row.Knubot, slots);
		}

		// Lift a captured bark into its columns. The session slot's VALUE is deliberately not carried over - it is
		// the capturing player's name (see NpcChatter.ParameterName).
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
				FormatKey = LdbKey.Decode(bark.Key) ?? new ProtoDecryptionKey(),
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

	// Live-value policy for a bark that is about to be SENT (#691) - not a codec, the same way `RemoteFormatSession`
	// beside it is not.
	public static class NpcChatterBarks
	{
		// What makes an NPC say a bark (#694). A bark is NOT one thing on a timer - the captured set splits three ways
		// on the `channel` component the row already stores, and each channel names its own cue:
		//
		//   conversation1 / conversation2 -> AMBIENT. Idle bystander chatter (`RenownBystanderLovers`, `*Women`,
		//       `*Child`). No addressee, no cue - it is overheard, and a clock is the right model for it.
		//   sendtell                      -> DETECTION. The crier's `RenownCrier` line, and the only channel whose
		//       name states the delivery: a TELL, i.e. one recipient. Its `DetectedChar.name` slot names the player
		//       the NPC has DETECTED, so it fires on a player arriving and goes to that player alone. Firing it on a
		//       clock is what made the crier greet an empty street.
		//   broadcastcycle                -> AMBIENT, and the channel name says so outright: a BROADCAST on a CYCLE.
		//       The Chronicler of Deeds' `AchievementsPetHeraldOfDeeds` lines - an achievements herald announcing to
		//       nobody in particular, on a clock. Same cue as the conversation channels, different publisher.
		//   event                         -> ENCOUNTER SCRIPT. The Damsel's `RnBLadyInDistressBrainScript` cry for
		//       help, driven by the encounter's own proximity trigger (#502), never by this tier. It was ALSO in the
		//       ambient pool, so it double-fired on the timer as well as on the trigger.
		//
		// The session slot corroborates this, but ONE WAY only, and the difference is why the channel is what gets
		// switched on. Every bark carrying a slot (`DetectedChar.name`, `Target.Name`) is non-ambient - it names a
		// player, so something had to pick that player - and no ambient bark carries one. The converse is false: the
		// crier has TWO `sendtell` lines and only one names anybody (SayInChat 57; SayInChat 10 is an unaddressed
		// tell). A tell still has exactly one recipient, so it is still said on detection. The channel states the CUE;
		// the slot only decides whether the body has to be built per-recipient (see Addressed).
		//
		// ⚠️ The corpus holds only these five channel values (24 rows / 7 NPC types). An unknown channel falls to
		// Ambient deliberately: a bark on a clock is a cosmetic line at the wrong moment, while a bark wired to a cue
		// we have not decoded would be a line that never fires at all.
		public static BarkTrigger TriggerOf(NpcChatterBark bark)
		{
			ArgumentNullException.ThrowIfNull(bark);

			return bark.Channel switch
			{
				"sendtell" => BarkTrigger.Detection,
				"event" => BarkTrigger.EncounterScript,
				"broadcastcycle" => BarkTrigger.Ambient, // explicit, not the default: the name states the cue

				_ => BarkTrigger.Ambient
			};
		}

		// The bark as this recipient must see it: every SESSION slot filled with THEIR character name.
		//
		// ⚠️ **A bark with a session slot cannot be one body broadcast to everyone.** The slot names the player the
		// NPC is addressing - `DetectedChar.name` on the crier, `Target.Name` on the Damsel - so two players in
		// earshot must receive two different bodies. The world layer caches each NPC type's barks once, at spawn,
		// where no recipient exists yet; this is what turns one into a per-recipient copy at the point of send.
		//
		// Without it the client renders the macro LITERALLY, and the player reads
		// "You should consider joining a guild, {DetectedChar.name}!".
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

	// The cue an NPC bark fires on (#694) - see NpcChatterBarks.TriggerOf for how each is read off the row.
	public enum BarkTrigger
	{
		Ambient,
		Detection,
		EncounterScript
	}
}
