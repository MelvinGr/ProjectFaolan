using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     The <c>remoteformat</c> and <c>localized</c> text-slot tokens every quest message carries in place of literal
	///     text: the client resolves (id, category) against its own RDB and renders the result. The key is a deterministic
	///     content hash, identical across characters, sessions and captures, so it is harvested data rather than a nonce.
	/// </summary>
	/// <remarks>
	///     Not a codec - nothing here encodes or decodes a message. It is the single spelling of these four tokens, and that
	///     is the point: every byte matters, the trailing <c>knubot="0"</c> and its two spaces included, because the goldens
	///     compare against captured bytes, so a second copy is a second chance to get it subtly wrong.
	///     <para>
	///         The token is a string; whoever composes it into a body writes it with the wire's own framing - u16 length plus
	///         Latin1 through FaolanStream.WriteString, or the u32-length form for the goal objective slot.
	///     </para>
	/// </remarks>
	public static class QuestTextTokens
	{
		/// <summary>
		///     The id-addressed form. <paramref name="key" /> is passed pre-escaped, a capture already carrying entities such
		///     as &amp;#36;.
		/// </summary>
		public static string RemoteFormat(uint id, uint category, string? key)
		{
			return $"<remoteformat id=\"{id}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}

		/// <summary>
		///     The token-addressed form. A text record addresses its string either by numeric id or by a name token -
		///     <c>token="Noname"</c> rather than <c>id="0"</c> - and both spellings occur, so modelling only the id form
		///     emits a body eight bytes short of the wire and desyncs everything after it.
		/// </summary>
		public static string RemoteFormatToken(string token, uint category, string? key)
		{
			return $"<remoteformat token=\"{token}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}

		/// <summary>The <c>localized</c> spelling, with the exact trailing " &gt;".</summary>
		public static string Localized(uint id, uint category, string flags, string? key)
		{
			return $"<localized id=\"{id}\" category=\"{category}\" flags=\"{flags}\" key=\"{key}\" >";
		}

		public static string LocalizedToken(string token, uint category, string flags, string? key)
		{
			return $"<localized token=\"{token}\" category=\"{category}\" flags=\"{flags}\" key=\"{key}\" >";
		}

		/// <summary>
		///     The stored-key overloads. The database holds the LDB key as the 128-bit TEA key, so the 20-character base-85
		///     string is rendered here at the wire boundary and escaped the way retail escapes it (LdbKey.ToWire). An empty
		///     key renders as <c>key=""</c>, which is what a capture with no key carries.
		/// </summary>
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

		/// <summary>
		///     A QuestTextRef as an on-wire token. A missing slot falls back to an empty remoteformat so the framing stays
		///     valid and the client renders nothing for that slot; dropping the record instead would shorten the body and
		///     desync everything after it.
		/// </summary>
		public static string Record(QuestDefinition def, QuestTextSlot slot)
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
