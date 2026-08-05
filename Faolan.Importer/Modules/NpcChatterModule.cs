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

			// The client's own line catalog, when an RDB is available. Without one the module runs exactly as before -
			// captures only - so a corpus-only import is unchanged rather than silently thinner.
			var catalog = string.IsNullOrWhiteSpace(ctx.RdbDir)
				? null
				: BarkCatalog.Load(ctx.RdbDir);

			var lines = 0;
			var harvested = 0;
			var npcs = 0;
			var captured = new List<(uint TemplateId, Dictionary<(string Channel, uint SayInChat), Core.Database.Tables.NpcChatter> Rows)>();
			foreach (var (template, texts) in byTemplate)
			{
				if (!npcByTemplate.TryGetValue(template, out var npc))
					continue;

				// Load the parameter CHILDREN too, and delete them explicitly. Removing a parent EF loaded without its
				// children leaves EF unaware of them, so it cannot order their delete ahead of the new rows' insert -
				// which collided on the child key the moment the slots became rows (#493).
				var existing = await ctx.Db.NpcChatter.Include(c => c.Parameters).Where(c => c.NpcTemplateId == npc.TemplateId).ToListAsync();
				ctx.Db.NpcChatterParameter.RemoveRange(existing.SelectMany(c => c.Parameters));
				ctx.Db.NpcChatter.RemoveRange(existing);

				// Deduped by the bark's KEY, not by its captured text. Two sightings of one bark differ in the text
				// whenever it names the addressed player - `value="7;NecroL80"` vs `value="7;Sonja"` are two strings and
				// one line - so a text-keyed set yields two rows on the same (template, channel, sayinchat) key. That
				// was latent while a bark carried at most one name-only slot; it surfaced as a duplicate-key insert on
				// the parameter child table as soon as the slots became rows (#493).
				var byKey = new Dictionary<(string Channel, uint SayInChat), Core.Database.Tables.NpcChatter>();
				foreach (var text in texts)
				{
					// COLUMNIZED, not stored verbatim: a bark whose token/attributes we cannot lift is REJECTED rather
					// than kept as a blob, and the columns must rebuild the captured string byte-for-byte - the runtime
					// has no raw-text path to fall back to. Same rule as ConversationScreenCodec.Parse.
					// NpcChatterCodec.Parse THROWS on a non-bark (#530 rule 4); re-thrown here naming the template.
					NpcChatterBark bark;
					try
					{
						bark = NpcChatterCodec.Parse(text);
					}
					catch (CodecParseException e)
					{
						throw new InvalidOperationException($"NPC {npc.TemplateId} bark is not a recognised chatter remoteformat: {text}", e);
					}

					var parameterValue = NpcChatterColumns.SessionValueOf(bark);
					var row = NpcChatterColumns.ToRow(bark);

					row.NpcTemplateId = npc.TemplateId;

					// Gate WITH the captured parameter value, then store WITHOUT it. The one bark that has a parameter
					// is the sendtell crier's `DetectedChar.name`, whose captured value is the capturing player's real
					// character name - session state the runtime regenerates, and which must never reach faolan.sql
					// (#417). Proving the round trip first means a bark we cannot reproduce still fails here.
					if (NpcChatterCodec.Build(row, parameterValue) != text)
						throw new InvalidOperationException($"NPC {npc.TemplateId} bark is not byte-exact round-trippable from columns: {text}");

					// First sighting wins. They describe the same line, and the differing part is exactly the session
					// value that is not stored anyway - so there is nothing to choose between them.
					if (byKey.TryAdd((row.Channel, row.SayInChat), row))
						lines++;
				}

				foreach (var row in byKey.Values)
					ctx.Db.NpcChatter.Add(row);

				captured.Add((npc.TemplateId, byKey));
				npcs++;
			}

			// HARVEST the rest of each captured group's conversation from the client's own LDB (#331). A capture only
			// ever catches the lines an NPC happened to say while someone was recording - five of Lord Ovidius's eleven
			// - and the client ships the whole set, in PLAINTEXT, at an id computable from the token we already hold.
			// So this fills in the siblings of every (channel, group) a capture has proved, for the same template.
			//
			// ⚠️ ONLY for a group with a SINGLE speaker. A bark group is often a two-hander: `RenownBystanderLovers`
			// is one conversation shared by the Nobleman and the Noblewoman, and the client's line set interleaves
			// them (`2` is Lord Ovidius on his sons, `3` is her answering "That's just dreadful, Lord Ovidius"). The
			// captures assign 3/8/16/23 to her and 6/19 to him, so the split is real - and it is NOT mechanically
			// recoverable: strict alternation from those anchors reproduces every line except the last, which is hers
			// and would be given to him. Harvesting a shared group would therefore put her lines in his mouth, which
			// is worse than the silence it replaces.
			//
			// ⚠️ The test is "one captured speaker", which is weaker than "one real speaker" and is the residual
			// assumption here: `RenownBystanderWomen` is plainly a two-hander too, but both its voices are noblewomen
			// and only one template was ever captured saying it, so its lines all land on that template. That is the
			// right call while it holds - a noblewoman saying a noblewoman's line is right either way - and it breaks
			// the moment a capture shows a second template in the group, which is exactly when this rule stops
			// harvesting it.
			//
			// Slot-free lines only: one carrying `{TopGloryName}` or `{DetectedChar.name}` needs a matching
			// <parameter> filled from live server state, and adding the row without it would put an unrenderable bark
			// in an NPC's mouth.
			//
			// The row's FormatKey stays EMPTY, and that is correct rather than a gap: the key is only read when a
			// string is marked encrypted, and this category is not (see BarkCatalog). A harvested line needs no key.
			if (catalog is { IsPlaintext: true })
			{
				var speakers = captured
					.SelectMany(c => c.Rows.Values.Select(r => (r.Group, r.Channel, r.ChannelCount, c.TemplateId)))
					.GroupBy(x => (x.Group, x.Channel, x.ChannelCount))
					.ToDictionary(g => g.Key, g => g.Select(x => x.TemplateId).Distinct().Count());

				foreach (var (templateId, byKey) in captured)
					foreach (var suffix in byKey.Values.Select(r => (r.Channel, r.ChannelCount, r.Group)).Distinct().ToList())
					{
						if (speakers.GetValueOrDefault((suffix.Group, suffix.Channel, suffix.ChannelCount)) != 1)
							continue; // a two-hander - see above

						foreach (var (sayInChat, text) in catalog.Lines(suffix.Channel, suffix.ChannelCount, suffix.Group))
						{
							if (BarkCatalog.HasSlot(text) || byKey.ContainsKey((suffix.Channel, sayInChat)))
								continue;

							ctx.Db.NpcChatter.Add(new Core.Database.Tables.NpcChatter
							{
								NpcTemplateId = templateId,
								Channel = suffix.Channel,
								ChannelCount = suffix.ChannelCount,
								Group = suffix.Group,
								SayInChat = sayInChat,
								Category = BarkCatalog.Category,
								Knubot = 0
							});
							harvested++;
						}
					}
			}

			await ctx.Db.SaveChangesAsync();
			return new ImportResult($"{npcs} NPC templates with chatter, {lines} captured bark line(s) + {harvested} harvested from the client LDB.");
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
