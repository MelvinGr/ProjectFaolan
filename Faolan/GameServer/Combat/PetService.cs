using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;
using Faolan.Core.Extensions;
using Faolan.Core;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// A player's summoned PET (#307, docs/subsystems/summon-pet-decode.md): summon, the timed variant's
	// extend-or-summon, and dismissal. All three are wire + world-model work over the broadcaster and the
	// simulation - the listener only ever forwarded them - so they live here and are reached through
	// ISpellEffectOutbound (the cast path) and the QueuePetCommand handler (the pet bar).
	public sealed class PetService
	{
		private readonly NpcBroadcaster _npcs;
		private readonly INpcRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _simulation;

		public PetService(INpcRepository database, WorldSimulation simulation, NpcBroadcaster npcs, ILogger logger)
		{
			_database = database;
			_simulation = simulation;
			_npcs = npcs;
			_logger = logger;
		}

		// Shared pet-summon (#307, docs/subsystems/summon-pet-decode.md): spawn the creature, bind it as a REAL pet by emitting
		// PetIIR owner-assign (so the client shows the pet bar + sends QueuePetCommand 0x205b), make it follow, and
		// mark the owner. Reached from the CastSpell hook (a spell carrying a SummonPet effect) through ISpellEffectOutbound.
		// `combat` picks the PetIIR kind triple (combat vs social). Returns null if no spawnable template matches.
		public async Task<WorldEntity?> SummonPetForPlayer(INetworkClient client, string petTemplate, bool combat, uint pointCost = 0)
		{
			// The RIGHT model (#244/#307): spawn the pet from its creature TYPE (columns: StatCore + Equipment + Morphs), no world
			// placement. Fall back to the placement path only for a world-placed creature, so a
			// creature that hasn't had its stat core lifted to the type yet can still be summoned as a follower.
			var npcType = await _database.GetNpcByName(petTemplate);
			var entity = npcType != null
				? await _npcs.SpawnPetForPlayer(client, npcType)
				: await _npcs.SpawnNpcForPlayer(client, petTemplate);
			if (entity == null)
				return null;

			var owner = client.Session.Character.Id;
			entity.OwnerHandle = owner;
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, b =>
				b.WriteArrayUInt32Length(PetCodec.Build(PetBindings.Assign(owner, entity.Handle, combat
					// ⭐ The pet's own POINT COST rides here (#823) - `f2` is what the client's pet-point bar counts, and
					// this used to be the constant 2 for every pet in the game. `pointCost` is the SUMMONING SPELL's
					// `SummonPointCost`; a pet with no spell behind it summons at 0, which is
					// the value retail's own social pets carry rather than an invented one.
					? PetBindings.CombatKind(pointCost)
					: PetBindings.SocialKind)))));

			_npcs.SetNpcFollow(client, entity.Handle, true);
			return entity;
		}

		// Summons a pet that lives for `duration`, or EXTENDS the one already out. Frozen Hatred's fatality pet is the
		// shape this serves (#140): "the Frozen Hatred pet appears and is under your control for 60 seconds. If you
		// already have a Frozen Hatred pet, the duration of the pet will increase by another 60 seconds. This timer is
		// capped at 5 minutes." (in-client tooltip). One field carries both halves - DespawnAt is pushed out on each
		// fatality and clamped to the cap, and the world tick fades whatever it finds expired.
		public async Task SummonTimedPetForPlayer(INetworkClient client, string petTemplate, TimeSpan duration, TimeSpan? cap, uint pointCost = 0)
		{
			var owner = _simulation.PlayerWorldEntity(client);
			if (owner == null)
				return;

			var now = DateTimeOffset.UtcNow;
			var existing = _simulation.World.ZoneForEntity(owner).Entities.FirstOrDefault(e => e.OwnerHandle == owner.Handle && e.Name == petTemplate && e.DespawnAt != null);
			if (existing != null)
			{
				var extended = (existing.DespawnAt!.Value > now
					? existing.DespawnAt.Value
					: now) + duration;
				existing.DespawnAt = cap is { } ceiling && extended > now + ceiling
					? now + ceiling
					: extended;
				_logger.LogInformation("Timed pet '{Pet}' for '{Player}' extended to {Seconds:F0}s remaining (cap {Cap}).", petTemplate, client.Session.Character?.Name,
					(existing.DespawnAt.Value - now).TotalSeconds, cap is { } c
						? $"{c.TotalSeconds:F0}s"
						: "none");
				return;
			}

			var pet = await SummonPetForPlayer(client, petTemplate, true, pointCost);
			if (pet == null)
			{
				// No creature type by that name: say so rather than silently granting nothing - the buff that asked for
				// it is already on the player, so a missing template is a content gap, not a state.
				_logger.LogWarning("Timed pet '{Pet}' could not be summoned for '{Player}': no NPC type by that name.", petTemplate, client.Session.Character?.Name);
				return;
			}

			pet.DespawnAt = now + duration;
			_logger.LogInformation("Timed pet '{Pet}' summoned for '{Player}' for {Seconds:F0}s.", petTemplate, client.Session.Character?.Name, duration.TotalSeconds);
		}

		// The pet bar's DISMISS (all three of its spellings - see HandlePetCommand): retail's PetIIR mode-1 remove,
		// then the n3ToClientQuit despawn.
		public async Task DismissPet(INetworkClient client, uint petHandle)
		{
			var owner = client.Session.Character.Id;
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(PetCodec.Build(PetBindings.Remove(owner, petHandle)))));

			var removed = await _npcs.DespawnEntityForPlayer(client, petHandle);
			_logger.LogInformation(removed != null
				? "Pet 0x{Pet:X8} dismissed (despawned)."
				: "Pet dismiss: no entity 0x{Pet:X8} in the player's zone.", petHandle);
		}

		// QueuePetCommand (0x205B, GameCharAgent): the pet command bar. Body = [u32 type=0xC350][u32 petHandle]
		// [u32 command]. The command set is the client's own twelve, recovered in #307 - see PetCommand for the
		// derivation and for the two conclusions it overturned (docs/subsystems/summon-pet-decode.md).
		//
		// DISMISSAL is the only family that ACTS today: retail's PetIIR mode-1 remove + the n3ToClientQuit despawn. The
		// stance commands need pet-side combat AI (a pet picking and engaging a target), which does not exist - a
		// summoned minion only follows, via SetNpcFollow. They are logged rather than silently dropped so the bar's
		// traffic is visible while that is built.
		//
		// All three dismissal codes are honoured, not just the captured one: `DismissFollower` (0x1F8) is what the
		// corpus shows, but `Dismiss` and `AllDismiss` are the same act from the per-pet and all-pets bars, and
		// answering only the captured spelling would leave the other two bars' buttons dead.
		internal async Task HandlePetCommand(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // type 0xC350
			var petHandle = body.ReadUInt32();
			var command = body.ReadUInt32<PetCommand>();
			_logger.LogInformation("QueuePetCommand from {Owner:X8}: pet 0x{Pet:X8}, command {Command}.", client.Session.Character.Id, petHandle, command);

			if (command is PetCommand.DismissFollower or PetCommand.Dismiss or PetCommand.AllDismiss)
				await this.DismissPet(client, petHandle);
		}
	}
}
