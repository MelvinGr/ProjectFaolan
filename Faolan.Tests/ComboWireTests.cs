using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The melee-combo WIRE claims (#139), gated against the captured swings in `assets/combo-swings.bin` - every
	// `Combat:*` BehaviorControlCenter record in the corpus whose CommandMessage args carry a `combo_id`, whole and
	// in capture order (`--report combo-swings --report-arg <file>` writes it).
	//
	// The claim being locked is that the combo is STATED on the wire, not inferred: `combo_id` (field 7) is the
	// `Combos` catalog id verbatim and `combo_attack_num` (field 8) is the step within it. That had been read as a
	// raw byte run instead - "[08][animByte][2B marker][u32 counter][u32 cid = 0x300038XX]" - which is the protobuf
	// misparsed ("0x300038XX" is `[30 00]` execution_status then the `[38 ...]` combo_id tag), and which left the
	// standing conclusion that combos could not be recovered from packets without client RE. They can; the client
	// says which combo and which step, every swing.
	public class ComboWireTests
	{
		private const string Fixture = "combo-swings.bin";

		// The whole point: every combo id the client ever put on the wire is a row we ship. A single unresolved id
		// would mean either the catalog import is short or the field is being read out of the wrong place.
		[Fact]
		public async Task CapturedComboSwings_AllResolveToTheCatalog()
		{
			var swings = Swings();
			Assert.True(swings.Count >= 840, $"only {swings.Count} combo swings in the fixture - it looks regenerated against a partial corpus");

			var catalog = await Catalog();
			var unknown = swings.Select(s => s.Args.ComboId).Distinct().Where(id => !catalog.ContainsKey(id)).ToList();

			Assert.Empty(unknown);
			Assert.True(swings.Select(s => s.Args.ComboId).Distinct().Count() >= 60, "fewer distinct combos than the corpus carries");
		}

		// `combo_attack_num` is the STEP INDEX, and this is what proves it: over every captured swing of a combo
		// that HAS a key sequence, the value stays inside that sequence - it never once runs past the last step.
		// A field that merely correlated with the step would not be bounded by the catalog's own step count.
		[Fact]
		public async Task ComboAttackNum_StaysInsideTheCatalogStepSequence()
		{
			var catalog = await Catalog();
			var stepped = 0;

			foreach (var swing in Swings())
			{
				var combo = catalog[swing.Args.ComboId];
				if (!ComboActionParser.TryParse(combo.Actions, out var sequence))
				{
					// A combo with no sequence carries step 0 or the unstepped sentinel, and nothing else.
					Assert.True(swing.Args.ComboAttackNum is 0 or ComboService.UnsteppedAttackNum,
						$"{combo.Name} has no key sequence but carries combo_attack_num {swing.Args.ComboAttackNum}");
					continue;
				}

				stepped++;
				Assert.True(swing.Args.ComboAttackNum < (uint)sequence.Count,
					$"{combo.Name} [{combo.Actions}] has {sequence.Count} steps but a swing carries combo_attack_num {swing.Args.ComboAttackNum}");
			}

			Assert.True(stepped >= 300, $"only {stepped} swings of a stepped combo - the fixture looks short");
		}

		// The NPC-combo set. All 69 `action`-marker rows are named `combo_npc_*`, and the wire agrees from the other
		// side: those ids arrive ONLY as a server-to-client record, never as a client request. So `action` is not a
		// placeholder for "an input the catalog does not model" - it is "no input at all, because the AI drives it".
		[Fact]
		public async Task NpcComboRows_AreTheActionMarkerSet()
		{
			var catalog = await Catalog();
			var marked = catalog.Values.Where(c => ComboActionParser.TryParseMarker(c.Actions, out var t) && t == ComboTrigger.NpcCombo).ToList();

			Assert.Equal(69, marked.Count);
			Assert.All(marked, c => Assert.StartsWith("combo_npc_", c.Name));

			var npcIds = marked.Select(c => c.Id).ToHashSet();
			var requested = Swings().Where(s => !s.IsDownload && npcIds.Contains(s.Args.ComboId)).ToList();
			Assert.Empty(requested);

			// And they really are exercised - otherwise "never requested" would be vacuous.
			Assert.True(Swings().Count(s => npcIds.Contains(s.Args.ComboId)) >= 400, "the fixture carries no NPC combo swings");
		}

		// A combo's payload is a SPELL: `Combo.GameActionId` resolves into the `Spells` catalog for the overwhelming
		// majority of the shipped rows, which is what lets the finisher run the ordinary cast path (damage + effect)
		// instead of a bespoke combo-damage model. Locked as a ratchet so an import that stopped resolving them
		// fails here rather than silently making every finisher a no-op.
		[Fact]
		public async Task ComboGameActionIds_ResolveIntoTheSpellCatalog()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var spells = await db.Spells.Select(s => s.Id).ToHashSetAsync(ct);
			var combos = await db.Combos.ToListAsync(ct);
			var resolved = combos.Count(c => spells.Contains(c.GameActionId));

			Assert.True(resolved >= 726, $"only {resolved} of {combos.Count} combos name a spell - the catalogs have drifted apart");
		}

		// Every captured swing that lands on a step of a directional combo, grouped by (class/weapon family,
		// direction), gives ONE `bcc_action_id` per cell in the families the corpus covers well - so the animation
		// id the old read called "animByte" is the direction after all, just per class+weapon rather than universal.
		// The exception is the session's FIRST swing of a family, which uses a distinct draw-and-strike animation;
		// those are the 96xxx / 200xxx ids, so the assertion is on the dominant id per cell, not on uniqueness.
		[Fact]
		public async Task BccActionId_IsTheDirection_PerClassAndWeapon()
		{
			var catalog = await Catalog();
			var cells = new Dictionary<(string Family, ComboInput Direction), List<uint>>();

			foreach (var swing in Swings())
			{
				var combo = catalog[swing.Args.ComboId];
				if (!ComboActionParser.TryParse(combo.Actions, out var sequence) || swing.Args.ComboAttackNum >= (uint)sequence.Count)
					continue;

				var key = (combo.Name.Split('_')[0], sequence[(int)swing.Args.ComboAttackNum]);
				if (!cells.TryGetValue(key, out var ids))
					cells[key] = ids = [];

				ids.Add(swing.Args.BccActionId);
			}

			// Within one family, two different directions must never share their dominant animation id - that is
			// what "the animation names the direction" means, and it is the half that could fail.
			foreach (var family in cells.GroupBy(c => c.Key.Family))
			{
				var dominant = family.ToDictionary(c => c.Key.Direction, c => c.Value.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key);
				Assert.Equal(dominant.Count, dominant.Values.Distinct().Count());
			}

			Assert.Contains(("Assassin", ComboInput.UpperLeft), cells.Keys);
			Assert.Contains(("Herald", ComboInput.Up), cells.Keys);
		}

		private static async Task<Dictionary<uint, Combo>> Catalog()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			return await db.Combos.ToDictionaryAsync(c => c.Id, c => c, TestContext.Current.CancellationToken);
		}

		private static List<Swing> Swings()
		{
			var swings = new List<Swing>();
			foreach (var packet in CapturedFixture.Packets(Fixture))
			{
				var bcc = BehaviorControlCenterCodec.Parse(CapturedRecord(packet.Full));
				var args = bcc.Args.FromProtobuf<CommandMessage>();
				Assert.True(args.ShouldSerializeComboId(), "the fixture must hold only swings that carry a combo id");

				// The direction is not in the record, so it comes from the fixture's own capture order: the client
				// REQUESTS on phase 0x02/0x03 and the server answers on 0x04/0x07/0x0A/0x0F.
				swings.Add(new Swing(bcc.Actor, (byte)bcc.Seq is not (0x02 or 0x03), args));
			}

			return swings;
		}

		// The IIR record inside a full captured packet: [len4][crc4][headerLen4][header][u32 bodyLen][record].
		private static byte[] CapturedRecord(byte[] full)
		{
			var headerLength = (int)full.ReadU32Be(8);
			return full[(12 + headerLength + 4)..];
		}

		private sealed record Swing(uint Actor, bool IsDownload, CommandMessage Args);
	}
}
