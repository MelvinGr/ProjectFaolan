using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     The records retail sends around a death, as value factories.
	/// </summary>
	/// <remarks>
	///     Not a codec: a death is five different messages (SimpleCharKilledByUpdateIIR, BehaviorControlCenterIIR,
	///     MagicBuffIIR, LookAtIIR and MoveTaskIIR), each of which has its own codec. Orchestrating a sequence is not
	///     encoding a message, so what lives here are the values those codecs encode.
	///     <para>
	///         The order retail sends them in for a dying entity: drop the target (LookAts.DropTarget), the death pose
	///         (<see cref="Pose" />), the death behaviour (<see cref="Bcc" />), and for a player the kill record
	///         (<see cref="SimpleCharKilledByUpdateCodec" />) followed by the release screen.
	///     </para>
	/// </remarks>
	public static class DeathSequence
	{
		/// <summary>
		///     The counter values the two golden captures happen to carry. They exist only so the byte-exact goldens can name
		///     them; a live death passes the dying entity's real counter.
		/// </summary>
		/// <remarks>
		///     Field 3 of the death record is the dying entity's own behaviour counter (CommandqueueServerId), not a death
		///     variant: across all 272 captured death records it takes 40 distinct values from 2 to 373, and against the same
		///     actor's previous BCC record of any kind it is +1 on 265 of 267. The progression is visible inside one kill - an
		///     NPC's two swings carry 5 and 6, the abort of the second carries 6 again, and its death carries 7. CombatService
		///     passes WorldEntity.AttackSwingCounter, the same counter the swing acks use.
		/// </remarks>
		public const byte CapturedNpcDeathCounter = 0x05;
		public const byte CapturedPlayerDeathCounter = 0x03;

		/// <summary>
		///     The behaviourId is the creature's death animation and is creature-type specific - decoded across the corpus by
		///     the importer's NpcDeathBehaviorExtractor: 41877 humanoid biped, 17036 demon or large caster, 215333 quadruped
		///     or beast. It is harvested per NpcTemplate (DeathBehaviorId) and passed in; un-harvested types default to the
		///     humanoid one.
		/// </summary>
		public const uint HumanoidDeathBehaviorId = 41877;

		/// <summary>
		///     A fourth death animation, and a player's: 128031, on two records, both on the same character and both with
		///     field 12 at 300. One of the two lands 8 s before that session's own "got incinerated by lava" annotation -
		///     inside the normal annotation lag - while the plain "died" annotation two minutes earlier maps to an ordinary
		///     41877 death, so an environmental death appears to play its own animation. Two records is not enough to wire a
		///     rule on, and nothing selects this one. [V0: what selects the 128031 death animation]
		/// </summary>
		public const uint EnvironmentalDeathBehaviorId = 128031;

		/// <summary>
		///     The fallback animation length in centiseconds for a creature whose own length has not been harvested.
		/// </summary>
		/// <remarks>
		///     Field 12 (ActivationTime) is per-animation rather than per-kill and reads as the animation's own length:
		///     humanoid 2.40 s, quadruped/beast 1.00 s, the environmental one 3.00 s. Each of those is invariant across every
		///     captured record of that animation (166 / 32 / 2 records).
		///     <para>
		///         The demon/large-caster animation is the exception, and its spread belongs to the creature (--report
		///         death-timing): it takes eight values over 52 records, and resolving every actor against its own capture's
		///         spawns gives 22 creatures, each carrying exactly one value - Valley Yeti 816, Enslaved Demon 592, Bat Demon
		///         360, Harvester 240, Necrotic Bomb 220, Striped Boar and Wild Boar 176, Wild Cow and Wild Pig 160, Moose 120,
		///         and a 280 cluster of ten casters and crawlers.
		///     </para>
		///     <para>
		///         A creature the corpus measured therefore carries its own length in NpcTemplate.DeathActivationTime and only
		///         an un-harvested one falls through to the modal value here, which remains correct for the humanoid and
		///         quadruped families since both are invariant.
		///     </para>
		/// </remarks>
		public static uint ActivationTimeFor(uint deathBehaviorId)
		{
			return deathBehaviorId switch
			{
				215333 => 100, // quadruped / beast - 32 of 32
				17036 => 280, // demon / large caster - modal, 30 of 52
				EnvironmentalDeathBehaviorId => 300, // 2 of 2
				_ => 240 // humanoid biped - 166 of 166, and the default for an un-harvested creature
			};
		}

		/// <summary>The self-applied "you are dead" effect's spell id.</summary>
		public const uint DeathEffectSpellId = 0x0009F000;

		/// <summary>The BCC seq both records carry.</summary>
		private const ushort DeathSeq = 0x0004;

		/// <summary>The revive tail's leading word.</summary>
		private const byte ReviveTailPrefix = 0x03;

		/// <summary>The revive's args.</summary>
		private static readonly byte[] ReviveArgs = new CommandMessage
		{
			BccActionId = 41368, // 0x98 0xC3 0x02
			CommandqueueServerId = 4,
			Flags = 8,
			ExecutionStatus = 0,
			ActivationTime = 100
		}.ToProtobuf();

		/// <summary>
		///     The death-behaviour record that transitions a dying SimpleChar - player or NPC - into the dead state and plays
		///     its death animation.
		/// </summary>
		/// <remarks>
		///     Retail sends it to the dying player right after the death-effect ApplySpell (Stihetfv beach capture packet
		///     1292), which is what drives bcc_state 29. A character logging in dead gets bcc_state 29 from its spawn, but an
		///     in-session death has no spawn re-send, so this record is how the live SimpleChar transitions. It also sends it
		///     for every dying NPC: in NecroL80_..._casting-combat every kill is a death-pose MoveTask plus this record, with
		///     no SimpleCharKilledBy at all.
		///     <para>
		///         <paramref name="commandQueueId" /> is the dying entity's own behaviour counter - see
		///         <see cref="CapturedNpcDeathCounter" />. <paramref name="activationTime" /> is the creature's own measured
		///         animation length; null falls back to the animation's modal value (<see cref="ActivationTimeFor" />). Field
		///         5 at 8 and field 6 at 0 are the only genuine constants: all 272 captured records carry them.
		///     </para>
		/// </remarks>
		public static BehaviorControlCenterIIR Bcc(uint handle, uint deathBehaviorId = HumanoidDeathBehaviorId, uint commandQueueId = CapturedPlayerDeathCounter,
			uint? activationTime = null)
		{
			var args = new CommandMessage
			{
				BccActionId = deathBehaviorId, // the creature's death animation
				CommandqueueServerId = commandQueueId,
				Flags = 8,
				ExecutionStatus = 0,
				ActivationTime = activationTime ?? ActivationTimeFor(deathBehaviorId)
			}.ToProtobuf();

			return new BehaviorControlCenterIIR(handle, DeathSeq, (uint)BehaviorId.LimboDeath, args, []);
		}

		/// <summary>
		///     The self-applied death-state effect. Decoded, and deliberately not sent.
		/// </summary>
		/// <remarks>
		///     Retail's AssassinL23 death on map 2010 carries it, but the retail beach death does not, and the spell's client
		///     RDB effect sets stat 0x546 - the wave-respawn timer - to 15, which puts the client into the wave countdown.
		///     The factory and its golden test stay because the bytes are proven and the record is part of the decoded death
		///     grammar, but nothing exposes a one-call path to sending it. If a capture ever justifies sending it, gate it on
		///     the map or the kind of death, not on retail having it somewhere.
		///     <para>
		///         applyTo and applyFrom are both the dying player. Decoded from AssassinL23_..._resurect packet 4051: the
		///         handles are rewritten to the live one and the data region kept verbatim, since the effect's magnitude and
		///         duration fields are not decoded.
		///     </para>
		/// </remarks>
		public static MagicBuffIIR Effect(uint dyingHandle)
		{
			return new MagicBuffIIR(dyingHandle, 0x00, MagicBuffIds.BaseOf(DeathEffectSpellId), MagicBuffIds.FormOf(DeathEffectSpellId), dyingHandle, EffectData);
		}

		/// <summary>
		///     The death-pose MoveTask retail forces onto the dying entity's own handle right before the kill record
		///     (AssassinL23_..._resurect packet 4048): a minimal stationary MoveTask at the death position, with no waypoint
		///     or speed payload. It is required - the kill record, the release screen and the death effect on their own leave
		///     the avatar standing and the release UI closed.
		/// </summary>
		public static MoveTaskIIR Pose(uint dyingHandle, Vector3 position)
		{
			return new MoveTaskIIR(dyingHandle, position, MoveTaskNodes.Stationary());
		}

		/// <summary>
		///     The revive action retail sends on RespawnMe: a behaviour record on the player that transitions bcc_state from
		///     29 back to 1, closing the release window and reviving in place.
		/// </summary>
		/// <remarks>
		///     Decoded from the retail beach death and resurrect (Stihetfv packet 1382, identical in AssassinL23 packet 4840)
		///     and carried verbatim apart from the player handle and the chosen resurrection-point id. Retail follows it with
		///     a vitals restore, which we send as PushVitals, and an IntendedTeleport to the point; the beach point is
		///     adjacent, so we revive in place instead.
		/// </remarks>
		public static BehaviorControlCenterIIR Revive(uint handle, uint respawnPointId)
		{
			using var tail = new FaolanStream();
			tail.WriteUInt32(ReviveTailPrefix).WriteUInt32(respawnPointId);
			return new BehaviorControlCenterIIR(handle, DeathSeq, (uint)BehaviorId.Revive, ReviveArgs, tail.ToArray());
		}

		private static readonly byte[] EffectData =
		[
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xBF, 0x20, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0xF0, 0x00, 0x00, 0x00, 0x17,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00, 0x00, 0x00, 0x14, 0xEF, 0x17, 0x7D
		];
	}
}
