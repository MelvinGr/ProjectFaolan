using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.CreateFlow;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Proves the create-editor SCENE PROPS (the SimpleDynelFullUpdateIIR records the initial burst streams) are
	// reconstructible from the committed faolan.db WorldObjects (map 4000) via WorldObjectCodec - the same
	// DB-source path the world-entry login replay uses (issue #189 item 2). Each burst record is matched to its row
	// by IDENTITY (display_name - NOT position: two props overlap at one spot) and rebuilt with the burst's own
	// handle + the create session's playfield token; the rebuilt body must equal the captured record byte-for-byte.
	// This is the gate that lets the verbatim dynel records be driven from rows instead of replayed.
	public class CreateEditorSceneDynelTests
	{
		// The create-editor session's playfield instance (the MapChange token), carried in every scene record's
		// transform. The committed map-4000 rows were captured in a different session, so the build must thread this
		// token in (the rows' stored token differs); everything else reconstructs byte-exact.
		private const uint CreateSessionPlayfield = 0x00502276;

		[Fact]
		public void EveryBurstDynel_RebuildsFromDbRow_ByteExact()
		{
			var rowsByIdentity = LoadMap4000ObjectsByDisplayName();
			var records = ExtractBurstDynelRecords();
			Assert.Equal(18, records.Count);

			var rebuilt = 0;
			foreach (var (handle, body) in records)
			{
				var identity = ExtractDisplayName(body);
				// Every prop now has a DB row: the importer keys WorldObjects by (position, identity), so localized
				// 3072626 and 347659 - which share an exact spot - both survive the dedup (#222).
				Assert.True(rowsByIdentity.TryGetValue(identity, out var row), $"no DB row for handle 0x{handle:X8} (identity '{identity}')");

				var built = WorldObjectCodec.Build(row, handle, CreateSessionPlayfield);
				// The codec returns [u32 subOpcode][record] (its [u32 len] is the sender's, #530 rule 2); the burst's
				// Iir() helper writes both of those words itself, so the comparable record is built[4..].
				Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(built.AsSpan(4).ToArray()));
				rebuilt++;
			}

			Assert.Equal(18, rebuilt);
		}

		// End-to-end: running the actual initial burst WITH a DB (the committed faolan.db) emits exactly the golden
		// bytes - i.e. the live scene-prop loop (EditorSceneDynelHandles -> WorldObjectCodec build from the row keyed
		// by captured handle) reproduces InitialBurst.bin. The dynels are SPAWNED FROM the DB, not verbatim, so this
		// locks the wired DB path, not just WorldObjectCodec in isolation.
		[Fact]
		public async Task InitialBurst_WithDb_IsByteIdenticalToGolden()
		{
			var options = TestDb.ReadOnly(TestPaths.FaolanDb);
			var repo = new DatabaseRepository(new SingleOptionsContextFactory(options));
			var client = new RecordingNetworkClient(0x02000001); // the instance the golden was recorded with

			// The burst's GameTime resync anchor is generated from the wall clock (#351), so feed the instant this
			// capture was taken - the anchor's own low word as unix epoch seconds - to keep the burst byte-identical.
			await CreateFlowService.SendRetailCreateEditorInitialBurst(client, repo, DateTimeOffset.FromUnixTimeSeconds(0x6A1D3837));

			var bytes = client.Sent.SelectMany(p => p).ToArray();
			Assert.Equal(Convert.ToHexString(Golden.Bytes("InitialBurst")), Convert.ToHexString(bytes));
		}

		// The SimpleDynel record body in the burst: [u32 0x0000C878][u32 handle][u32 0] 2C 0A 0F 0D <posX> ... ;
		// WorldObjectCodec consumes/produces exactly this body (the bytes after the 0x2000 carrier prefix). Returns
		// (handle, body) for each of the burst's dynel records, lifted from the capture-locked InitialBurst.bin.
		private static List<(uint Handle, byte[] Body)> ExtractBurstDynelRecords()
		{
			var burst = Golden.Bytes("InitialBurst");
			var sig = new byte[] { 0x00, 0x00, 0xC8, 0x78 };
			var result = new List<(uint, byte[])>();
			for (var i = 0; i + 13 < burst.Length;)
			{
				var at = IndexOf(burst, sig, i);
				if (at < 0)
					break;

				i = at + 1;
				if (burst[at + 12] != 0x2C) // the transform group tag - confirms this is a SimpleDynel record body
					continue;

				// the carrier length [u32] sits 4 bytes before the subOpcode (8 bytes before the body): len = 4 + body
				var bodyLen = (int)burst.ReadU32Be(at - 8) - 4;
				var handle = burst.ReadU32Be(at + 4);
				result.Add((handle, burst.AsSpan(at, bodyLen).ToArray()));
			}

			return result;
		}

		// The ProtoSimpleDynelIIR display_name (field 3, `1A <len> <utf8>`) - the "<localized id=...>" string that
		// uniquely identifies the prop. Located via the codec (skips the header + transform), then read directly.
		// The burst record starts at the typeTag - its own Iir() helper owns the [u32 len][u32 subOpcode] pair - so
		// the sub-opcode is put back for the codec, which reads a whole record.
		private static string ExtractDisplayName(byte[] body)
		{
			byte[] record = [0x66, 0xAE, 0xDD, 0x50, .. body]; // SimpleDynelFullUpdateIIR
			Assert.True(WorldObjectColumns.TryReadIdentity(record, out var message));
			return message.DisplayName;
		}

		private static int IndexOf(byte[] haystack, byte[] needle, int start)
		{
			for (var i = start; i <= haystack.Length - needle.Length; i++)
			{
				var ok = true;
				for (var j = 0; j < needle.Length; j++)
				{
					if (haystack[i + j] != needle[j])
					{
						ok = false;
						break;
					}
				}

				if (ok)
					return i;
			}

			return -1;
		}

		private static Dictionary<string, WorldObject> LoadMap4000ObjectsByDisplayName()
		{
			var options = TestDb.ReadOnly(TestPaths.FaolanDb);
			using var ctx = new DatabaseContext(options);
			var map = new Dictionary<string, WorldObject>();
			foreach (var o in ctx.WorldObjects.Where(o => o.MapId == 4000 && o.DisplayKind != null).AsNoTracking())
				map.TryAdd(WorldObjectDisplay.ToWire(o)!, o);
			return map;
		}

		private sealed class SingleOptionsContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
