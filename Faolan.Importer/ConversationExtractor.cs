using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Faolan.Importer
{
	// A captured on-wire packet with the metadata the extractors need, uniform across the .json dumps and the
	// .pcapng analyzer output.
	public readonly record struct CapturedPacket(long Ticks, bool IsDownload, uint Opcode, uint SubOpcode, byte[] Bytes);

	// One extracted conversation SCREEN, keyed by its answer-index PATH within the NPC's dialogue tree. The path is
	// the sequence of option indices the player picked to reach this screen ("" = the greeting, "0" = after picking
	// option 0, "0>1" = then option 1, ...). Packets are the ordered server responses to replay for that screen.
	// NpcName is resolved from the same capture's FA015807 spawn for the clicked handle (the stable identity the
	// runtime routes on; the handle/instance are session-volatile and only used to re-address the replay).
	// (Mode/Field3 were the OLD key but both are session-volatile counters, not branch ids - the AnswerPath is the
	// stable branch key; memory protocol_conversation_routing.)
	public sealed record ConversationEntry(uint TypeId, string AnswerPath, uint CapturedNpcHandle, uint CapturedInstance, List<byte[]> Packets, string NpcName);

	// Reconstructs each NPC's dialogue as a TREE of screens keyed by answer-index PATH, from the captured request/
	// response flow. The grammar (memory protocol_conversation_routing): a conversation INSTANCE = TalkToNpc(f3=0)
	// [greeting] then the run of server responses + the player's KnubotAnswer(idx) picks, until the next greeting (a
	// re-walk). The greeting's responses are the "" screen; the responses after the player's first pick (idx i0) are
	// the "i0" screen; after the next pick they're "i0>i1"; etc. The branches are spread across the re-walks (each
	// Mode picked different options), so MERGING the instances by answer-path unions the full option tree.
	// `packets` must be one capture's GameServer packets in chronological order.
	public static class ConversationExtractor
	{
		private const uint InteractSubOpcode = 0x642CD3D6;
		private const uint KnubotAnswerSubOpcode = 0x4D7D9123;
		private const uint Opcode2000 = 0x2000;
		private const uint Opcode2015 = 0x2015;

		private const uint SpawnSubOpcode = 0xFA015807;
		private const uint TalkToNpcActionType = 0x16;

		// The Knubot conversation-display response sub-opcodes (642CD3D6 echo excluded: it is a generic interact ack):
		// OpenChatWindow (045B0B0F), AppendText (4A3D899A), AnswerList (F164A4A5), CloseChatWindow (C4AF39EB).
		private static readonly HashSet<uint> ResponseSubOpcodes = [0x4A3D899A, 0xF164A4A5, 0x045B0B0F, 0xC4AF39EB];

		public static List<ConversationEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var namesByHandle = BuildSpawnNameMap(packets);
			var entries = new List<ConversationEntry>();

			// Per-conversation-instance state, reset at each greeting (TalkToNpc f3=0).
			var active = false;
			uint typeId = 0, npc = 0, instance = 0;
			var path = ""; // answer-index path of the CURRENT screen (the one whose responses we're collecting)
			var screen = new List<byte[]>();

			void Flush()
			{
				if (active && screen.Count > 0)
					entries.Add(new ConversationEntry(typeId, path, npc, instance, [..screen], namesByHandle.GetValueOrDefault(npc)));
			}

			foreach (var p in packets)
			{
				if (!p.IsDownload && p.Opcode == Opcode2000 && p.SubOpcode == InteractSubOpcode)
				{
					var body = GetBody(p.Bytes);
					if (body.Length < 26 || BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(18, 4)) != TalkToNpcActionType)
						continue;

					var argLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(22, 4));
					if (26 + argLen > body.Length)
						continue;

					var (parsedTypeId, _, field3, parsedNpc) = ParseArgs(body.AsSpan(26, argLen));
					if (field3 != 0)
						continue; // a non-greeting TalkToNpc (retransmit / options re-request) - not an instance boundary

					// A greeting opens a new conversation instance: flush the previous one's last screen, then reset.
					Flush();
					active = true;
					typeId = parsedTypeId;
					npc = parsedNpc;
					instance = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
					path = "";
					screen = [];
				}
				else if (!p.IsDownload && p.Opcode == Opcode2000 && p.SubOpcode == KnubotAnswerSubOpcode && active)
				{
					// The player picked option `idx`: the current screen is complete (emit it), and the responses that
					// follow belong to the child screen reached by that option.
					var body = GetBody(p.Bytes);
					if (body.Length < 4)
						continue;

					var idx = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(body.Length - 4, 4));
					Flush();
					path = path.Length == 0
						? idx.ToString()
						: $"{path}>{idx}";
					screen = [];
				}
				else if (p.IsDownload && active &&
				         (p.Opcode == Opcode2015 || (p.Opcode == Opcode2000 && ResponseSubOpcodes.Contains(p.SubOpcode))))
				{
					screen.Add(p.Bytes);
				}
			}

			Flush();
			return entries;
		}

		// Merges screens from many captures/instances, deduped by (NpcName, TypeId, AnswerPath) - the stable branch
		// key. This is what UNIONS the option tree: each conversation re-walk (a different Mode) explored a different
		// option at a screen, so it contributes a different answer-path screen; merging them assembles the full tree.
		// Keyed by NPC NAME (stable), not the session-volatile handle. Prefers an entry whose captured player instance
		// is a 4-byte varint (re-addresses to any normal character), then the most packets.
		public static List<ConversationEntry> Merge(IEnumerable<ConversationEntry> all)
		{
			var best = new Dictionary<(string, uint, string), (int score, ConversationEntry entry)>();
			foreach (var e in all)
			{
				var identity = string.IsNullOrEmpty(e.NpcName)
					? $"#{e.CapturedNpcHandle}"
					: e.NpcName;
				var key = (identity, e.TypeId, e.AnswerPath);
				var score = (VarintLength(e.CapturedInstance) == 4
					? 1_000_000
					: 0) + e.Packets.Count;
				if (!best.TryGetValue(key, out var cur) || score > cur.score)
					best[key] = (score, e);
			}

			return best
				.Values
				.Select(v => v.entry)
				.ToList();
		}

		// Maps each FA015807 spawn's handle to its embedded name (0x0A <len> <utf8>), so a conversation's NPC handle
		// can be resolved to a stable name within the same capture.
		internal static Dictionary<uint, string> BuildSpawnNameMap(IReadOnlyList<CapturedPacket> packets)
		{
			var map = new Dictionary<uint, string>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || p.Opcode != Opcode2000 || p.SubOpcode != SpawnSubOpcode)
					continue;

				var body = GetBody(p.Bytes);
				if (body.Length < 16)
					continue;

				var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
				var name = ReadSpawnName(body);
				if (name != null)
					map[handle] = name;
			}

			return map;
		}

		// The 0x2000 body within the full on-wire packet: [len4][crc4][headerLen4][header][body].
		private static byte[] GetBody(byte[] full)
		{
			if (full.Length < 12)
				return [];

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= full.Length
				? full[bodyStart..]
				: [];
		}

		// Walks the InteractArgs protobuf -> (typeId f1, mode f2, field3 f3, npc handle = field10.SpellTarget.f2).
		private static (uint TypeId, uint Mode, uint Field3, uint Npc) ParseArgs(ReadOnlySpan<byte> a)
		{
			uint typeId = 0, mode = 0, field3 = 0, npc = 0;
			var i = 0;
			while (i < a.Length)
			{
				var tag = a[i++];
				int fn = tag >> 3, wt = tag & 7;
				if (wt == 0)
				{
					var v = ReadVarint(a, ref i);
					if (fn == 1)
						typeId = (uint)v;
					else if (fn == 2)
						mode = (uint)v;
					else if (fn == 3)
						field3 = (uint)v;
				}
				else if (wt == 2)
				{
					var len = (int)ReadVarint(a, ref i);
					var sub = a.Slice(i, len);
					i += len;
					if (fn == 10) // SpellTarget { f1, f2 = npc handle }
					{
						var j = 0;
						while (j < sub.Length)
						{
							var t2 = sub[j++];
							int f2 = t2 >> 3, w2 = t2 & 7;
							if (w2 == 0)
							{
								var v2 = ReadVarint(sub, ref j);
								if (f2 == 2)
									npc = (uint)v2;
							}
							else
								break;
						}
					}
				}
				else
					break;
			}

			return (typeId, mode, field3, npc);
		}

		// The entity name is the first `0x0A <len 1..63> <printable-ascii>` run in the spawn body.
		private static string ReadSpawnName(ReadOnlySpan<byte> body)
		{
			for (var i = 16; i + 2 < body.Length; i++)
			{
				if (body[i] != 0x0A)
					continue;

				int len = body[i + 1];
				if (len is < 1 or > 63 || i + 2 + len > body.Length)
					continue;

				var ok = true;
				for (var j = 0; j < len; j++)
				{
					var c = body[i + 2 + j];
					if (c is < 0x20 or > 0x7E)
					{
						ok = false;
						break;
					}
				}

				if (ok)
					return Encoding.ASCII.GetString(body.Slice(i + 2, len));
			}

			return null;
		}

		private static ulong ReadVarint(ReadOnlySpan<byte> b, ref int i)
		{
			ulong r = 0;
			var shift = 0;
			while (true)
			{
				var x = b[i++];
				r |= (ulong)(x & 0x7f) << shift;
				if ((x & 0x80) == 0)
					return r;

				shift += 7;
			}
		}

		private static int VarintLength(uint value)
		{
			var n = 1;
			while (value >= 0x80)
			{
				value >>= 7;
				n++;
			}

			return n;
		}
	}
}
