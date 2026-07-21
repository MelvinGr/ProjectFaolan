using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Importer.Extractors
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
	// QuestContextId: the quest that was ACTIVE (last QuestFullUpdate) when the player opened this conversation - the
	// player's quest state AT THE GREETING. Lets a single capture that talks to one NPC at MULTIPLE chain stages (e.g.
	// Casilda: offer pre-quest, then "free me" once you have the key) be split per-state, since the answer path alone
	// ("" greeting both times) can't distinguish them (#264). 0 = no quest active at the greeting.
	public sealed record ConversationEntry(
		uint TypeId,
		string AnswerPath,
		uint CapturedNpcHandle,
		uint CapturedInstance,
		List<byte[]> Packets,
		string NpcName,
		uint? NpcTemplateId = null,
		string ConditionScript = "",
		uint GrantQuestId = 0,
		uint QuestContextId = 0);

	// Reconstructs each NPC's dialogue as a TREE of screens keyed by answer-index PATH, from the captured request/
	// response flow. The grammar (memory protocol_conversation_routing): a conversation INSTANCE = TalkToNpc(f3=0)
	// [greeting] then the run of server responses + the player's KnubotAnswer(idx) picks, until the next greeting (a
	// re-walk). The greeting's responses are the "" screen; the responses after the player's first pick (idx i0) are
	// the "i0" screen; after the next pick they're "i0>i1"; etc. The branches are spread across the re-walks (each
	// Mode picked different options), so MERGING the instances by answer-path unions the full option tree.
	// `packets` must be one capture's GameServer packets in chronological order.
	public static class ConversationExtractor
	{
		private const uint InteractSubOpcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR;
		private const uint KnubotAnswerSubOpcode = (uint)GameServer0X2000Opcodes.KnubotAnswerIIR;
		private const uint Opcode2000 = 0x2000;
		private const uint Opcode2015 = 0x2015; // GameCharInterfaceOpcodes.DispatchSetDialogueCamera

		// A quest GRANT signal seen mid-conversation: the full journal record (0x63268258) the retail server sends when
		// the player accepts a quest. (QuestIIR-add 0x97E02314 also adds the quest but the full-update is the
		// unambiguous "here is the new quest" marker; the runtime guards against re-granting, so this is safe.)
		private const uint QuestFullUpdateSubOpcode = (uint)GameServer0X2000Opcodes.QuestFullUpdateIIR;

		private const uint SpawnSubOpcode = (uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR;
		private const uint TalkToNpcActionType = 0x16;

		// The Knubot conversation-display response sub-opcodes (642CD3D6 echo excluded: it is a generic interact ack):
		// OpenChatWindow (045B0B0F), AppendText (4A3D899A), AnswerList (F164A4A5), CloseChatWindow (C4AF39EB).
		private static readonly HashSet<uint> ResponseSubOpcodes =
		[
			(uint)GameServer0X2000Opcodes.KnubotAppendTextIIR, (uint)GameServer0X2000Opcodes.KnubotAnswerListIIR, (uint)GameServer0X2000Opcodes.KnubotOpenChatWindowIIR,
			(uint)GameServer0X2000Opcodes.KnubotCloseChatWindowIIR
		];

		public static List<ConversationEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var namesByHandle = BuildSpawnNameMap(packets);
			// handle -> NPC TEMPLATE id (#244): the runtime gates the conversation on the clicked NPC's template so a
			// same-named NPC (25 share "Halen van Dummy") can't open another's dialogue.
			var templatesByHandle = NpcSpawnExtractor.Extract(packets).Where(e => e.Handle != 0 && e.TemplateId != 0).GroupBy(e => e.Handle).ToDictionary(g => g.Key, g => g.First().TemplateId);
			var entries = new List<ConversationEntry>();

			// Per-conversation-instance state, reset at each greeting (TalkToNpc f3=0).
			var active = false;
			uint typeId = 0, npc = 0, instance = 0;
			var path = ""; // answer-index path of the CURRENT screen (the one whose responses we're collecting)
			var screen = new List<byte[]>();
			uint grant = 0; // a quest the server granted while collecting THIS screen's responses (0 = none)
			var questsSeen = new HashSet<uint>(); // every quest whose full-update appeared earlier in THIS capture
			uint currentQuest = 0; // the last quest whose full-update appeared (the active quest at any point in the capture)
			uint instanceContext = 0; // currentQuest AT the active instance's greeting - the player's state when they talked

			foreach (var p in packets)
			{
				if (p is { IsDownload: false, Opcode: Opcode2000, SubOpcode: InteractSubOpcode })
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
					grant = 0;
					instanceContext = currentQuest; // the quest active WHEN the player opened this conversation (#264)
				}
				else if (p is { IsDownload: false, Opcode: Opcode2000, SubOpcode: KnubotAnswerSubOpcode } && active)
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
					grant = 0;
				}
				else if (p is { IsDownload: true, Opcode: Opcode2000, SubOpcode: QuestFullUpdateSubOpcode })
				{
					// A quest full-update. body @16 = [00000000][01][u32 questId]. It's a GRANT (vs a login restore or a
					// re-display) only if: a conversation is active, the player has navigated past the greeting (path !=
					// ""), and this is the quest's FIRST appearance in the capture (a restore re-sends a quest first seen
					// at login). That pins the accept to the dialogue node the player walked. Always remember the quest so
					// later re-sends aren't mistaken for grants.
					var body = GetBody(p.Bytes);
					if (body.Length >= 25)
					{
						var questId = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(21, 4));
						currentQuest = questId; // track the active quest for the per-greeting QuestContextId (#264)
						if (active && path.Length != 0 && questsSeen.Add(questId))
							grant = questId;
						else
							questsSeen.Add(questId);
					}
				}
				else if (p.IsDownload && active && (p.Opcode == Opcode2015 || (p.Opcode == Opcode2000 && ResponseSubOpcodes.Contains(p.SubOpcode))))
					screen.Add(p.Bytes);
			}

			Flush();
			return entries;

			void Flush()
			{
				if (active && screen.Count > 0)
				{
					entries.Add(new ConversationEntry(typeId, path, npc, instance, [.. screen], namesByHandle.GetValueOrDefault(npc)!, templatesByHandle.TryGetValue(npc, out var tpl)
						? tpl
						: null, GrantQuestId: grant, QuestContextId: instanceContext));
				}
			}
		}

		// Every distinct NPC (by resolved spawn name) the player sent a GREETING TalkToNpc to (field3 == 0) in these
		// packets - the DENOMINATOR for conversation coverage. An NPC greeted here but absent from ConversationNodes is
		// a gap: a dump of the interaction exists, but no dialogue was decoded/imported for it (it fell to the "no
		// dialogue imported" bubble). Greetings whose handle isn't in the capture's spawn-name map resolve to null and
		// are dropped (not attributable to a named NPC).
		public static IReadOnlyCollection<string> GreetedNpcNames(IReadOnlyList<CapturedPacket> packets)
		{
			var namesByHandle = BuildSpawnNameMap(packets);
			var names = new HashSet<string>();
			foreach (var p in packets)
			{
				if (p is not { IsDownload: false, Opcode: Opcode2000, SubOpcode: InteractSubOpcode })
					continue;

				var body = GetBody(p.Bytes);
				if (body.Length < 26 || BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(18, 4)) != TalkToNpcActionType)
					continue;

				var argLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(22, 4));
				if (26 + argLen > body.Length)
					continue;

				var (_, _, field3, parsedNpc) = ParseArgs(body.AsSpan(26, argLen));
				if (field3 != 0)
					continue;

				if (namesByHandle.GetValueOrDefault(parsedNpc) is { Length: > 0 } name)
					names.Add(name);
			}

			return names;
		}

		// Merges screens from many captures/instances, deduped by (NpcName, TypeId, AnswerPath) - the stable branch
		// key. This is what UNIONS the option tree: each conversation re-walk (a different Mode) explored a different
		// option at a screen, so it contributes a different answer-path screen; merging them assembles the full tree.
		// Keyed by NPC NAME (stable), not the session-volatile handle. Prefers an entry whose captured player instance
		// is a 4-byte varint (re-addresses to any normal character), then the most packets.
		public static List<ConversationEntry> Merge(IEnumerable<ConversationEntry> all)
		{
			var best = new Dictionary<(string, uint, string, string), (int score, ConversationEntry entry)>();
			foreach (var e in all)
			{
				var identity = string.IsNullOrEmpty(e.NpcName)
					? $"#{e.CapturedNpcHandle}"
					: e.NpcName;
				// The ConditionScript is part of the key so a NPC's quest/tag-conditioned variants (e.g. Casilda's
				// pre- vs post-first-quest greeting) stay SEPARATE rows instead of merging into one mixed screen.
				var key = (identity, e.TypeId, e.AnswerPath, e.ConditionScript);
				var score = (VarintLength(e.CapturedInstance) == 4
					? 1_000_000
					: 0) + e.Packets.Count;
				if (!best.TryGetValue(key, out var cur) || score > cur.score)
				{
					// Preserve a grant seen in any capture of this screen even if the highest-scoring replay didn't see it.
					var grant = e.GrantQuestId != 0
						? e.GrantQuestId
						: cur.entry?.GrantQuestId ?? 0;
					best[key] = (score, e with
					{
						GrantQuestId = grant
					});
				}
				else if (e.GrantQuestId != 0 && cur.entry.GrantQuestId == 0)
				{
					best[key] = (cur.score, cur.entry with
					{
						GrantQuestId = e.GrantQuestId
					});
				}
			}

			return best.Values.Select(v => v.entry).ToList();
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

		// The TalkToNpc action's args message. This is a CommandMessage (the client-recovered BCC payload), NOT the
		// old hand-reconstructed "ProtoInteractIIR" - see Gitea #387. It used to be walked by a hand-written
		// tag/varint parser here, duplicating what the generated class does; the call sites already guarantee the
		// span is the args protobuf (they check the actionType and that the declared length fits), so a decode
		// failure is a real surprise and is left to throw rather than silently returning zeros.
		private static (uint BccActionId, uint CommandqueueClientId, uint CommandqueueServerId, uint NpcTemplate) ParseArgs(ReadOnlySpan<byte> a)
		{
			var args = a.FromProtobuf<CommandMessage>();

			// target is a ProtoID32 { type, instance }; the NPC handle is its instance. (The old model called this
			// field a "SpellTarget" and its first word a spell_id - it is an ENTITY CLASS, e.g. 0xC350.)
			return (args.BccActionId, args.CommandqueueClientId, args.CommandqueueServerId, args.Target?.Instance ?? 0);
		}

		// The entity name is the first `0x0A <len 1..63> <printable-ascii>` run in the spawn body.
		private static string? ReadSpawnName(ReadOnlySpan<byte> body)
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
