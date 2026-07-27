using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Text;
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
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

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

						var sender = body.ReadU32Be(0);
						var len = body.ReadU16Be(4);
						if (6 + len > body.Length)
							continue;

						var text = Encoding.Latin1.GetString(body.AsSpan(6, len));
						if (!text.Contains("sayinchat(", StringComparison.Ordinal) || !handleToTemplate.TryGetValue(sender, out var template))
							continue;

						if (!byTemplate.TryGetValue(template, out var set))
							byTemplate[template] = set = [];
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
				{
					// COLUMNIZED, not stored verbatim: a bark whose token/attributes we cannot lift is REJECTED rather
					// than kept as a blob, and the columns must rebuild the captured string byte-for-byte - the runtime
					// has no raw-text path to fall back to. Same rule as ConversationScreenColumns.ToRow.
					if (RemoteFormatXml.ParseChatter(text, out var parameterValue) is not { } row)
						throw new InvalidOperationException($"NPC {npc.TemplateId} bark is not a recognised chatter remoteformat: {text}");

					row.NpcTemplateId = npc.TemplateId;

					// Gate WITH the captured parameter value, then store WITHOUT it. The one bark that has a parameter
					// is the sendtell crier's `DetectedChar.name`, whose captured value is the capturing player's real
					// character name - session state the runtime regenerates, and which must never reach faolan.sql
					// (#417). Proving the round trip first means a bark we cannot reproduce still fails here.
					if (RemoteFormatXml.BuildChatter(row, parameterValue) != text)
						throw new InvalidOperationException($"NPC {npc.TemplateId} bark is not byte-exact round-trippable from columns: {text}");

					ctx.Db.NpcChatter.Add(row);
					lines++;
				}

				npcs++;
			}

			await ctx.Db.SaveChangesAsync();
			return new ImportResult($"{npcs} NPC templates with chatter, {lines} bark lines.");
		}

		private static byte[]? Body(JsonElement p)
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

		private static string? Str(JsonElement p, string name)
		{
			return p.TryGetProperty(name, out var v)
				? v.GetString()
				: null;
		}
	}
}
