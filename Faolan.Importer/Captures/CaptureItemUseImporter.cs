using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests a consumable's ON-USE spell (Items.OnUseSpell) from a use-a-consumable capture. Using an item emits, in
	// one frame, a self-ApplySpell (MagicBuffIIR 0x6B87E316 - the effect) immediately followed by a TemplateActionIIR
	// (0x6C300937 - the used item's template). So the on-use spell of a template = the BASE of the self-ApplySpell that
	// precedes its TemplateActionIIR. Verified in NecroL80_..._use-consumables: Daggamalt->Health Potion Effect,
	// Moonspill->Mana Potion Effect, etc. See docs/subsystems/item-claims.md (item-use) / GameServerListener.UseInventoryItem.
	public static class CaptureItemUseImporter
	{
		private const uint ApplySpellSubOpcode = MagicBuffCodec.Opcode;
		private const uint TemplateActionSubOpcode = TemplateActionCodec.Opcode;

		// (template -> on-use spell BASE) for one capture, in frame order (state resets per capture so a use in one
		// capture can't pair with a template in the next).
		public static Dictionary<uint, uint> Extract(IEnumerable<byte[]> packets)
		{
			var map = new Dictionary<uint, uint>();
			uint? pendingSpellBase = null;

			foreach (var bytes in packets)
			{
				if (!TryReadIirBody(bytes, out var sub, out var body))
					continue;

				if (sub == ApplySpellSubOpcode)
				{
					// [c350][applyTo][env][spellBaseId][form][c350][applyFrom][data]. This is the MagicBuffIIR content,
					// minus its leading subop (TryReadIirBody split it off) - re-prepend the subop so the codec parses
					// it, instead of re-deriving applyTo@8/spellId@14/applyFrom@22 by hand. Only
					// a SELF-buff full apply (applyTo == applyFrom, and a payload present - not the ~short rank-1 removal
					// twin) is a consumable effect.
					if (body.Length < 22)
						continue;

					using var content = new FaolanStream();
					content.WriteUInt32(sub).WriteArray(body);
					var cast = MagicBuffCodec.Parse(content.ToArray());
					if (cast.ApplyTo == cast.ApplyFrom && body.Length > 30)
						pendingSpellBase = cast.SpellBaseId;
				}
				else if (sub == TemplateActionSubOpcode && pendingSpellBase is { } spellBase)
				{
					if (TryReadTemplate(body, out var template) && template != 0)
						map[template] = spellBase;
					pendingSpellBase = null;
				}
			}

			return map;
		}

		// Sets Items.OnUseSpell for every harvested (template -> spell) whose item row exists. Returns (harvested, set).
		public static async Task<(int Harvested, int Set)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<byte[]>> captures, ILogger? logger = null)
		{
			var merged = new Dictionary<uint, uint>();
			foreach (var cap in captures)
			foreach (var (template, spell) in Extract(cap))
				merged[template] = spell;

			if (merged.Count == 0)
				return (0, 0);

			// Only set spells that EXIST in Spells - OnUseSpell is a FK, so an unknown spell would fail the insert.
			var spellIds = merged.Values.Distinct().ToList();
			var knownSpells = (await db.Spells.Where(s => spellIds.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();

			var ids = merged.Keys.ToList();
			var items = await db.Items.Where(i => ids.Contains(i.Id)).ToListAsync();
			var set = 0;
			foreach (var item in items)
			{
				var spell = merged[item.Id];
				if (knownSpells.Contains(spell) && item.OnUseSpell != spell)
				{
					item.OnUseSpell = spell;
					set++;
				}
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureItemUseImporter: {Harvested} on-use spells harvested, {Set} set on Items.", merged.Count, set);
			return (merged.Count, set);
		}

		// The 0x2000 IIR sub-opcode + the bytes after it, or false if not a 0x2000 IIR packet.
		private static bool TryReadIirBody(byte[] packetBytes, out uint sub, out byte[] body)
		{
			sub = 0;
			body = [];
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Opcode != (ushort)GameAgentOpcodes.PassBlob)
					return false;

				var payload = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
				sub = payload.ReadUInt32();
				body = payload.ReadRemainingArray();
				return true;
			}
			catch
			{
				return false;
			}
		}

		// The used item's template from a TemplateActionIIR body: it is emitted TWICE in a row (template + dup), so the
		// first u32 that equals the next u32 is it (robust to the exact header offset).
		private static bool TryReadTemplate(byte[] body, out uint template)
		{
			for (var i = 0; i + 8 <= body.Length; i++)
			{
				var a = body.ReadU32Be(i);
				if (a != 0 && a == body.ReadU32Be(i + 4))
				{
					template = a;
					return true;
				}
			}

			template = 0;
			return false;
		}
	}
}
