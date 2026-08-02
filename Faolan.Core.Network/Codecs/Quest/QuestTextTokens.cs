using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The `<remoteformat>` / `<localized>` TEXT-SLOT tokens every quest message carries in place of literal text: the
	// client resolves (id, category) against its own RDB and renders the result. `key` is a DETERMINISTIC content hash
	// (identical across characters, sessions and captures), so it is harvested data rather than a nonce.
	//
	// Not a codec - nothing here encodes or decodes a message. It is the one spelling of these four tokens, which is
	// why it exists: the exact same four formats were written out THREE times (QuestcRegion, QuestObjectiveRegion and
	// WorldQuest), plus a fourth copy of the QuestTextRef -> token mapping in WorldBroadcastBodies and two test files.
	// Every byte of the spelling matters - the trailing `knubot="0"` and its two spaces included - because the goldens
	// compare against captured bytes, so a second copy is a second chance to get it subtly wrong.
	//
	// The token is a STRING; whoever composes it into a body writes it with the wire's own framing (u16 length +
	// Latin1 via FaolanStream.WriteString, or the u32-length form for the goal objective slot).
	public static class QuestTextTokens
	{
		// The id-addressed form. `key` is passed pre-escaped (a capture already carries entities like &#36;).
		public static string RemoteFormat(uint id, uint category, string? key)
		{
			return $"<remoteformat id=\"{id}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}

		// A text record addresses its string EITHER by numeric id OR by a NAME token - `token="Noname"` rather than
		// `id="0"`. Modelling only the id form emitted `id="0"` where the wire carries a token, which is 8 bytes short
		// and desynced everything after it. Same split as the feedback records in #372.
		public static string RemoteFormatToken(string token, uint category, string? key)
		{
			return $"<remoteformat token=\"{token}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}

		// The <localized> spelling, with the exact trailing " >".
		public static string Localized(uint id, uint category, string flags, string? key)
		{
			return $"<localized id=\"{id}\" category=\"{category}\" flags=\"{flags}\" key=\"{key}\" >";
		}

		public static string LocalizedToken(string token, uint category, string flags, string? key)
		{
			return $"<localized token=\"{token}\" category=\"{category}\" flags=\"{flags}\" key=\"{key}\" >";
		}

		// The stored-key overloads. The DB holds the LDB key as the 128-bit TEA key (#520), so the 20-character
		// base-85 string is rendered here, at the wire boundary, and escaped the way retail escapes it - LdbKey.ToWire.
		// An empty key renders as `key=""`, which is what a capture with no key carried.
		public static string RemoteFormat(uint id, uint category, ProtoDecryptionKey? key)
		{
			return RemoteFormat(id, category, LdbKey.ToWire(key));
		}

		public static string RemoteFormatToken(string token, uint category, ProtoDecryptionKey? key)
		{
			return RemoteFormatToken(token, category, LdbKey.ToWire(key));
		}

		public static string Localized(uint id, uint category, string flags, ProtoDecryptionKey? key)
		{
			return Localized(id, category, flags, LdbKey.ToWire(key));
		}

		public static string LocalizedToken(string token, uint category, string flags, ProtoDecryptionKey? key)
		{
			return LocalizedToken(token, category, flags, LdbKey.ToWire(key));
		}

		// A QuestTextRef -> on-wire token. A MISSING slot falls back to an empty remoteformat so the framing stays
		// valid (the client simply renders nothing for that slot) - dropping the record instead would shorten the body
		// and desync everything after it.
		public static string Record(QuestDefinition def, QuestTextSlot slot) // TODO: Move to QuestDefinition ?
		{
			var r = def?.TextRefs.FirstOrDefault(t => t.Slot == slot);
			if (r == null)
				return RemoteFormat(0, 0, "");

			return r.IsLocalized
				? Localized(r.RefId, r.Category, r.Flags ?? "", r.KeyText)
				: RemoteFormat(r.RefId, r.Category, r.KeyText);
		}
	}
}
