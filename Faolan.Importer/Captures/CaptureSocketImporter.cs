using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     The gems socketed into an existing character's items, harvested from the captured login snapshots.
	/// </summary>
	/// <remarks>
	///     The characters module is insert-only: it skips a character already in the DB, so a database that predates
	///     sockets can never pick them up from it, and re-importing the characters wholesale would delete locally created
	///     ones and every bit of play state with them. Sockets are additive per-item state, so they get their own pass -
	///     the same reason the bank has one.
	///     The match is exact, not heuristic. A character's DB id is its on-wire instance (CaptureCharacterImporter keeps
	///     the retail spawn handle as the id), and the snapshot is addressed by instance, so a snapshot resolves to one
	///     character by id. Within it, an entry resolves to one item by (container, slot), and the harvest only writes
	///     when the entry's template agrees with the item that is there - anything else means the character has been
	///     played since the capture and the cell now holds something else.
	/// </remarks>
	public static class CaptureSocketImporter
	{
		/// <summary>
		///     What one run did.
		/// </summary>
		/// <remarks>
		///     <c>SocketedSeen</c> vs <c>Matched</c> is the number that matters: a run that writes nothing looks identical
		///     to a run with nothing to write, and the difference is whether the captured cells still line up with the
		///     database. They stop lining up when the corpus is re-dumped after the characters were imported - the pass
		///     picks the largest snapshot per instance, and a re-dump can make a different one the largest, so the
		///     character's bag was built from one snapshot and is now being read against another. A bare count would
		///     report that as success.
		/// </remarks>
		public readonly record struct SocketImport(int Characters, int Sockets, int SocketedSeen, int Matched, IReadOnlyCollection<uint> UnresolvedGems);

		public static async Task<SocketImport> ImportAsync(DatabaseContext db, IEnumerable<CapturedPacket> packets)
		{
			// Instance -> snapshot bodies, pooled across captures. A truncated snapshot fails to parse, so every
			// candidate is kept and RootOf falls through them largest-first - the same way the character import does
			// it.
			var byInstance = new Dictionary<uint, List<byte[]>>();
			foreach (var packet in packets)
			{
				if (!packet.IsIirCarrier)
					continue;

				var body = packet.Body;
				if (body.Length < 16 || body.ReadU32Be(4) != FullPlayerCharacterCodec.Opcode)
					continue;

				var instance = body.ReadU32Be(12);
				if (!byInstance.TryGetValue(instance, out var list))
					byInstance[instance] = list = [];
				list.Add(body);
			}

			if (byInstance.Count == 0)
				return new SocketImport(0, 0, 0, 0, []);

			var knownTemplates = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();
			var unresolvedGems = new HashSet<uint>();
			var characters = 0;
			var sockets = 0;
			var socketedSeen = 0;
			var matched = 0;

			foreach (var (instance, bodies) in byInstance.OrderBy(kv => kv.Key))
			{
				if (!await db.Characters.AnyAsync(c => c.Id == instance))
					continue;

				if (RootOf(bodies) is not { } root)
					continue;

				var held = await db.ItemInstances.Include(i => i.Sockets).Where(i => i.OwnerCharacterId == instance).ToListAsync();
				var byCell = held.ToDictionary(i => (i.Container, i.Slot));
				var written = 0;

				foreach (var container in root.Inventories)
				{
					var kind = (ContainerKind)container.InventoryId.Type;
					foreach (var entry in container.Items)
					{
						if (!SnapshotRoot.SocketsOf(entry).Any())
							continue;

						socketedSeen++;
						if (!byCell.TryGetValue((kind, (int)entry.Pos), out var item) || item.TemplateId != (entry.Item?.ItemData?.Template1 ?? 0))
							continue;

						matched++;

						// The captured gem list is the whole truth about that item's sockets, so it replaces what is
						// there rather than merging: a merge could only leave a gem the capture says was removed.
						item.Sockets.Clear();
						foreach (var (socket, gem) in SnapshotRoot.SocketsOf(entry))
						{
							if (!knownTemplates.Contains(gem.Template1))
							{
								unresolvedGems.Add(gem.Template1);
								continue;
							}

							item.Sockets.Add(new ItemInstanceSocket
							{
								ItemInstanceId = item.Id,
								SocketIndex = socket,
								GemItemId = gem.Template1
							});
							written++;
						}
					}
				}

				if (written == 0)
					continue;

				characters++;
				sockets += written;
			}

			if (sockets > 0)
				await db.SaveChangesAsync();

			return new SocketImport(characters, sockets, socketedSeen, matched, unresolvedGems);
		}

		// The largest snapshot body that parses. Truncated ones throw out of SnapshotRoot.Of.
		private static ProtoFullCharacterIIR? RootOf(List<byte[]> bodies)
		{
			foreach (var body in bodies.OrderByDescending(b => b.Length))
			{
				try
				{
					// The body opens with the record's own u32 carrier length, which belongs to the sender - the record
					// itself starts behind it.
					return SnapshotRoot.Of(body.AsSpan(4));
				}
				catch
				{
					// truncated - try the next one
				}
			}

			return null;
		}

	}
}
