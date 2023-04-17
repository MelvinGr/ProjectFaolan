using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Helpers;
using Faolan.PacketAnalyzer;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer
{
	// Rebuilds the database from the raw capture sources. The DB is the single source of truth (committed
	// faolan.db); this tool is the one way to (re)populate it from `Captures/` - no embedded data blobs in the
	// server. Imports item identities (inventory snapshots) and NPC conversations; maps/NPCs/spells follow (see
	// docs/faolan-importer-plan.md).
	// TODO: IMPORTER conversations should not delete anything.
	// TODO: show before and after stats at the end of import
	internal static class Program
	{
		private const string RepoRoot = "/home/melvin/Development/C#/Project Faolan";

		private static async Task<int> Main(string[] args)
		{
			var dbPath = GetOption(args, "--db") ?? Path.Combine(RepoRoot, "faolan.db");
			var capturesDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(RepoRoot, "Captures");
			var dryRun = args.Contains("--dry-run");

			Console.WriteLine($"Faolan.Importer: db={dbPath}  captures={capturesDir}  dryRun={dryRun}");

			if (!Directory.Exists(capturesDir))
			{
				Console.Error.WriteLine($"Captures directory not found: {capturesDir}");
				return 1;
			}

			if (!File.Exists(dbPath))
			{
				Console.Error.WriteLine($"Database not found: {dbPath} (the DB is the source of truth; point --db at it).");
				return 1;
			}

			// One ordered GameServer packet list per capture (conversations need per-capture sequence + metadata).
			var captures = ReadCaptures(capturesDir);
			var itemPackets = captures.SelectMany(c => c.Select(p => p.Bytes)).ToList();
			foreach (var bin in Directory.EnumerateFiles(capturesDir, "*.bin"))
				itemPackets.Add(File.ReadAllBytes(bin));

			var conversations = ConversationExtractor.Merge(captures.SelectMany(ConversationExtractor.Extract));

			Console.WriteLine($"Read {captures.Count} captures, {itemPackets.Count} item-candidate packets; extracted {conversations.Count} conversation nodes.");

			if (dryRun)
			{
				Console.WriteLine("[dry-run] no changes written.");
				return 0;
			}

			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={dbPath}", x => x.MigrationsAssembly("Faolan")).Options;
			await using var db = new DatabaseContext(options);
			await db.Database.MigrateAsync();

			var (total, inserted) = await CaptureItemImporter.ImportAsync(db, itemPackets);
			Console.WriteLine($"Items: upserted {total} ({inserted} new). DB now: {await db.Items.CountAsync()} items, {await db.Items.CountAsync(i => i.Archetype != 0)} buildable.");

			var newMaps = await CaptureMapImporter.ImportAsync(db, itemPackets);
			Console.WriteLine($"Maps: {newMaps} new inserted. DB now: {await db.Maps.CountAsync()} maps.");

			await ImportConversations(db, conversations);
			Console.WriteLine($"Conversations: {await db.ConversationNodes.CountAsync()} nodes, {await db.ConversationPackets.CountAsync()} packets.");

			return 0;
		}

		// Full replace: conversation nodes are entirely derived from captures, so rebuild them every run.
		private static async Task ImportConversations(DatabaseContext db, List<ConversationEntry> conversations)
		{
			await db.ConversationPackets.ExecuteDeleteAsync();
			await db.ConversationNodes.ExecuteDeleteAsync();

			foreach (var e in conversations)
			{
				db.ConversationNodes.Add(new ConversationNode
				{
					TypeId = e.TypeId,
					Mode = e.Mode,
					Field3 = e.Field3,
					CapturedNpcHandle = e.CapturedNpcHandle,
					CapturedInstance = e.CapturedInstance,
					Packets = e.Packets.Select((p, i) => new ConversationPacket { Sequence = i, Data = p }).ToList()
				});
			}

			await db.SaveChangesAsync();
		}

		private static List<List<CapturedPacket>> ReadCaptures(string capturesDir)
		{
			var captures = new List<List<CapturedPacket>>();

			foreach (var json in Directory.EnumerateFiles(capturesDir, "*.json"))
				captures.Add(ReadJsonCapture(json));

			var analysis = new PacketAnalysisService();
			foreach (var pcap in Directory.EnumerateFiles(capturesDir, "*.pcapng"))
			{
				if (File.Exists(Path.ChangeExtension(pcap, ".json")))
					continue; // covered by its dump

				try
				{
					captures.Add(ReadPcapCapture(analysis, pcap));
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"  skip {Path.GetFileName(pcap)}: {ex.Message}");
				}
			}

			return captures;
		}

		private static List<CapturedPacket> ReadPcapCapture(PacketAnalysisService analysis, string pcap)
		{
			var list = new List<CapturedPacket>();
			var tick = 0L;
			foreach (var p in analysis.AnalyzeCapture(pcap).Packets)
			{
				if (p.ProtocolName != "GameServer")
					continue;

				list.Add(new CapturedPacket(tick++, p.IsDownload, p.OpcodeValue, p.SubOpcodeValue ?? 0, p.Bytes));
			}

			return list; // analyzer order is chronological
		}

		private static List<CapturedPacket> ReadJsonCapture(string jsonPath)
		{
			var list = new List<CapturedPacket>();

			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
			}
			catch
			{
				return list;
			}

			using (doc)
			{
				if (!doc.RootElement.TryGetProperty("Packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
					return list;

				foreach (var p in packets.EnumerateArray())
				{
					if (p.TryGetProperty("ProtocolName", out var proto) && proto.GetString() != "GameServer")
						continue;
					if (!p.TryGetProperty("RawBytesBase64", out var raw) || raw.ValueKind != JsonValueKind.String)
						continue;

					byte[] bytes;
					try
					{
						bytes = Convert.FromBase64String(raw.GetString()!);
					}
					catch
					{
						continue;
					}

					var ticks = p.TryGetProperty("TimestampTicks", out var t) && t.TryGetInt64(out var tv) ? tv : 0L;
					var isDownload = p.TryGetProperty("IsDownload", out var d) && d.ValueKind == JsonValueKind.True;
					var opcode = GetNestedValue(p, "Opcode");
					var subOpcode = GetNestedValue(p, "SubOpcode");

					list.Add(new CapturedPacket(ticks, isDownload, opcode, subOpcode, bytes));
				}
			}

			list.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
			return list;
		}

		private static uint GetNestedValue(JsonElement packet, string property)
		{
			return packet.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("Value", out var v) && v.TryGetUInt32(out var value)
				? value
				: 0;
		}

		private static string? GetOption(string[] args, string name)
		{
			var i = Array.IndexOf(args, name);
			return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
		}
	}
}
