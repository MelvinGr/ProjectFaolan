using System;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Progression
{
	// ALTERNATE ADVANCEMENT: the xp pools, the give-away that turns a full pool into perk tokens, and the wallet those
	// tokens live in (#150). AA adds no messages of its own - it rides the feedback, stat and token-amount channels
	// that already exist - so this service is rules plus three sends, and the rules themselves are
	// `AlternateAdvancementRules` (pure, and tested without a world).
	//
	// It sits beside LevelUpService for the reason that service's own header gives: a kill is one SOURCE of
	// advancement, not the definition of it. Ordinary experience and AA experience are two pools fed by the same
	// event, and the AA one is the pool that keeps filling once the level bar cannot.
	//
	// ⚠️ Retail states ONE token pool per message and only when it moves - ten pushes across the whole corpus, each a
	// single pool. So there is deliberately NO wallet burst at world entry: the client keeps its own totals and asking
	// it to re-read them would be traffic retail never sends.
	public sealed class AlternateAdvancementService
	{
		private readonly ICharacterProgressionRepository _progression;
		private readonly ILogger _logger;

		public AlternateAdvancementService(ICharacterProgressionRepository progression, ILogger logger)
		{
			_progression = progression;
			_logger = logger;
		}

		/// <summary>
		///     A kill's share of the killer's ALTERNATE ADVANCEMENT: the xp the creature is worth into the PvE pool,
		///     the announcement retail makes for it, and the token give-away if the pool fills.
		/// </summary>
		/// <remarks>
		///     ⭐ <b>The KILLING BLOW attributes the grant, not the damage majority.</b> A level-75 kill whose last hit
		///     came from the necromancer's PET paid nothing although the player had damaged it moments before, while a
		///     level-78 kill where the pets dealt the clear majority but the player landed the last hit paid in full.
		///     This is called from the killing-blow path for exactly that reason.
		/// </remarks>
		public async Task AwardKillXp(INetworkClient client, uint creatureLevel, uint? npcType, uint? npcFlags)
		{
			var character = client.Session.Character;

			// The same gate the accrual has, and it covers the restatement below too: no capture shows a kill moving
			// this stat below the level cap, in either direction.
			if (character.Level < LevelingResolver.MaxLevel)
				return;

			var amount = AlternateAdvancementRules.KillXp(character.Level, LevelingResolver.MaxLevel, creatureLevel, npcType, npcFlags);
			if (amount > 0)
				await AwardXp(client, amount); // announces, drains, and restates the pool ONCE

			// ⭐ #935: retail restates the pool TWICE per kill, whether or not the kill pays. Measured by pairing each
			// push with the event nearest to it rather than by counting both per capture and dividing: of the 142
			// `AAPvEXP` pushes in the corpus, 128 sit within 30 packets of a death the PLAYER caused, at a median
			// distance of -2 - and 52 deaths carry exactly two. The 23 that carry none are the deaths the player only
			// witnessed, which is the count of `deaths - playerKills`.
			//
			// ⚠️ The value is whatever the pool now reads, unchanged on a kill that pays nothing: the captures with no
			// grant at all still push, and their pool never moves (10 pushes, 0 moves in one).
			await SendPool(client, CharacterStatId.AAPvEXP, character.AaPvEXp);
			if (amount == 0)
				await SendPool(client, CharacterStatId.AAPvEXP, character.AaPvEXp);
		}

		/// <summary>
		///     Adds <paramref name="amount" /> to the character's PvE AA pool, announces it, restates the pool stat and
		///     pays out any give-away the pool has now earned.
		/// </summary>
		/// <remarks>
		///     Public because a kill is not the only source: the corpus's flat item redemptions pay 15,000-125,000 at a
		///     time (and pay a Tortage lowbie, where a kill pays nothing), and a quest turn-in pays through a modifier.
		/// </remarks>
		public async Task AwardXp(INetworkClient client, uint amount, int? bonusPercent = null)
		{
			var character = client.Session.Character;
			character.AaPvEXp += amount;

			// The announcement first, then the stat: that is the order retail sends them in, and the client's own
			// "next give-away" percentage is computed from the stat, so the line would otherwise describe a pool the
			// client has not been told about yet.
			await GameWire.SendFeedback(client, new AlternateAdvancementXpGranted(amount, bonusPercent));

			var (specific, generic, remainder) = AlternateAdvancementRules.Drain(character.AaPvEXp);
			character.AaPvEXp = remainder;

			await SendPool(client, CharacterStatId.AAPvEXP, character.AaPvEXp);
			await _progression.SetAlternateAdvancementPools(character.Id, character.AaPvEXp, character.AaPvPXp);

			_logger.LogInformation("AA: {Name} gained {Amount} AA xp ({Pool}/{Cap}).", character.Name, amount, character.AaPvEXp, AlternateAdvancementRules.XpCap);

			if (specific > 0)
				await GrantTokens(client, TokenPool.AltAdvancementPvEToken, (ulong)specific);

			if (generic > 0)
				await GrantTokens(client, TokenPool.AltAdvancementGeneralToken, (ulong)generic);
		}

		/// <summary>
		///     Credits one token wallet and tells the client its new totals.
		/// </summary>
		/// <remarks>
		///     ⚠️ The UNSPENT ceiling is applied to the credit, not to the stored total: the wire carries lifetime
		///     `gained`/`spent` and the client subtracts, so clamping the stored total would make an already-spent
		///     token reappear. Nothing in the client reads the cap - the three `*UnusedTokensCap` keys are absent from
		///     the exe - so it is the server's to enforce, and enforcing it means declining the credit that would
		///     breach it.
		/// </remarks>
		public async Task GrantTokens(INetworkClient client, TokenPool pool, ulong count)
		{
			var characterId = client.Session.Character.Id;
			var wallets = await _progression.GetCharacterTokens(characterId);
			var wallet = Array.Find(wallets, w => w.Pool == pool);
			var unspent = (long)((wallet?.Gained ?? 0) - (wallet?.Spent ?? 0));
			var room = AlternateAdvancementRules.UnspentTokenCap - unspent;
			if (room <= 0)
			{
				_logger.LogInformation("AA: {Pool} is at the {Cap}-token unspent ceiling for character {CharacterId}; the give-away is dropped.",
					pool, AlternateAdvancementRules.UnspentTokenCap, characterId);
				return;
			}

			var credited = Math.Min(count, (ulong)room);
			var (gained, spent) = await _progression.AddTokens(characterId, pool, credited, 0);
			await SendWallet(client, pool, gained, spent);
			_logger.LogInformation("AA: character {CharacterId} was paid {Credited} {Pool} ({Gained} gained / {Spent} spent).", characterId, credited, pool, gained, spent);
		}

		/// <summary>
		///     Charges <paramref name="cost" /> to one token wallet. False - and nothing sent - when the wallet does
		///     not hold that much, which is the perk purchase's refusal.
		/// </summary>
		public async Task<bool> Spend(INetworkClient client, TokenPool pool, uint cost)
		{
			var characterId = client.Session.Character.Id;
			var wallets = await _progression.GetCharacterTokens(characterId);
			var wallet = Array.Find(wallets, w => w.Pool == pool);
			if ((wallet?.Gained ?? 0) - (wallet?.Spent ?? 0) < cost)
				return false;

			var (gained, spent) = await _progression.AddTokens(characterId, pool, 0, cost);
			await SendWallet(client, pool, gained, spent);
			_logger.LogInformation("AA: character {CharacterId} spent {Cost} {Pool} ({Gained} gained / {Spent} spent).", characterId, cost, pool, gained, spent);

			return true;
		}

		/// <summary>
		///     Restates one wallet's current totals to the client, without changing them (#936).
		/// </summary>
		/// <remarks>
		///     ⭐ Retail opens a NEW character with exactly this, and only this: a
		///     <c>DispatchUpdateTokenAmount</c> saying <see cref="TokenPool.VeteranPoints" /> <b>0 gained / 0 spent</b>.
		///     It is in five of the six character-CREATE captures and in NO login capture, at the same place in the
		///     burst every time - between the entry teleport and the map change. So it is a statement that the wallet
		///     is empty rather than a credit, and it is sent ONCE, at creation.
		///     <para />
		///     ⚠️ That is also why this does not belong on the ordinary world entry: restating a wallet on every login
		///     and every teleport is traffic retail does not send.
		/// </remarks>
		public async Task StateWallet(INetworkClient client, TokenPool pool)
		{
			var wallets = await _progression.GetCharacterTokens(client.Session.Character.Id);
			var wallet = Array.Find(wallets, w => w.Pool == pool);
			await SendWallet(client, pool, wallet?.Gained ?? 0, wallet?.Spent ?? 0);
		}

		/// <summary>How many unspent tokens one wallet holds - what the purchase gate reads.</summary>
		public async Task<ulong> Unspent(uint characterId, TokenPool pool)
		{
			var wallets = await _progression.GetCharacterTokens(characterId);
			var wallet = Array.Find(wallets, w => w.Pool == pool);

			return (wallet?.Gained ?? 0) - (wallet?.Spent ?? 0);
		}

		// DispatchUpdateTokenAmount (s2c 0x204D) on the GameCharAgent -> GameCharInterface conversation the corpus's
		// own ten pushes ride (sender 0xA9ECB95D, receiver 0x8B87F791 - the 0x201C spawn-handshake pair).
		private static Task SendWallet(INetworkClient client, TokenPool pool, ulong gained, ulong spent)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchUpdateTokenAmount, UpdateTokenAmountCodec.Build(new UpdateTokenAmount(pool, gained, spent, false))));
		}

		// The pool itself, as the standalone player-addressed StatIIR retail pushes in the same frame as the
		// announcement. Its value IS the running total toward the cap - the client does the division.
		private static Task SendPool(INetworkClient client, CharacterStatId stat, uint value)
		{
			return WorldIir.Send(client, StatCodec.Build(StatIirs.ForCharacter(client.Session.Character.Id, (uint)stat, value)));
		}
	}
}
