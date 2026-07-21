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
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// Ambient NPC chatter (per-NPC bark lines). The barks are chat-server CharacterChat (0x0022) local-says whose body
	// is [u32 senderHandle][u16 len][<remoteformat token="sayinchat(N)|conversationM(K)|<Group>" ...>]. The dialogue is
	// SPECIFIC to the NPC that says it, so it is keyed by the NPC's TEMPLATE (NpcStats.f4 / NpcTemplate.TemplateId), NOT by
	// name - several distinct templates share a display name (7 "Nobleman"/"Noblewoman" rows), and only the exact
	// template captured barking gets the lines; the others stay silent until captured.
	//
	// The shared tshark reader keeps only the Conan stream, so spawns come from ctx.Captures (via NpcSpawnExtractor,
	// which decodes the TemplateId out of the FA015807 CharacterIIR) while the ChatServer 0x0022 barks are read from
	// the matching *.tshark.json directly. Correlation: bark sender handle -> the spawn's TemplateId (same capture).
	public sealed class NpcChatterModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public string Description => "Ambient NPC chatter bark lines (chat-server sayinchat remoteformats), keyed by template.";
		public string Name => "npc-chatter";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// TemplateId -> the distinct verbatim <remoteformat> bark lines captured for that exact NPC template.
			var byTemplate = new Dictionary<uint, HashSet<string>>();

			foreach (var cap in ctx.Captures)
			{
				// handle -> TemplateId from the Conan FA015807 spawns (last spawn for a reused handle wins).
				var handleToTemplate = new Dictionary<uint, uint>();
				foreach (var spawn in NpcSpawnExtractor.Extract(cap.Packets))
				{
					if (spawn.TemplateId != 0)
						handleToTemplate[spawn.Handle] = spawn.TemplateId;
				}

				var file = Path.Combine(ctx.CapturesDir, cap.Name + ".tshark.json");
				if (!File.Exists(file))
					continue;

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

					foreach (var p in packets.EnumerateArray())
					{
						if (Str(p, "Protocol") != "ChatServer" || Str(p, "Opcode") != "0x0022" || Body(p) is not { } body || body.Length < 6)
							continue;

						var sender = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
						var len = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4, 2));
						if (6 + len > body.Length)
							continue;

						var text = Encoding.Latin1.GetString(body.AsSpan(6, len));
						if (!text.Contains("sayinchat(", StringComparison.Ordinal) || !handleToTemplate.TryGetValue(sender, out var template))
							continue;

						if (!byTemplate.TryGetValue(template, out var set))
							byTemplate[template] = set = new HashSet<string>();
						set.Add(text);
					}
				}
			}

			// Attach to the exact NpcTemplate type by TemplateId (unique - one NpcTemplate row per template).
			var npcByTemplate = await ctx.Db.NpcTemplates.ToDictionaryAsync(n => n.TemplateId, n => n);

			var lines = 0;
			var npcs = 0;
			foreach (var (template, texts) in byTemplate)
			{
				if (!npcByTemplate.TryGetValue(template, out var npc))
					continue;

				var existing = await ctx.Db.NpcChatter.Where(c => c.NpcTemplateId == npc.TemplateId).ToListAsync();
				ctx.Db.NpcChatter.RemoveRange(existing);
				foreach (var text in texts)
					ctx.Db.NpcChatter.Add(new NpcChatter
					{
						NpcTemplateId = npc.TemplateId,
						RemoteFormat = text
					});

				npcs++;
				lines += texts.Count;
			}

			await ctx.Db.SaveChangesAsync();
			return new ImportResult($"{npcs} NPC templates with chatter, {lines} bark lines.");
		}

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

		private static string Str(JsonElement p, string name)
		{
			return p.TryGetProperty(name, out var v)
				? v.GetString()
				: null;
		}
	}
}
