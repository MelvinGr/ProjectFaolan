using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// The SCRIPTED EVENTS an NPC's dialogue fires (#839). Retail sends a `GameActionListIIR` whose action type is
	// 53241 `SendScryEvent`, carrying a bare string the playfield's script listens for - `Fight` on Saddur (what turns
	// him on the player), `SpawnScavenger` and `RunToTortage` on Casilda.
	//
	// The corpus holds 17 of them across 7 distinct strings, so this is retail's general channel for "a conversation
	// made something happen in the world", not a one-off.
	public static class NpcScriptEventExtractor
	{
		// GameAction 53241 - `SendScryEvent`, whose first parameter is the EventString (GameActionTypes).
		private const uint SendScryEventType = 53241;

		// [u16 length][latin1 bytes] inside the action tail. The tail is otherwise the opaque property-map shape #190
		// describes, so the string is located by its own length prefix rather than by a fixed offset - which is what
		// keeps this working across the 36- and 82-byte tails the corpus carries.
		private const int MinEventLength = 3;
		private const int MaxEventLength = 64;

		public static List<(uint TemplateId, string Name, string Event)> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			ArgumentNullException.ThrowIfNull(packets);
			var namesByHandle = new Dictionary<uint, (uint Template, string Name)>();
			var found = new List<(uint, string, string)>();

			foreach (var p in packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true })
					continue;

				// Who each handle IS. The action names only a handle, and a handle is session-volatile, so the spawn
				// record is the only thing that can turn it into a creature TYPE (#835's lesson in the other direction).
				if (p.SubOpcode == SimpleCharFullUpdateCodec.Opcode)
				{
					if (p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn) && spawn.Character?.npc_stats?.TemplateId is { } template and > 0)
						namesByHandle[spawn.Handle] = (template, spawn.Character.Name ?? "");
					continue;
				}

				if (p.SubOpcode != GameActionListCodec.Opcode || !p.TryParse(GameActionListCodec.Parse, out var list))
					continue;
				if (list.DynelHandle != SendScryEventType)
					continue;
				if (!namesByHandle.TryGetValue(list.FrameHandle, out var who))
					continue; // the action fired on a handle this capture never spawned - unattributable, so dropped
				if (!TryReadEventString(list.ActionTail, out var name))
					continue;

				found.Add((who.Template, who.Name, name));
			}

			return found;
		}

		// The event string out of the action tail. Scans for a `[u16 len][len printable bytes]` pair rather than
		// trusting an offset: the tail's surrounding property map is undecoded (#190) and its length varies with the
		// action, so the self-describing part is the only reliable anchor.
		private static bool TryReadEventString(byte[] tail, out string value)
		{
			value = "";
			if (tail == null)
				return false;

			for (var i = 0; i + 2 <= tail.Length; i++)
			{
				var length = (tail[i] << 8) | tail[i + 1];
				if (length is < MinEventLength or > MaxEventLength || i + 2 + length > tail.Length)
					continue;

				var span = tail.AsSpan(i + 2, length);
				var printable = true;
				foreach (var c in span)
					if (c is < 0x20 or > 0x7e)
					{
						printable = false;
						break;
					}

				if (!printable)
					continue;

				value = Encoding.Latin1.GetString(span);
				return true;
			}

			return false;
		}
	}
}
