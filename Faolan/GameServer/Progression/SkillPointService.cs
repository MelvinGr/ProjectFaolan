using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Progression
{
	// Skill-point spends. A character earns a pool of points (`SkillPoints` 0x0F3, the "skill points to distribute" the
	// level-up hands out) and moves them into the four trainable skills. The client sends the whole transfer as one
	// `SkillIIR` (0xD558FF16): a (statId, delta) list debiting the pool and crediting the skill, so the deltas sum to
	// zero. See SkillCodec for the grammar.
	//
	// The answer retail gives is a pair of ordinary `StatIIR` records - the skill at its new total, then the pool at
	// its new total - which is why nothing here builds a bespoke reply message:
	//
	//   f98e10b3 0000c350 <handle> 01 0000043d 00000001   <- Runspeed = 1
	//   f98e10b3 0000c350 <handle> 01 000000f3 00000763   <- SkillPoints = 1891
	//
	// The client then mirrors both records straight back (c2s StatIIR, frames 958/959), which the inbound StatIIR case
	// drops on the floor - it is an echo, not a request.
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_3 frames 953-959 / 1040-1044 / 1121-1124 / 1179-1183]
	//
	// Sellback is deliberately not implemented. The client has a `DoSkillSellback(int i_eStat)` in its method table and
	// the transfer grammar would express a refund perfectly well (pool credit, skill debit), but there is no sellback
	// traffic in the corpus and the signature carries no amount, so how much a sellback returns is unrecoverable.
	// Accepting the reverse direction on trust would let a client cash out the base value every character starts with
	// (Perception 560 on the captured necromancer) as 560 free points, so it is refused rather than guessed.
	public sealed class SkillPointService
	{
		// The pool every spend draws from.
		private const CharacterStatId Pool = CharacterStatId.SkillPoints;

		// Stats whose value also lives on the n3 dynel layer, and which retail therefore restates with the base byte
		// clear (`env` 0x00) before the ordinary gamecode record. Sweeping every captured character `StatIIR` shows
		// env=0 is a property of the stat rather than of the moment: it is confined to 22 ids out of 53, `Runspeed`
		// (3 records) among them, while `Climbing`, `Hiding` and `Perception` never carry one. So the extra record on
		// the Runspeed spend is not "the first point in a skill" - it is Runspeed being a movement stat.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_3.tshark.json frame 956 carries env=0 + env=1 for 0x43D; frames 1041/1122/1180 carry env=1 only]
		private static readonly HashSet<CharacterStatId> DynelMirrored = [CharacterStatId.RunSpeed];

		private readonly ICharacterStatRepository _database;
		private readonly ILogger _logger;

		public SkillPointService(ICharacterStatRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// c2s SkillIIR: move points out of the pool and into one or more skills, then restate both on the wire.
		// Refusals are silent by design: retail's client gates the spend itself, because it knows the pool, so a
		// request that fails here is a client that already disagrees with the server, and inventing a rejection message
		// would put bytes on the wire retail never sends.
		public async Task HandleAdjustSkill(INetworkClient client, SkillIIR request)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			if (request.Handle != character.Id)
			{
				_logger.LogWarning("SkillIIR addressed to handle 0x{Handle:X8} from character {CharacterId} - ignored.", request.Handle, character.Id);
				return;
			}

			var spent = -request.Adjustments.Where(a => a.StatId == (uint)Pool).Sum(a => a.Delta);
			var credits = request.Adjustments.Where(a => a.StatId != (uint)Pool).ToArray();

			// A spend conserves points: the pool loses exactly what the skills gain. Anything else - a credit-only
			// list, a negative skill delta (that is a sellback, see the class comment), a mismatched total - is not
			// a shape the retail client produces.
			if (spent <= 0 || credits.Length == 0 || credits.Any(a => a.Delta <= 0) || credits.Sum(a => a.Delta) != spent)
			{
				_logger.LogWarning("Character {CharacterId} sent a SkillIIR that is not a conserving spend ({Adjustments}) - ignored.",
					character.Id, string.Join(", ", request.Adjustments.Select(a => $"0x{a.StatId:X3}{a.Delta:+#;-#;0}")));

				return;
			}

			// The stat sheet is the authority: it is what the login snapshot is authored from, so a spend that is not
			// written there does not survive a relog.
			var sheet = await _database.GetCharacterStats(character.Id);
			var current = sheet.GroupBy(s => s.StatId).ToDictionary(g => g.Key, g => g.First().Value);

			if (!current.TryGetValue(Pool, out var available) || available < spent)
			{
				_logger.LogWarning("Character {CharacterId} tried to spend {Spent} skillpoint(s) with {Available} available - ignored.",
					character.Id, spent, current.TryGetValue(Pool, out var have) ? have : 0);

				return;
			}

			// Every credited skill must already have a sheet slot. Minting one would put the stat on the wire at an
			// ordinal no capture has, so an unknown skill is refused rather than invented.
			var unknown = credits.Where(a => !current.ContainsKey((CharacterStatId)a.StatId)).ToArray();
			if (unknown.Length != 0)
			{
				_logger.LogWarning("Character {CharacterId} tried to spend into stat(s) {Stats} that have no stat-sheet slot - ignored.",
					character.Id, string.Join(", ", unknown.Select(a => $"0x{a.StatId:X3}")));

				return;
			}

			var updated = new Dictionary<CharacterStatId, uint> { [Pool] = (uint)(available - spent) };
			foreach (var credit in credits)
				updated[(CharacterStatId)credit.StatId] = (uint)(current[(CharacterStatId)credit.StatId] + credit.Delta);

			await _database.SaveCharacterStats(character.Id, updated);

			// Retail's order: the skill(s) first, the pool last.
			foreach (var credit in credits)
			{
				var stat = (CharacterStatId)credit.StatId;
				if (DynelMirrored.Contains(stat))
					await WorldIir.Send(client, StatCodec.Build(StatIirs.ForCharacter(character.Id, credit.StatId, updated[stat], StatIirs.WorldObjectEnv)));

				await WorldIir.Send(client, StatCodec.Build(StatIirs.ForCharacter(character.Id, credit.StatId, updated[stat])));
			}

			await WorldIir.Send(client, StatCodec.Build(StatIirs.ForCharacter(character.Id, (uint)Pool, updated[Pool])));

			_logger.LogInformation("Character {CharacterId} spent {Spent} skillpoint(s) on {Skills}; {Remaining} left.",
				character.Id, spent, string.Join(", ", credits.Select(a => $"{(CharacterStatId)a.StatId} +{a.Delta}")), updated[Pool]);
		}
	}
}
