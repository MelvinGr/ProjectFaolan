using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	/// <summary>
	///     One capture-timeline event: a <see cref="CapturedPacket" /> plus the two things a reader needs that the
	///     bytes do not carry - what the message is called, and where it sat in the session.
	/// </summary>
	/// <remarks>
	///     <c>Raw</c> is the IIR record as the dump carried it and <c>Content</c> the same with the u32 length prefix
	///     stripped. Both exist because different consumers want different ones: the codecs take Content, since every
	///     Build starts at the sub-opcode, while the offsets quoted in docs/re and in capture notes are into Raw.
	/// </remarks>
	public sealed class TimelineEvent
	{
		public TimelineEvent(CapturedPacket packet, TimelineLookups look)
		{
			Ticks = packet.Ticks;
			Frame = packet.Frame;
			Direction = packet.Direction;
			Tier = TimelineLookups.Tier(packet);
			Raw = packet.Body;
			// The opcode alone cannot tell an IIR carrier from an ordinary method: opcodes are per interface and every
			// interface numbers its first method 0x2000, so PassBlob, InitiateAuthentication, DispatchChallenge and
			// the first GameChar method all render the same. Values that look like decoded IIR ids are routinely that
			// collision - the universe handshake's account name, or an aggro-status packet whose body[4..8] is an
			// entity handle.
			//
			// The endpoint is what says which interface a packet is on, so that is the test. The length check is a
			// second, structural one: a real IIR record opens with its own length.
			var onIirEndpoint = packet.SenderClass is RpcInterfaces.GameAgentType or RpcInterfaces.GameInterfaceType
			                    && packet.ReceiverClass is RpcInterfaces.GameAgentType or RpcInterfaces.GameInterfaceType;
			Iir = onIirEndpoint && packet.IsIirCarrier && Raw.Length >= 8 && Raw.ReadU32Be(0) == Raw.Length - 4
				? packet.SubOpcode
				: 0;
			Content = Iir != 0 && Raw.Length >= 4 ? Raw[4..] : Raw;
			Name = look.NameOf(packet, Iir, Tier);
		}

		public long Ticks { get; }

		public int Frame { get; }

		public string Direction { get; }

		public string Tier { get; }

		public uint Iir { get; }

		public byte[] Raw { get; }

		public byte[] Content { get; }

		public string Name { get; }
	}

	/// <summary>
	///     Everything the timeline resolves ids against: the opcode enums (so names cannot go stale the way a dump's
	///     <c>Method</c> column can), the game-data tables, and the client's encrypted LDB pages.
	/// </summary>
	public sealed class TimelineLookups
	{
		private static readonly Regex RemoteFormatTag = new Regex("<(?:remoteformat|localized)([^>]*)>", RegexOptions.Compiled);
		private static readonly Regex Attribute = new Regex("(\\w+)=\"?([^\"\\s>]*)\"?", RegexOptions.Compiled);

		private Dictionary<(uint Category, uint Id), byte[]> _ciphertext = [];

		/// <remarks>
		///     The plaintext half of the LDB. Deliberately a second map rather than merged into the ciphertext one:
		///     that holds strings still needing the key the wire carried, this holds finished strings. Merging them
		///     would let a decrypt failure read as a lookup miss and print the placeholder either way, hiding which
		///     one broke.
		/// </remarks>
		private Dictionary<(uint Category, uint Id), string> _plaintext = [];

		public Dictionary<uint, string> IirNames { get; } = Enum.GetValues<IirTypes>()
			.GroupBy(v => (uint)v).ToDictionary(g => g.Key, g => g.First().ToString());

		public Dictionary<uint, string> Maps { get; private set; } = [];

		public Dictionary<uint, string> Items { get; private set; } = [];

		public Dictionary<uint, string> Spells { get; private set; } = [];

		public Dictionary<uint, string> Abilities { get; private set; } = [];

		/// <summary>
		///     Combo id -> the combo's name, for the <c>combo_id</c> a behaviour record's <c>CommandMessage</c> carries.
		/// </summary>
		/// <remarks>
		///     The wire value is the <c>Combos</c> primary key, so a swing's combo resolves with no correlation at all.
		///     Worth stating because combos are not in <c>Spells</c> - they live in their own catalog - so the obvious
		///     lookup misses every one of them.
		/// </remarks>
		public Dictionary<uint, string> Combos { get; private set; } = [];

		public Dictionary<uint, string> AbilityBySlot { get; private set; } = [];

		public Dictionary<uint, List<Placed>> NpcSpawns { get; private set; } = [];

		public Dictionary<uint, List<Placed>> WorldObjects { get; private set; } = [];

		/// <remarks>
		///     The stat names come from the typed enum rather than a parsed doc: the LDB gives current and max the same
		///     name, which is why the enum spells them HealthCurrent and HealthMax - the timeline wants that precision.
		/// </remarks>
		public Dictionary<uint, string> Stats { get; } = Enum.GetValues<CharacterStatId>()
			.GroupBy(v => (uint)v).ToDictionary(g => g.Key, g => g.First().ToString());

		public static async Task<TimelineLookups> LoadAsync(DatabaseContext db, string rdbDir)
		{
			var look = new TimelineLookups
			{
				Maps = await db.Maps.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name),
				Items = await db.Items.AsNoTracking().Where(i => i.Name != null && i.Name != "")
					.ToDictionaryAsync(i => i.Id, i => i.Name!),
				Spells = await db.Spells.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name),
				Combos = await db.Set<Core.Database.Tables.Combo>().AsNoTracking()
					.Where(c => c.Name != "").ToDictionaryAsync(c => c.Id, c => c.Name)
			};

			var abilities = await db.Set<Core.Database.Tables.Ability>().AsNoTracking()
				.Select(a => new { a.Id, a.Slot, a.Name }).ToListAsync();
			look.Abilities = abilities.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Name);
			look.AbilityBySlot = abilities.GroupBy(a => a.Slot).ToDictionary(g => g.Key, g => g.First().Name);

			look.NpcSpawns = (await db.NpcSpawns.AsNoTracking()
					.Select(s => new { s.MapId, s.PositionX, s.PositionY, s.PositionZ, Name = s.NpcTemplate!.Name })
					.ToListAsync())
				.GroupBy(s => s.MapId)
				.ToDictionary(g => g.Key, g => g.Select(s => new Placed(s.PositionX, s.PositionY, s.PositionZ, s.Name)).ToList());

			look.WorldObjects = (await db.WorldObjects.AsNoTracking()
					.Select(o => new { o.MapId, o.PositionX, o.PositionY, o.PositionZ, o.Name }).ToListAsync())
				.GroupBy(o => o.MapId)
				.ToDictionary(g => g.Key, g => g.Select(o => new Placed(o.PositionX, o.PositionY, o.PositionZ, o.Name ?? "(unnamed world object)")).ToList());

			look._ciphertext = ReportCli.EncryptedLdbBlobsFor(rdbDir);
			look._plaintext = ReportCli.PlaintextLdbStringsFor(rdbDir);
			return look;
		}

		/// <summary>
		///     Which service tier a packet sat on.
		/// </summary>
		/// <remarks>
		///     Deliberately not a port lookup: the ports differ between this deployment and retail, and every capture
		///     in the corpus is retail's, so a port map would recognise only the tiers that happen to agree - and a
		///     tier the report does not recognise falls back to the game opcode tables, which names chat and CS frames
		///     after unrelated game methods.
		///     The deployment-independent signals are the dump's <c>Protocol</c> column for chat (a ChatServer frame
		///     carries no RpcHeader at all) and the frame's own endpoint class for everything else. The port is kept
		///     only as a last resort, for a frame whose dump row has neither.
		/// </remarks>
		public static string Tier(CapturedPacket packet)
		{
			if (packet.Protocol == "ChatServer")
				return "chat";

			var endpoint = packet.ReceiverClass != 0 ? packet.ReceiverClass : packet.SenderClass;
			switch (endpoint)
			{
				case RpcInterfaces.UniverseAgentType:
				case RpcInterfaces.UniverseInterfaceType:
					return "universe";

				case RpcInterfaces.PlayerAgentType:
				case RpcInterfaces.PlayerInterfaceType:
				case RpcInterfaces.PlayerAgentServiceConnectionType:
					return "playeragent";

				case RpcInterfaces.CSPlayerAgentType:
				case RpcInterfaces.CSPlayerInterfaceType:
					return "cs";

				case RpcInterfaces.GameAgentType:
				case RpcInterfaces.GameInterfaceType:
				case RpcInterfaces.GameCharAgentType:
				case RpcInterfaces.GameCharInterfaceType:
				case RpcInterfaces.GameCharAgentProjectType:
				case RpcInterfaces.GameCharInterfaceProjectType:
				case RpcInterfaces.TradepostAgentType:
				case RpcInterfaces.TradepostInterfaceType:
					return "game";

				default:
					return packet.Port == 0 ? "?" : packet.Port.ToString();
			}
		}

		/// <remarks>
		///     Which interface each receiver endpoint class is. The receiver is the object being addressed - the
		///     server's *Agent on a c2s call, the client's *Interface on an s2c dispatch - so it names the interface in
		///     both directions. Verified over the whole corpus: every Conan packet is a clean swap of one of these
		///     pairs, and the receiver's enum contains the opcode in every case bar a handful of mis-framed packets in
		///     one lossy capture, whose bodies are the first bytes of an RpcHeader.
		/// </remarks>
		private static readonly Dictionary<uint, Type> InterfaceByReceiver = new Dictionary<uint, Type>
		{
			[RpcInterfaces.UniverseAgentType] = typeof(UniverseAgentMethods),
			[RpcInterfaces.UniverseInterfaceType] = typeof(UniverseInterfaceMethods),
			[RpcInterfaces.CSPlayerAgentType] = typeof(CSPlayerAgentMethods),
			[RpcInterfaces.CSPlayerInterfaceType] = typeof(CSPlayerInterfaceMethods),
			[RpcInterfaces.GameAgentType] = typeof(GameAgentMethods),
			[RpcInterfaces.GameInterfaceType] = typeof(GameInterfaceMethods),
			[RpcInterfaces.GameCharAgentType] = typeof(GameCharAgentMethods),
			[RpcInterfaces.GameCharInterfaceType] = typeof(GameCharInterfaceMethods),
			[RpcInterfaces.GameCharAgentProjectType] = typeof(GameCharAgentProjectMethods),
			[RpcInterfaces.GameCharInterfaceProjectType] = typeof(GameCharInterfaceProjectMethods),
			[RpcInterfaces.TradepostAgentType] = typeof(TradepostAgentMethods),
			[RpcInterfaces.TradepostInterfaceType] = typeof(TradepostInterfaceMethods)
		};

		/// <summary>
		///     The message's name. An IIR names itself through <see cref="IirTypes" />; an RPC method is named by its
		///     interface, and the receiver endpoint class is the only thing that says which interface a packet is on.
		/// </summary>
		/// <remarks>
		///     Opcodes are per interface - every interface numbers its first method 0x2000 - so picking the interface
		///     from the tier plus the direction is picking it by guess. The world connection alone carries four
		///     interfaces, and trying them in a fixed order lets the first enum answer for all of them, which renders
		///     the aggro poll, the sprint input and the quest-marker reply under unrelated names and defeats the noise
		///     filter along with it.
		///     A known receiver is therefore authoritative: if its interface does not define the opcode, the answer is
		///     that the message cannot be named, never the name a different interface happens to have at that number.
		/// </remarks>
		/// <remarks>
		///     The two classes that register by 32-bit hash, so their frames are named from the method constant rather
		///     than from an opcode that cannot tell two of them apart.
		/// </remarks>
		private static readonly Dictionary<uint, Type> MethodsByReceiver = new Dictionary<uint, Type>
		{
			[RpcInterfaces.PlayerAgentType] = typeof(PlayerAgentMethods),
			[RpcInterfaces.PlayerInterfaceType] = typeof(PlayerInterfaceMethods)
		};

		/// <summary>
		///     Re-reads the frame for its <c>method_instance</c>.
		/// </summary>
		/// <remarks>
		///     The catch is deliberate and narrow: this is a report over captures known to contain mis-framed buffers,
		///     and a diagnostic should render those as a raw id rather than abort the whole timeline. Nothing on the
		///     wire path swallows a parse failure.
		///     <c>InvalidDataException</c> is listed explicitly and is not redundant: it lives in <c>System.IO</c> but
		///     derives from <c>SystemException</c>, not from <c>IOException</c>, and <c>Rpc.Parse</c> wraps the
		///     underlying <c>EndOfStreamException</c> in one - so an <c>IOException</c> catch alone looks like it
		///     covers this and does not.
		/// </remarks>
		private static string NameOfMethod(CapturedPacket packet, Type methods)
		{
			uint method;
			try
			{
				method = Rpc.Parse(packet.Bytes).Method;
			}
			catch (Exception e) when (e is IOException or InvalidDataException)
			{
				// The dump already recorded the method - the Wireshark dissector read it off the real frame, before
				// the rebuild had a chance to lose anything - so a header that cannot be re-framed is not a dead end.
				// The frames themselves are fine; it is the reconstruction of their RpcHeader that fails.
				method = packet.MethodId;
			}

			return Enum.GetName(methods, method) ?? $"0x{method:x8}";
		}

		public string NameOf(CapturedPacket packet, uint iir, string tier)
		{
			if (iir != 0)
				return IirNames.TryGetValue(iir, out var n) ? n : $"0x{iir:x8}";

			// Every Conan frame is named from its `method_instance`, re-read out of the header. Comparing the dump's
			// u16 against the enum directly would compare two different spaces: an enum member is the method
			// (`ChatVicinity = 0x02`) while the dump records the wire rendering of it (`0x2002`). That is the mismatch
			// `CapturedPacket.Wire(...)` exists to bridge.
			if (MethodsByReceiver.TryGetValue(packet.ReceiverClass, out var methods))
				return NameOfMethod(packet, methods);

			if (InterfaceByReceiver.TryGetValue(packet.ReceiverClass, out var iface))
				return NameOfMethod(packet, iface);

			// ChatServer frames carry no RpcHeader, so they have no endpoint to key off and the tier is all there is
			// (TsharkCaptureReader leaves SenderClass and ReceiverClass zero for them). Its two enums do not collide
			// with each other, so direction alone separates them. Here the dump's u16 is the opcode - chat has no
			// method field to render, which is why these are the only two `*Opcodes` enums in the codebase.
			var opcode = (ushort)packet.WireOpcode;
			foreach (var type in InterfacesFor(tier, packet.Direction == "c2s"))
			{
				var name = Enum.GetName(type, opcode);
				if (name != null)
					return name;
			}

			return $"0x{packet.WireOpcode:x4}";
		}

		/// <summary>
		///     The endpoint-less fallback. Only the chat tier reaches it in the corpus; the other tiers are listed so a
		///     packet whose dump row is missing its endpoint columns still gets a best effort rather than a bare hex id.
		/// </summary>
		private static IEnumerable<Type> InterfacesFor(string tier, bool c2s)
		{
			switch (tier)
			{
				case "universe":
				{
					yield return c2s ? typeof(UniverseAgentMethods) : typeof(UniverseInterfaceMethods);
					break;
				}

				// No "playeragent" case: that pair is named from its method constant (MethodsByReceiver), which needs
				// the frame, and its enums are uint-backed so a bare u16 opcode could not index them anyway.
				case "cs":
				{
					yield return c2s ? typeof(CSPlayerAgentMethods) : typeof(CSPlayerInterfaceMethods);
					break;
				}

				case "chat":
				{
					yield return c2s ? typeof(ChatServerOpcodes) : typeof(ChatServerInterfaceOpcodes);
					break;
				}

				default:
				{
					yield return c2s ? typeof(GameAgentMethods) : typeof(GameInterfaceMethods);
					yield return c2s ? typeof(GameCharAgentMethods) : typeof(GameCharInterfaceMethods);
					yield return c2s ? typeof(TradepostAgentMethods) : typeof(TradepostInterfaceMethods);
					break;
				}
			}
		}

		/// <summary>
		///     Readable text out of a body: a plain wire string, or the LDB string a remoteformat references, decrypted
		///     with the key the wire itself carries.
		/// </summary>
		/// <remarks>
		///     That key is the whole trick: the encrypted categories are not readable from the client alone, and the
		///     server hands over the per-string key in the very packet that references it, so a capture is
		///     self-sufficient - including for screens no local table has ever captured.
		/// </remarks>
		public string Text(byte[] raw)
		{
			var tag = RemoteFormatTag.Match(Encoding.Latin1.GetString(raw));
			if (!tag.Success)
				return PlainString(raw);

			var (text, literal) = Resolve(tag);
			return text.Length == 0 ? PlainString(raw)
				: literal ? $"\"{text}\""
				: text;
		}

		/// <summary>
		///     The resolved text of one <c>&lt;remoteformat&gt;</c> string that is already a string rather than a packet
		///     body - a quest journal carries a dozen of them as wire strings inside the record, each referencing its
		///     own LDB line with its own key.
		/// </summary>
		/// <remarks>
		///     Unquoted, so a caller can compose several into one line. A string with no tag comes back unchanged:
		///     journal fields are occasionally plain text rather than a reference.
		/// </remarks>
		public string Line(string remoteFormat)
		{
			if (string.IsNullOrEmpty(remoteFormat))
				return "";

			var tag = RemoteFormatTag.Match(remoteFormat);
			if (!tag.Success)
				return Flatten(remoteFormat);

			var (text, _) = Resolve(tag);
			return text.Length == 0 ? Flatten(remoteFormat) : text;
		}

		/// <summary>
		///     The content a <c>&lt;remoteformat&gt;</c> tag points at. <c>Literal</c> distinguishes the real LDB line
		///     - decrypted, and worth quoting - from a stand-in for one that cannot be produced: a <c>token "..."</c>
		///     reference, or <c>LDB[cat][id]</c> when the client pages hold no ciphertext for it or the wire carried no
		///     key.
		/// </summary>
		private (string Text, bool Literal) Resolve(Match tag)
		{
			var attrs = Attribute.Matches(tag.Groups[1].Value)
				.ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
			if (attrs.TryGetValue("token", out var token))
				return ($"token \"{token}\"" + (attrs.TryGetValue("category", out var tc) ? $" (cat {tc})" : ""), false);

			if (!attrs.TryGetValue("id", out var idText) || !attrs.TryGetValue("category", out var catText)
			    || !uint.TryParse(idText, out var id) || !uint.TryParse(catText, out var category))
				return ("", false);

			if (attrs.TryGetValue("key", out var key) && _ciphertext.TryGetValue((category, id), out var blob)
			    && LdbKey.Decode(key) is { } decoded && LdbKey.Decrypt(blob, decoded) is { } plain)
				return (Flatten(plain), true);

			// A reference into one of the plaintext categories carries no key and needs none - the string ships
			// readable. Half the category table is on that side (52000 regions, 51200 bounded_areas, 51000
			// npc_template, 50200 item, ...), so the ciphertext map alone cannot resolve it.
			if (_plaintext.TryGetValue((category, id), out var text))
				return (Flatten(text), true);

			return ($"LDB[{category}][{id}]", false);
		}

		private static string Flatten(string text)
		{
			return text.Replace('\n', ' ').Replace('\r', ' ').Trim();
		}

		/// <summary>
		///     Scans a body for a wire string, which is [u16 big-endian length][latin-1 bytes].
		/// </summary>
		/// <remarks>
		///     Finding that framing beats scraping printable bytes: the length's low byte is itself often printable and
		///     arrives glued to the front of the message.
		///     This is still a guess over a body whose grammar is unknown - the last resort for messages with no case
		///     in DescribeCore - and a two-byte match is where the guessing goes wrong. An entity handle is the trap:
		///     <c>[0000c350][0002792a]</c> reads at offset 4 as length 0x0002 with payload <c>79 2a</c>, which renders
		///     as the low half of a handle.
		///     So a two-character run has to earn it: all of it must be letters or digits. That keeps the genuine short
		///     strings in the corpus ("jo" in channel chat, "en" the language code on LoginCharacter) and rejects the
		///     handle halves, which pair a letter with punctuation. It is not airtight - a handle can end in two
		///     letters - but the fix for a message that matters is a case in DescribeCore, not a better guess here.
		/// </remarks>
		private const int UnambiguousLength = 3;

		private static string PlainString(byte[] raw)
		{
			var best = "";
			for (var i = 0; i + 2 < raw.Length; i++)
			{
				int n = raw.ReadU16Be(i);
				if (n < 2 || i + 2 + n > raw.Length)
					continue;

				var candidate = raw.AsSpan(i + 2, n);
				var printable = true;
				foreach (var b in candidate)
				{
					if (b is < 32 or > 126 && b != 9 && b != 10 && b != 13)
					{
						printable = false;
						break;
					}

					if (n < UnambiguousLength && !char.IsAsciiLetterOrDigit((char)b))
					{
						printable = false;
						break;
					}
				}

				if (printable && n > best.Length)
					best = Encoding.Latin1.GetString(candidate);
			}

			best = Regex.Replace(best, "<[^>]+>", "").Trim();
			return best.Length > 1 ? $"\"{best}\"" : "";
		}

		public readonly record struct Placed(float X, float Y, float Z, string Name);
	}
}
