using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer
{
	// Harvests spell ids the character actually cast, from ApplySpell packets (0x2000 / 0x6B87E316; spell id at
	// inner[12..16]) and catalogs any not already in the Spell table (id-only; names/params are client-side / from
	// the archive `spelldata`). This is the thin capture-side spell signal - the full spellbook is class/level
	// derived and not in the pcaps (see docs/archive/faolan-importer-plan.md). Takes raw on-wire packet bytes.
	public static class CaptureSpellImporter
	{
		private const uint ApplySpellSubOpcode = 0x6B87E316;

		// The DispatchLearnSpell (s2c 0x200A) grant: sender = GameCharAgent, receiver = GameCharInterface (the
		// character), body = [u32 spellId] (#73). The class pair disambiguates the 0x200A opcode collisions on other
		// conversations (CharacterSearch etc.); the receiver-instance == character-id is the s2c-direction filter.
		private const ushort LearnSpellOpcode = 0x200A;
		private const uint GameCharAgentType = 0xA9ECB95D;
		private const uint GameCharInterfaceType = 0x8B87F791;

		public static async Task<(int Seen, int Inserted)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger logger = null)
		{
			var ids = new HashSet<uint>();
			foreach (var packet in rawPackets)
				TryReadSpellId(packet, ids);

			if (ids.Count == 0)
				return (0, 0);

			var idList = ids.ToList();
			var existing = (await db
				.Spells
				.Where(s => idList.Contains(s.Id))
				.Select(s => s.Id)
				.ToListAsync()).ToHashSet();

			var inserted = 0;
			foreach (var id in ids)
			{
				if (existing.Contains(id))
					continue;

				db.Spells.Add(new Spell
				{
					Id = id,
					Name = string.Empty
				});
				inserted++;
			}

			if (inserted > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureSpellImporter: {Seen} cast spell ids seen, {Inserted} new cataloged.", ids.Count, inserted);
			return (ids.Count, inserted);
		}

		// Seeds each captured character's LEARNED spell set (CharacterSpell rows) from the DispatchLearnSpell grants
		// in the captures, so GrantStartingSpells re-grants them on login (#74). Retail re-sends a caster's whole
		// learned set on every world entry, so a capture's grants ARE that character's spellbook. Attributed to the
		// character via the grant's RpcHeader receiver instance (= character id). Only characters already in the DB
		// are touched (CaptureCharacterImporter materializes them first); idempotent (skips spells already recorded).
		public static async Task<(int Characters, int Inserted)> ImportLearnedSpellsAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadLearnSpell(packet, byCharacter);

			if (byCharacter.Count == 0)
				return (0, 0);

			var charactersSeeded = 0;
			var inserted = 0;
			foreach (var (characterId, spellIds) in byCharacter)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				var existing = (await db
					.CharacterSpells
					.Where(s => s.CharacterId == characterId)
					.Select(s => s.SpellId)
					.ToListAsync()).ToHashSet();

				var added = false;
				foreach (var spellId in spellIds)
				{
					if (!existing.Add(spellId))
						continue;

					db.CharacterSpells.Add(new CharacterSpell
					{
						CharacterId = characterId,
						SpellId = spellId
					});
					inserted++;
					added = true;
				}

				if (added)
					charactersSeeded++;
			}

			if (inserted > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureSpellImporter: learned-spell sets for {Characters} characters, {Inserted} new CharacterSpell rows.", charactersSeeded, inserted);
			return (charactersSeeded, inserted);
		}

		// A DispatchLearnSpell grant: opcode 0x200A, GameCharAgent -> GameCharInterface, 4-byte [u32 spellId] body.
		// The c2s direction has the classes swapped, so the sender/receiver class pair selects only the s2c grant.
		private static void TryReadLearnSpell(byte[] packetBytes, IDictionary<uint, HashSet<uint>> byCharacter)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != LearnSpellOpcode || packet.Sender.Type != GameCharAgentType || packet.Receiver.Type != GameCharInterfaceType)
					return;

				var body = packet.Body.ReadRemainingArray();
				if (body.Length != 4)
					return;

				var spellId = BinaryPrimitives.ReadUInt32BigEndian(body);
				if (spellId == 0)
					return;

				var characterId = packet.Receiver.Instance;
				(byCharacter.TryGetValue(characterId, out var set) ? set : byCharacter[characterId] = new HashSet<uint>()).Add(spellId);
			}
			catch
			{
				// not a parseable DispatchLearnSpell grant
			}
		}

		private static void TryReadSpellId(byte[] packetBytes, ISet<uint> ids)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != 0x2000)
					return;

				var inner = packet.Body.ReadArrayUInt32Length();
				// inner: [subop4][c350 4][casterHandle4][flag byte][spellId4] -> spell id at offset 13.
				if (inner.Length < 17 || BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(0, 4)) != ApplySpellSubOpcode)
					return;

				var spellId = BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(13, 4));
				if (spellId != 0)
					ids.Add(spellId);
			}
			catch
			{
				// not a parseable ApplySpell packet
			}
		}
	}
}
