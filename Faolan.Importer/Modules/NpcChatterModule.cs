using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Cli;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// Ambient NPC chatter (per-NPC bark lines). The barks are chat-server CharacterChat (0x0022) local-says whose body
	// is [u32 senderHandle][u16 len][<remoteformat token="sayinchat(N)|conversationM(K)|<Group>" ...>]. The shared
	// tshark reader keeps only the Conan stream, so this module reads the *.tshark.json directly: it correlates the
	// bark's sender handle -> the NPC name (from the Conan FA015807 spawns) -> the Npc TYPE, and stores each distinct
	// remoteformat verbatim under NpcChatter. The dialogue is SPECIFIC per NPC, so it is keyed per type.
	public sealed class NpcChatterModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public string Description => "Ambient NPC chatter bark lines (chat-server sayinchat remoteformats).";
		public string Name => "npc-chatter";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// name -> the distinct verbatim <remoteformat> bark lines seen for it.
			var byName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

			foreach (var file in Directory.EnumerateFiles(ctx.CapturesDir, "*.tshark.json"))
			{
				JsonDocument doc;
				try
				{
					doc = JsonDocument.Parse(File.ReadAllText(file));
				}
				catch
				{
					continue;
				}

				using (doc)
				{
					if (!doc.RootElement.TryGetProperty("Packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
						continue;

					// Pass 1: handle -> NPC name from the Conan FA015807 spawns.
					var names = new Dictionary<uint, string>();
					foreach (var p in packets.EnumerateArray())
					{
						if (!IsDownload(p) || Body(p) is not { } body || body.Length < 16)
							continue;
						if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) != 0xFA015807u)
							continue;
						var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
						if (ReadSpawnName(body) is { } name)
							names[handle] = name;
					}

					// Pass 2: the chat-server sayinchat barks -> attribute to the sender's NPC name.
					foreach (var p in packets.EnumerateArray())
					{
						if (Proto(p) != "AgentServer" || Opcode(p) != "0x0022" || Body(p) is not { } body || body.Length < 6)
							continue;
						var sender = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
						var len = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4, 2));
						if (6 + len > body.Length)
							continue;
						var text = Encoding.Latin1.GetString(body.AsSpan(6, len));
						if (!text.Contains("sayinchat(", StringComparison.Ordinal) || !names.TryGetValue(sender, out var name))
							continue;
						if (!byName.TryGetValue(name, out var set))
							byName[name] = set = new HashSet<string>();
						set.Add(text);
					}
				}
			}

			// Attach to each NPC type by name. Overwrite the type's chatter set wholesale (idempotent rebuild).
			var npcsByName = (await ctx.Db.Npcs.ToListAsync())
				.Where(n => n.Name != null)
				.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

			var lines = 0;
			var npcs = 0;
			foreach (var (name, texts) in byName)
			{
				if (!npcsByName.TryGetValue(name, out var npc))
					continue;

				var existing = await ctx.Db.NpcChatter.Where(c => c.NpcId == npc.Id).ToListAsync();
				ctx.Db.NpcChatter.RemoveRange(existing);
				foreach (var text in texts)
					ctx.Db.NpcChatter.Add(new NpcChatter { NpcId = npc.Id, RemoteFormat = text });

				npcs++;
				lines += texts.Count;
			}

			await ctx.Db.SaveChangesAsync();
			return new ImportResult($"{npcs} NPC types with chatter, {lines} bark lines.");
		}

		private static bool IsDownload(JsonElement p) => p.TryGetProperty("IsDownload", out var d) && d.ValueKind == JsonValueKind.True;
		private static string Proto(JsonElement p) => p.TryGetProperty("Protocol", out var v) ? v.GetString() : null;
		private static string Opcode(JsonElement p) => p.TryGetProperty("Opcode", out var v) ? v.GetString() : null;

		private static byte[] Body(JsonElement p)
		{
			if (!p.TryGetProperty("BodyHex", out var bh) || bh.ValueKind != JsonValueKind.String)
				return null;
			var s = bh.GetString();
			if (string.IsNullOrEmpty(s) || (s.Length & 1) != 0)
				return null;
			try
			{
				return Convert.FromHexString(s);
			}
			catch
			{
				return null;
			}
		}

		// The NPC name embedded in an FA015807 spawn body: the first `0x0A [u8 len 1..63] [ascii]` from offset 16.
		private static string ReadSpawnName(byte[] body)
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
					if (body[i + 2 + j] is < 0x20 or > 0x7E)
					{
						ok = false;
						break;
					}

				if (ok)
					return Encoding.ASCII.GetString(body, i + 2, len);
			}

			return null;
		}
	}
}
