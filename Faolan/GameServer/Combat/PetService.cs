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
	/// <summary>
	///     A player's summoned pet: summon, the timed variant's extend-or-summon, and dismissal
	///     (docs/subsystems/summon-pet-decode.md).
	/// </summary>
	/// <remarks>
	///     All three are wire and world-model work over the broadcaster and the simulation. They are reached through
	///     <see cref="ISpellEffectOutbound" /> for the cast path and through the QueuePetCommand handler for the pet
	///     bar.
	/// </remarks>
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

		/// <summary>
		///     Spawns the creature, binds it as a real pet by emitting the PetIIR owner-assign - which is what makes the
		///     client show the pet bar and send QueuePetCommand 0x205b - makes it follow, and marks the owner
		///     (docs/subsystems/summon-pet-decode.md).
		/// </summary>
		/// <param name="combat">Picks the PetIIR kind triple: combat or social.</param>
		/// <returns>Null when no spawnable template matches.</returns>
		public async Task<WorldEntity?> SummonPetForPlayer(INetworkClient client, string petTemplate, bool combat, uint pointCost = 0)
		{
			// Spawn the pet from its creature type (StatCore, Equipment and Morphs columns), with no world placement.
			// Fall back to the placement path only for a world-placed creature, so a creature whose stat core has not
			// been lifted to the type can still be summoned as a follower.
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
					// The pet's own point cost rides here: `f2` is what the client's pet-point bar counts, and
					// `pointCost` is the summoning spell's `SummonPointCost`. A pet with no spell behind it summons
					// at 0, which is the value retail's own social pets carry.
					? PetBindings.CombatKind(pointCost)
					: PetBindings.SocialKind)))));

			_npcs.SetNpcFollow(client, entity.Handle, true);
			return entity;
		}

		/// <summary>
		///     Summons a pet that lives for <paramref name="duration" />, or extends the one already out.
		/// </summary>
		/// <remarks>
		///     Frozen Hatred's fatality pet is the shape this serves - its in-client tooltip reads "the Frozen Hatred
		///     pet appears and is under your control for 60 seconds. If you already have a Frozen Hatred pet, the
		///     duration of the pet will increase by another 60 seconds. This timer is capped at 5 minutes." One field
		///     carries both halves: <c>DespawnAt</c> is pushed out on each fatality and clamped to the cap, and the
		///     world tick fades whatever it finds expired.
		/// </remarks>
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

		/// <summary>
		///     The pet bar's dismiss, in all three of its spellings (see <see cref="HandlePetCommand" />): retail's
		///     PetIIR mode-1 remove, then the n3ToClientQuit despawn.
		/// </summary>
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

		/// <summary>
		///     QueuePetCommand (0x205B, GameCharAgent): the pet command bar. Body = [u32 type=0xC350][u32 petHandle]
		///     [u32 command].
		/// </summary>
		/// <remarks>
		///     <para>
		///         The command set is the client's own twelve - see <see cref="PetCommand" /> and
		///         docs/subsystems/summon-pet-decode.md for the derivation.
		///     </para>
		///     <para>
		///         Dismissal is the only family that acts: retail's PetIIR mode-1 remove plus the n3ToClientQuit
		///         despawn. The stance commands need pet-side combat AI - a pet picking and engaging a target - which
		///         does not exist, since a summoned minion only follows. They are logged rather than silently dropped
		///         so the bar's traffic stays visible.
		///     </para>
		///     <para>
		///         All three dismissal codes are honoured, not just the captured one: <c>DismissFollower</c> (0x1F8) is
		///         what the corpus shows, but <c>Dismiss</c> and <c>AllDismiss</c> are the same act from the per-pet
		///         and all-pets bars, and answering only the captured spelling would leave the other two bars' buttons
		///         dead.
		///     </para>
		/// </remarks>
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
