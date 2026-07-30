using System;
using System.Collections.Generic;
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
using Faolan.Importer.Extractors;
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

		public Dictionary<uint, string> IirNames { get; } = Enum.GetValues<GameServer0X2000Opcodes>()
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

			var abilities = await db.Set<Faolan.Core.Database.Tables.Ability>().AsNoTracking()
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

		// The message's name. An IIR names itself through GameServer0X2000Opcodes; an RPC method is named by its
		// interface, which the TIER plus the DIRECTION pick - *AgentOpcodes are c2s, *InterfaceOpcodes are s2c.
		public string NameOf(CapturedPacket packet, uint iir, string tier)
		{
			if (iir != 0)
				return IirNames.TryGetValue(iir, out var n) ? n : $"0x{iir:x8}";

			var c2s = packet.Direction == "c2s";
			var opcode = (ushort)packet.Opcode;
			foreach (var type in InterfacesFor(tier, c2s))
			{
				var name = Enum.GetName(type, opcode);
				if (name != null)
					return name;
			}

			return $"0x{packet.Opcode:x4}";
		}

		private static IEnumerable<Type> InterfacesFor(string tier, bool c2s)
		{
			switch (tier)
			{
				case "universe":
					yield return c2s ? typeof(UniverseAgentOpcodes) : typeof(UniverseInterfaceOpcodes);
					break;
				case "playeragent":
					yield return c2s ? typeof(PlayerAgentOpcodes) : typeof(PlayerInterfaceOpcodes);
					break;
				case "cs":
					yield return c2s ? typeof(CSPlayerAgentOpcodes) : typeof(CSPlayerInterfaceOpcodes);
					break;
				case "chat":
					yield return c2s ? typeof(ChatServerOpcodes) : typeof(ChatServerInterfaceOpcodes);
					break;
				default:
					// The world tier carries the create-editor's GameChar* interfaces on the same connection as the
					// GameAgent/GameInterface pair, so both are tried.
					yield return c2s ? typeof(GameAgentOpcodes) : typeof(GameInterfaceOpcodes);
					yield return c2s ? typeof(GameCharAgentOpcodes) : typeof(GameCharInterfaceOpcodes);
					yield return c2s ? typeof(TradepostAgentOpcodes) : typeof(TradepostInterfaceOpcodes);
					break;
			}
		}

		// Readable text out of a body: a plain wire string, or the LDB string a remoteformat references - DECRYPTED
		// with the key the wire itself carries. That key is the whole trick: the eleven encrypted categories are not
		// readable from the client alone, and the server hands over the per-string key in the very packet that
		// references it, so a capture is self-sufficient - including for screens our own tables have never captured.
		public string Text(byte[] raw)
		{
			var ascii = Encoding.Latin1.GetString(raw);
			var tag = RemoteFormatTag.Match(ascii);
			if (tag.Success)
			{
				var attrs = Attribute.Matches(tag.Groups[1].Value)
					.ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
				if (attrs.TryGetValue("token", out var token))
					return $"token \"{token}\"" + (attrs.TryGetValue("category", out var tc) ? $" (cat {tc})" : "");

				if (attrs.TryGetValue("id", out var idText) && attrs.TryGetValue("category", out var catText)
				    && uint.TryParse(idText, out var id) && uint.TryParse(catText, out var category))
				{
					if (attrs.TryGetValue("key", out var key) && _ciphertext.TryGetValue((category, id), out var blob)
					    && LdbKey.Decode(key) is { } decoded && LdbKey.Decrypt(blob, decoded) is { } plain)
						return $"\"{Flatten(plain)}\"";

					return $"LDB[{category}][{id}]";
				}
			}

			return PlainString(raw);
		}

		private static string Flatten(string text)
		{
			return text.Replace('\n', ' ').Replace('\r', ' ').Trim();
		}

		// A wire string is [u16 BE length][latin-1 bytes]. Finding that FRAMING beats scraping printable bytes: the
		// length's low byte is itself often printable and arrives glued to the front of the message.
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
