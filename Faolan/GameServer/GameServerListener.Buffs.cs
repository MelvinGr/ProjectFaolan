using Faolan.Core.Constants;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// ISpellEffectOutbound (#439) - the three wire ops a SpellEffect row needs, so SpellEffectService dispatches
		// data-driven effects without holding the listener. All three already existed for their original callers; this
		// only names them as one seam.
		Task ISpellEffectOutbound.ApplySelfBuff(INetworkClient client, uint wireSpellId, bool walkChain)
		{
			var self = client.Session.Character.Id;
			return _buffs.ApplySpell(client, self, self, wireSpellId, walkChain
				? null
				: SpellChainWalk.Registry());
		}

		async Task<bool> ISpellEffectOutbound.SummonPet(INetworkClient client, string template, bool combat, uint pointCost)
		{
			return await SummonPetForPlayer(client, template, combat, pointCost) != null;
		}

		Task ISpellEffectOutbound.SummonTimedPet(INetworkClient client, string template, TimeSpan duration, TimeSpan? cap, uint pointCost)
		{
			return SummonTimedPetForPlayer(client, template, duration, cap, pointCost);
		}

		// The buff-removal end-twin (#274/#253): the same ApplySpell carrier as the apply, rank byte -> 1, empty
		// payload. Routes the private SendBuffRemoval used by .cancelbuff so the world-tick expiry and the GM cancel
		// share one builder.
		Task IWorldOutbound.SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			return _buffs.SendBuffRemoval(client, applyTo, applyFrom, appliedSpellId);
		}

		// Inits the energy pool + starts the single persistent energy loop (idempotent). Called on sprint and on weave
		// start, so energy is tracked + regenerates regardless of which happens first.
		internal void EnsureEnergyLoop(INetworkClient client)
		{
			var session = client.Session;
			if (session.Character == null)
				return;

			if (session.EnergyMax == 0)
			{
				session.EnergyMax = SpellweaveCatalog.EnergyMaxForClass(session.Character.Class);
				session.Energy = session.EnergyMax;
			}

			if (!session.EnergyLoopActive)
				_ = RunEnergyMirror(client); // fire-and-forget; it logs its own exceptions
		}

		// Shared pet-summon (#307, docs/subsystems/summon-pet-decode.md): spawn the creature, bind it as a REAL pet by emitting
		// PetIIR owner-assign (so the client shows the pet bar + sends QueuePetCommand 0x205b), make it follow, and
		// mark the owner. Used by BOTH the .pet GM command AND the CastSpell hook (casting a spell with a SummonPet effect).
		// `combat` picks the PetIIR kind triple (combat vs social). Returns null if no spawnable template matches.
		internal async Task<WorldEntity?> SummonPetForPlayer(INetworkClient client, string petTemplate, bool combat, uint pointCost = 0)
		{
			// The RIGHT model (#244/#307): spawn the pet from its creature TYPE (columns: StatCore + Equipment + Morphs), no world
			// placement. Fall back to the placement path only as a GM-test convenience (`.pet <any world NPC>`), so a
			// creature that hasn't had its stat core lifted to the type yet can still be summoned as a follower.
			var npcType = await Database.GetNpcByName(petTemplate);
			var entity = npcType != null
				? await _broadcaster.SpawnPetForPlayer(client, npcType)
				: await _broadcaster.SpawnNpcForPlayer(client, petTemplate);
			if (entity == null)
				return null;

			var owner = client.Session.Character.Id;
			entity.OwnerHandle = owner;
			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, b =>
				b.WriteArrayUInt32Length(PetCodec.Build(PetBindings.Assign(owner, entity.Handle, combat
					// ⭐ The pet's own POINT COST rides here (#823) - `f2` is what the client's pet-point bar counts, and
					// this used to be the constant 2 for every pet in the game. `pointCost` is the SUMMONING SPELL's
					// `SummonPointCost`; a pet with no spell behind it (the `.pet` GM command) summons at 0, which is
					// the value retail's own social pets carry rather than an invented one.
					? PetBindings.CombatKind(pointCost)
					: PetBindings.SocialKind)))));

			_broadcaster.SetNpcFollow(client, entity.Handle, true);
			return entity;
		}

		// Summons a pet that lives for `duration`, or EXTENDS the one already out. Frozen Hatred's fatality pet is the
		// shape this serves (#140): "the Frozen Hatred pet appears and is under your control for 60 seconds. If you
		// already have a Frozen Hatred pet, the duration of the pet will increase by another 60 seconds. This timer is
		// capped at 5 minutes." (in-client tooltip). One field carries both halves - DespawnAt is pushed out on each
		// fatality and clamped to the cap, and the world tick fades whatever it finds expired.
		internal async Task SummonTimedPetForPlayer(INetworkClient client, string petTemplate, TimeSpan duration, TimeSpan? cap, uint pointCost = 0)
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
				Logger.LogInformation("Timed pet '{Pet}' for '{Player}' extended to {Seconds:F0}s remaining (cap {Cap}).", petTemplate, client.Session.Character?.Name,
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
				Logger.LogWarning("Timed pet '{Pet}' could not be summoned for '{Player}': no NPC type by that name.", petTemplate, client.Session.Character?.Name);
				return;
			}

			pet.DespawnAt = now + duration;
			Logger.LogInformation("Timed pet '{Pet}' summoned for '{Player}' for {Seconds:F0}s.", petTemplate, client.Session.Character?.Name, duration.TotalSeconds);
		}

		// The single per-session energy loop: drains while sprinting, REGENS when idle, and steps aside while weaving
		// (the weave owns the drain + the 0x200E stream). Persistent - it keeps ticking for the whole session so energy
		// recovers after a weave/sprint - and exits only on disconnect. A PeriodicTimer loop, not a self-rescheduling
		// callback. EnergyLoopActive guards against a second loop.
		//
		// It also MIRRORS the pool to the client as CharacterStatId.Energy, which is not a decoration: 1000028 is the
		// value the client's own energy model holds (FUN_0112ad70 caches it and drives the Exhausted state off it), and
		// it is what the sprint bar's view is seeded from. Retail streams exactly this - 1,650 records across the
		// corpus, on StatUpdateIIR (0x96B8DC59), per entity - and its cadence is a push every ~3-7s WHILE the pool is
		// moving, going silent once it is back at the max (Hoxxling_..._gameplay2: 14000 -> 13977 -> 14000 -> quiet,
		// with ~4800-point drops for real sprints). EnergyMirrorTicks + the `!= mirrored` guard reproduce that shape:
		// the loop is silent at full and speaks only while the number is changing.
		//
		// ⚠️ This is a MIRROR, not the visual. The client sprints and drains locally with no server traffic at all -
		// which is why retail sends no 0x200E during a sprint - so what this restores is the server's number, not the
		// bar. Only the SPELLWEAVE drain is server-driven, and that owns the 0x200E stream (see SpellweaveService).
		private async Task RunEnergyMirror(INetworkClient client)
		{
			client.Session.EnergyLoopActive = true;
			try
			{
				// What was last put on the wire as CharacterStatId.Energy, so the mirror only speaks when the pool
				// actually moved. uint.MaxValue is "nothing sent yet" rather than 0, which is a legal energy value.
				var mirrored = uint.MaxValue;
				var sinceMirror = 0;
				var exhausted = false;

				using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(EnergyTickMs));
				while (await timer.WaitForNextTickAsync())
				{
					var session = client.Session;
					if (session.Account == null)
						return; // disconnected

					if (session.IsSpellweaving)
						continue; // the weave owns the energy this tick

					if (session.IsSprinting)
						session.Energy -= (uint)Math.Min(SprintEnergyDrainPerTick, (int)session.Energy);
					else if (session.Energy < session.EnergyMax)
						session.Energy = Math.Min(session.EnergyMax, session.Energy + EnergyRegenPerTick);

					// Exhausted rides the pool, not the weave: retail applies it the instant Energy reaches 0 and sends
					// the end twin on the first push that lifts it back above zero, sprint included
					// (SpellweaveCatalog.ExhaustedBuff carries the capture). The weave has its own copy of this because
					// it also owns a health DoT while empty; this is the plain-sprint half.
					if (session.Character is { } self)
					{
						if (session.Energy == 0 && !exhausted)
						{
							exhausted = true;
							await _buffs.ApplyBuffById(client, self.Id, self.Id, SpellweaveCatalog.ExhaustedBuff);

							// An empty pool ends the sprint, and the client has to be TOLD - otherwise it keeps
							// rendering a sprint the server has stopped draining for (#867). Both flags ride this
							// same zero-crossing rather than a threshold of their own: `canSprint` is the refusal the
							// client checks before it even asks (00 x151 in the corpus), `isSprinting` is the state.
							if (session.IsSprinting)
							{
								session.IsSprinting = false;
								await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSetIsSprinting, false);
							}

							await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSyncCanSprint, false);
						}
						else if (session.Energy > 0 && exhausted)
						{
							exhausted = false;
							await _buffs.SendBuffRemoval(client, self.Id, self.Id, SpellweaveCatalog.ExhaustedBuff);
							await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSyncCanSprint, true);
						}
					}

					if (++sinceMirror >= EnergyMirrorTicks && session.Energy != mirrored && session.Character != null)
					{
						sinceMirror = 0;
						mirrored = session.Energy;
						await SendStatUpdate(client, new CharacterStatBlock { [CharacterStatId.Energy] = session.Energy },
							session.Character.Id);
					}
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Energy mirror loop failed.");
			}
			finally
			{
				var session = client.Session;
				session.EnergyLoopActive = false;
			}
		}

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// tools/capture/decode_learn_spell.py, byte-exact in LearnSpellTests). The client pops the "learned a new spell"
		// toast itself. (The sibling DispatchForgetSpell 0x200B / RemoveSpecialAbility 0x2053 / UntrainSingleFeat
		// 0x2011 have no capture oracle yet, so they are not built - see #73.)
		private Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchLearnSpell,
				LearnSpellCodec.Build(spellId)));
		}

		private static Task SendSprintBar(INetworkClient client, uint energy)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x54), client.Endpoint(RpcInterfaces.GameCharInterfaceProjectType),
				GameCharInterfaceProjectMethods.DispatchUpdateSprintbar, SprintBarCodec.Build(energy)));
		}

		// One of the three server-owned sprint booleans (#867). All four dispatches in this family share one endpoint
		// pair in the corpus - sender GameCharAgentProject at the session's own small instance, receiver the player's
		// GameCharInterfaceProject - which is the pair SendSprintBar already used.
		private static Task SendSprintFlag(INetworkClient client, GameCharInterfaceProjectMethods method, bool on)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x54), client.Endpoint(RpcInterfaces.GameCharInterfaceProjectType),
				method, SprintFlagCodec.Build(on)));
		}

		// The sprint key changed state. Retail answers a CHANGE of `sprintInput` rather than every frame - 2,959
		// changes drew 2,624 `DispatchSetIsSprinting` answers over the corpus - and it does NOT merely echo: 14.5% of
		// answers carry a different value from the input before them, which is the server refusing.
		//
		// The one refusal we can justify is an empty pool, and it needs no new rule: it is the same zero-crossing the
		// Exhausted buff already rides below. Anything finer (a cooldown, a stance, a movement-mode gate) would be
		// invented - the corpus shows THAT retail refuses, not what else it refuses for.
		private async Task TrackSprintEnergy(INetworkClient client, bool sprinting)
		{
			var session = client.Session;
			EnsureEnergyLoop(client);

			if (session.SprintInputRequested == sprinting)
				return;

			session.SprintInputRequested = sprinting;

			var granted = sprinting && session.Energy > 0;
			session.IsSprinting = granted;
			await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSetIsSprinting, granted);

			// A sprint ending is where retail puts most of its regen-delayed sends (514 of them follow an
			// `isSprinting = 0`, median 820 ms later). Every captured frame carries 0 - the corpus proves the CLEAR
			// and never the set, so this sends only what it can show.
			// `[V0: what sets regenDelayed - no capture carries a 1]`
			if (!granted)
				await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSyncRegenDelayed, false);
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantability / future
		// level-up), false for the silent per-login re-grant.
		// `sender` defaults to the login/world grant sender (GameCharAgent 0x58); the create-editor world-prep grant
		// rides its own conversation (GameCharAgent 0x0C), so it passes that in - the only thing that differs between
		// the two grant sites (the 81-byte body incl. the TEA key comes wholly from the Ability row either way).
		internal static Task SendAddSpecialAbility(INetworkClient client, Ability ability, bool announce = true, ProtoEndpoint? sender = null)
		{
			return client.SendAsync(new Rpc(sender ?? LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddSpecialAbility,
				AddSpecialAbilityCodec.Build(ability, announce)));
		}

		// --- ISpellweaveOutbound: the spellweaving FSM's wire + world-state surface. Explicit forwards to the
		// methods that already existed, which is all the seven Func<> seams ever were - see ISpellweaveOutbound.
		bool ISpellweaveOutbound.IsMounted(INetworkClient client)
		{
			return _simulation.PlayerWorldEntity(client)?.IsMounted == true;
		}

		void ISpellweaveOutbound.EnsureEnergyLoop(INetworkClient client)
		{
			EnsureEnergyLoop(client);
		}

		Task ISpellweaveOutbound.ApplyBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId)
		{
			return _buffs.ApplyBuffById(client, applyTo, applyFrom, wireSpellId);
		}

		Task ISpellweaveOutbound.RemoveBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId)
		{
			return _buffs.SendBuffRemoval(client, applyTo, applyFrom, wireSpellId);
		}

		Task ISpellweaveOutbound.SendFeedback(INetworkClient client, string message)
		{
			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, message);
		}

		Task ISpellweaveOutbound.SendMountedRefusal(INetworkClient client)
		{
			return SendWorldChat(client, ChatTextXml.MustNotHaveMountRunning);
		}

		Task ISpellweaveOutbound.SendSprintBar(INetworkClient client, uint energy)
		{
			return SendSprintBar(client, energy);
		}
	}
}
