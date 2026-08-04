using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// One capture-timeline event: a CapturedPacket plus the two things a reader needs that the bytes do not carry -
	// what the message is CALLED, and where it sat in the session.
	//
	// `Raw` is the IIR record as the dump carried it, `Content` the same with the u32 length prefix stripped. Both
	// exist because they are what different consumers want: the codecs take Content (every Build here starts at the
	// sub-opcode), while the offsets quoted in docs/re and in capture notes are into Raw.
	public sealed class TimelineEvent
	{
		public TimelineEvent(CapturedPacket packet, TimelineLookups look)
		{
			Ticks = packet.Ticks;
			Frame = packet.Frame;
			Direction = packet.Direction;
			Tier = look.Tier(packet.Port);
			Raw = packet.Body;
			// ⚠️ The opcode alone CANNOT tell an IIR carrier from an ordinary method: opcodes are PER INTERFACE and
			// every interface numbers its first method 0x2000, so PassBlob, InitiateAuthentication, DispatchChallenge
			// and the first GameChar method are all the same number. Two things that looked like decoded IIR ids were
			// really that collision - the universe handshake's account name ("0x6c6c3165") and ~990 aggro-status
			// packets whose body[4..8] is an entity handle ("0x00027917" is Laranga).
			//
			// The ENDPOINT is what actually says which interface a packet is on (CLAUDE.md: every 0x2000 packet in the
			// corpus is GameAgent <-> GameInterface), so that is the test. The length check is a second, structural
			// one: a real IIR record opens with its own length.
			var onIirEndpoint = packet.SenderClass is Endpoints.GameAgentType or Endpoints.GameInterfaceType
			                    && packet.ReceiverClass is Endpoints.GameAgentType or Endpoints.GameInterfaceType;
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

	// Everything the timeline resolves ids against: the opcode ENUMS (so names cannot go stale the way a dump's
	// `Method` column can), the game-data tables, and the client's encrypted LDB pages.
	public sealed class TimelineLookups
	{
		private static readonly Regex RemoteFormatTag = new("<(?:remoteformat|localized)([^>]*)>", RegexOptions.Compiled);
		private static readonly Regex Attribute = new("(\\w+)=\"?([^\"\\s>]*)\"?", RegexOptions.Compiled);

		private Dictionary<(uint Category, uint Id), byte[]> _ciphertext = [];

		// The plaintext half of the LDB. Deliberately a SECOND map rather than merged into _ciphertext: one holds
		// ciphertext that still needs the key the wire carried, the other holds finished strings. Merging them would
		// let a decrypt failure read as a lookup miss and print LDB[cat][id] either way, hiding which one broke.
		private Dictionary<(uint Category, uint Id), string> _plaintext = [];

		public Dictionary<uint, string> IirNames { get; } = Enum.GetValues<IirTypes>()
			.GroupBy(v => (uint)v).ToDictionary(g => g.Key, g => g.First().ToString());

		public Dictionary<uint, string> Maps { get; private set; } = [];

		public Dictionary<uint, string> Items { get; private set; } = [];

		public Dictionary<uint, string> Spells { get; private set; } = [];

		public Dictionary<uint, string> Abilities { get; private set; } = [];

		public Dictionary<uint, string> AbilityBySlot { get; private set; } = [];

		public Dictionary<uint, List<Placed>> NpcSpawns { get; private set; } = [];

		public Dictionary<uint, List<Placed>> WorldObjects { get; private set; } = [];

		// The stat NAMES come from the typed enum rather than a parsed doc. LDB gives current and max the same name,
		// which is why the enum spells them HealthCurrent/HealthMax - the timeline wants the enum's precision.
		public Dictionary<uint, string> Stats { get; } = Enum.GetValues<CharacterStatId>()
			.GroupBy(v => (uint)v).ToDictionary(g => g.Key, g => g.First().ToString());

		public static async Task<TimelineLookups> LoadAsync(DatabaseContext db, string rdbDir)
		{
			var look = new TimelineLookups
			{
				Maps = await db.Maps.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name),
				Items = await db.Items.AsNoTracking().Where(i => i.Name != null && i.Name != "")
					.ToDictionaryAsync(i => i.Id, i => i.Name!),
				Spells = await db.Spells.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name)
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
				.ToDictionary(g => g.Key, g => g.Select(o => new Placed(o.PositionX, o.PositionY, o.PositionZ, o.Name)).ToList());

			look._ciphertext = ReportCli.EncryptedLdbBlobsFor(rdbDir);
			look._plaintext = ReportCli.PlaintextLdbStringsFor(rdbDir);
			return look;
		}

		public string Tier(int port)
		{
			return port switch
			{
				7010 => "universe", 7011 => "playeragent", 7012 => "cs", 7001 => "chat", 7017 => "game",
				0 => "?", _ => port.ToString()
			};
		}

		// Which interface each RECEIVER endpoint class is. The receiver is the object being addressed - the server's
		// *Agent on a c2s call, the client's *Interface on an s2c dispatch - so it names the interface in BOTH
		// directions. Verified over the whole corpus: all 404k Conan packets are a clean A<->B swap of one of these
		// pairs, and the receiver's enum contains the opcode in every case bar three mis-framed packets in one lossy
		// capture (their "bodies" are the first bytes of an RpcHeader).
		private static readonly Dictionary<uint, Type> InterfaceByReceiver = new()
		{
			[Endpoints.UniverseAgentType] = typeof(UniverseAgentMethods),
			[Endpoints.UniverseInterfaceType] = typeof(UniverseInterfaceMethods),
			[Endpoints.CSPlayerAgentType] = typeof(CSPlayerAgentMethods),
			[Endpoints.CSPlayerInterfaceType] = typeof(CSPlayerInterfaceMethods),
			[Endpoints.GameAgentType] = typeof(GameAgentMethods),
			[Endpoints.GameInterfaceType] = typeof(GameInterfaceMethods),
			[Endpoints.GameCharAgentType] = typeof(GameCharAgentMethods),
			[Endpoints.GameCharInterfaceType] = typeof(GameCharInterfaceMethods),
			[Endpoints.GameCharAgentProjectType] = typeof(GameCharAgentProjectMethods),
			[Endpoints.GameCharInterfaceProjectType] = typeof(GameCharInterfaceProjectMethods),
			[Endpoints.TradepostAgentType] = typeof(TradepostAgentMethods),
			[Endpoints.TradepostInterfaceType] = typeof(TradepostInterfaceMethods)
		};

		// The message's name. An IIR names itself through IirTypes; an RPC method is named by its INTERFACE, and the
		// receiver endpoint class is the only thing that says which interface a packet is on.
		//
		// ⚠️ Opcodes are PER INTERFACE - every interface numbers its first method 0x2000 - so picking the interface
		// from the tier plus the direction, as this did, is picking it by guess. The world connection alone carries
		// four interfaces (GameAgent/GameInterface, GameChar*, GameChar*Project) and trying them in a fixed order let
		// the first enum answer for all of them: 1,642 packets in one capture came out as PassBlob / DispatchPassBlob /
		// RemoveGuildFeat / DispatchGameStats / DispatchReportDimesionID when they were really the aggro poll, the
		// sprint input and the quest-marker reply. Every one of those is in TimelineNoise, so the wrong name also
		// defeated the noise filter and printed 40% of the timeline as heartbeat traffic (#558).
		//
		// A KNOWN receiver is authoritative: if its interface does not define the opcode, the answer is that we cannot
		// name it, NOT the name a different interface happens to have at that number. Falling through would rebuild
		// the bug.
		// The two classes that register by 32-bit HASH, so their frames are named from the method constant rather than
		// from an opcode that cannot tell two of them apart (#608/#613).
		private static readonly Dictionary<uint, Type> MethodsByReceiver = new()
		{
			[Endpoints.PlayerAgentType] = typeof(PlayerAgentMethods),
			[Endpoints.PlayerInterfaceType] = typeof(PlayerInterfaceMethods)
		};

		// Re-reads the frame for its `method_instance`. The catch is deliberate and narrow: this is a REPORT over
		// captures that are known to contain mis-framed buffers (a lossy capture's three such packets are noted above),
		// and a diagnostic should render those as a raw id rather than abort the whole timeline. Nothing on the wire
		// path swallows a parse failure.
		//
		// ⚠️ `InvalidDataException` is listed EXPLICITLY and is not redundant (#628). It lives in `System.IO` but
		// derives from `SystemException`, NOT from `IOException` - so the `IOException` catch that was here alone read
		// as covering it and did not. `Rpc.Parse` wraps the underlying `EndOfStreamException` (which IS an
		// `IOException`) in one, so the only exception this ever actually saw was the one slipping through, and the
		// timeline died on all three mount captures.
		private static string NameOfMethod(CapturedPacket packet, Type methods)
		{
			uint method;
			try
			{
				method = Rpc.Parse(packet.Bytes).Method;
			}
			catch (Exception e) when (e is IOException or InvalidDataException)
			{
				return $"0x{packet.WireOpcode:x4} (unparseable header)";
			}

			return Enum.GetName(methods, method) ?? $"0x{method:x8}";
		}

		public string NameOf(CapturedPacket packet, uint iir, string tier)
		{
			if (iir != 0)
				return IirNames.TryGetValue(iir, out var n) ? n : $"0x{iir:x8}";

			// PlayerAgent/PlayerInterface have no opcodes (#613). The dump's u16 is only the method varint's FIRST
			// group there and collides - `0x20EC` is both ReceivedCharacterSettings and AuthenticateLoggedIn - so the
			// frame is re-read for its whole method constant and named from that.
			if (MethodsByReceiver.TryGetValue(packet.ReceiverClass, out var methods))
				return NameOfMethod(packet, methods);

			var opcode = (ushort)packet.WireOpcode;
			if (InterfaceByReceiver.TryGetValue(packet.ReceiverClass, out var iface))
				return Enum.GetName(iface, opcode) ?? $"0x{packet.WireOpcode:x4}";

			// ChatServer frames carry NO RpcHeader, so they have no endpoint to key off and the tier is all there is
			// (TsharkCaptureReader leaves SenderClass/ReceiverClass zero for them). Its two enums do not collide with
			// each other, so direction alone separates them.
			foreach (var type in InterfacesFor(tier, packet.Direction == "c2s"))
			{
				var name = Enum.GetName(type, opcode);
				if (name != null)
					return name;
			}

			return $"0x{packet.WireOpcode:x4}";
		}

		// The endpoint-less fallback. Only the chat tier reaches it in the corpus; the other tiers are listed so a
		// packet whose dump row is missing its endpoint columns still gets a best effort rather than a bare hex id.
		private static IEnumerable<Type> InterfacesFor(string tier, bool c2s)
		{
			switch (tier)
			{
				case "universe":
					yield return c2s ? typeof(UniverseAgentMethods) : typeof(UniverseInterfaceMethods);
					break;
				// No "playeragent" case: that pair is named from its method constant (MethodsByReceiver), which needs
				// the frame, and its enums are uint-backed so a bare u16 opcode could not index them anyway (#613).
				case "cs":
					yield return c2s ? typeof(CSPlayerAgentMethods) : typeof(CSPlayerInterfaceMethods);
					break;
				case "chat":
					yield return c2s ? typeof(ChatServerOpcodes) : typeof(ChatServerInterfaceOpcodes);
					break;
				default:
					yield return c2s ? typeof(GameAgentMethods) : typeof(GameInterfaceMethods);
					yield return c2s ? typeof(GameCharAgentMethods) : typeof(GameCharInterfaceMethods);
					yield return c2s ? typeof(TradepostAgentMethods) : typeof(TradepostInterfaceMethods);
					break;
			}
		}

		// Readable text out of a body: a plain wire string, or the LDB string a remoteformat references - DECRYPTED
		// with the key the wire itself carries. That key is the whole trick: the eleven encrypted categories are not
		// readable from the client alone, and the server hands over the per-string key in the very packet that
		// references it, so a capture is self-sufficient - including for screens our own tables have never captured.
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

		// The resolved text of ONE `<remoteformat>` string that is already a string rather than a packet body - a quest
		// journal carries a dozen of them as wire strings inside the record, and each one references its own LDB line
		// with its own key. Unquoted, so a caller can compose several into one line. A string with no tag comes back
		// unchanged: journal fields are occasionally plain text rather than a reference.
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

		// The content a `<remoteformat>` tag points at. `Literal` distinguishes the real LDB line (decrypted, and worth
		// quoting) from a stand-in for one we cannot produce - a `token "..."` reference, or `LDB[cat][id]` when the
		// client pages hold no ciphertext for it or the wire carried no key.
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

			// A reference into one of the PLAINTEXT categories carries no key and needs none - the string ships
			// readable. Half the category table is this side (52000 regions, 51200 bounded_areas, 51000 npc_template,
			// 50200 item, ...), so consulting only the ciphertext map made all of it unresolvable (#561).
			if (_plaintext.TryGetValue((category, id), out var text))
				return (Flatten(text), true);

			return ($"LDB[{category}][{id}]", false);
		}

		private static string Flatten(string text)
		{
			return text.Replace('\n', ' ').Replace('\r', ' ').Trim();
		}

		// A wire string is [u16 BE length][latin-1 bytes]. Finding that FRAMING beats scraping printable bytes: the
		// length's low byte is itself often printable and arrives glued to the front of the message.
		//
		// ⚠️ This is a GUESS over a body whose grammar we do not know - it is the last resort for messages with no
		// case in DescribeCore - and a two-byte match is where the guessing goes wrong. An entity handle is the
		// trap: `[0000c350][0002792a]` reads at offset 4 as length 0x0002 with payload `79 2a`, so 292 lines in one
		// capture printed "y)" / "y+" / "y(" / "y*" - the low half of a Tortage handle, 0x79 being 'y' (#556).
		//
		// So a two-character run has to earn it: ALL of it must be letters or digits. That keeps the genuine short
		// strings in the corpus ("jo" in channel chat, "en" the language code on LoginCharacter) and rejects the
		// handle halves, which pair a letter with punctuation. It is not airtight - a handle CAN end in two letters -
		// but the real fix for a message that matters is a case in DescribeCore, not a better guess here.
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
