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
	/// <summary>
	///     Harvests a consumable's on-use spell (Items.OnUseSpell) from a use-a-consumable capture.
	/// </summary>
	/// <remarks>
	///     Using an item emits, in one frame, a self-ApplySpell (MagicBuffIIR - the effect) immediately followed by a
	///     TemplateActionIIR (the used item's template). So the on-use spell of a template is the base of the
	///     self-ApplySpell that precedes its TemplateActionIIR (e.g. Daggamalt -> Health Potion Effect in the
	///     NecroL80 use-consumables capture). See docs/subsystems/item-claims.md (item-use) and ItemUseService.
	/// </remarks>
	public static class CaptureItemUseImporter
	{
		private const uint ApplySpellSubOpcode = MagicBuffCodec.Opcode;
		private const uint TemplateActionSubOpcode = TemplateActionCodec.Opcode;

		/// <summary>
		///     (template -> on-use spell base) for one capture, in frame order. State resets per capture so a use in one
		///     capture cannot pair with a template in the next.
		/// </summary>
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
					// The MagicBuffIIR content minus its leading subop (TryReadIirBody split it off) - re-prepend the
					// subop so the codec parses it. Only a self-buff full apply (applyTo == applyFrom, and a payload
					// present - not the short removal twin) is a consumable effect.
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

		/// <summary>
		///     Sets Items.OnUseSpell for every harvested (template -> spell) whose item row exists.
		/// </summary>
		public static async Task<(int Harvested, int Set)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<byte[]>> captures, ILogger? logger = null)
		{
			var merged = new Dictionary<uint, uint>();
			foreach (var cap in captures)
			foreach (var (template, spell) in Extract(cap))
				merged[template] = spell;

			if (merged.Count == 0)
				return (0, 0);

			// Only set spells that exist in Spells - OnUseSpell is a FK, so an unknown spell would fail the insert.
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

		// The PassBlob IIR sub-opcode and the bytes after it, or false if not a PassBlob IIR packet.
		private static bool TryReadIirBody(byte[] packetBytes, out uint sub, out byte[] body)
		{
			sub = 0;
			body = [];
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Method != (uint)GameAgentMethods.PassBlob)
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

		// The used item's template from a TemplateActionIIR body: it is emitted twice in a row (template + dup), so the
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
