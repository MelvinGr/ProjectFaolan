using System;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The wire for NON-COMBAT damage - fall, lava, drowning (#495/#504) - off GameServerListener (#826).
	/// </summary>
	/// <remarks>
	///     ⚠️ PushVitals is written out here rather than delegating. It used to forward through
	///     <c>((IWorldOutbound)this)</c>, and when that port left the listener the cast still COMPILED and would have
	///     thrown on the first environmental hit (fixed in d66482f1). ApplySelfBuff likewise calls BuffService directly
	///     instead of bouncing through a second port on the listener.
	/// </remarks>
	internal sealed class EnvironmentOutbound : IEnvironmentOutbound
	{
		private readonly BuffService _buffs;

		// Dismount spans the listener's own state - it persists the mount and pushes the viewer broadcast - so it stays
		// there behind a single-op seam, the same one WorldOutbound holds.
		private readonly Func<INetworkClient, Task> _sendDismount;

		public EnvironmentOutbound(BuffService buffs, Func<INetworkClient, Task> sendDismount)
		{
			_buffs = buffs;
			_sendDismount = sendDismount;
		}

		// Same one path, reached from the other side (#681): a no-mount volume unseats through exactly the record pair
		// the button press and a thrown rider do.
		public Task SendDismount(INetworkClient client)
		{
			return _sendDismount(client);
		}
		// IEnvironmentOutbound (#495/#504). The StatDamageIIR ENVIRONMENTAL form - no caster, no spell, and NO paired
		// DamageResult, because retail renders no floating combat number for a fall (CombatEffectBuilder).
		//
		// ⚠️ `displayAmount` is in DISPLAY units, like everything on EntityVitals, and the record carries the WIRE
		// value - so it is scaled here. Sending it unscaled is a bug you can watch happen: the health BAR moves by the
		// right amount (PushVitals scales, through CharacterStatBlock.Set) while the floating number reads a hundredth
		// of it, i.e. "1" for a fall that actually cost 27 health. The combat path scales at the same point, in
		// CombatService, which is why only the environmental one was wrong.
		public Task SendEnvironmentalDamage(INetworkClient client, uint targetHandle, uint displayAmount, uint statId, uint school)
		{
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(StatDamageCodec.Build(StatDamages.Environmental(targetHandle, WireAmount(displayAmount, statId), statId, school)))));
		}

		/// <summary>
		///     A display amount at the stat's own wire scale - the conversion the record needs and
		///     <see cref="EntityVitals" /> does not carry.
		/// </summary>
		/// <remarks>
		///     Per STAT rather than a flat 100, because that is what the client applies: `StatDisplayScale.Of` is the
		///     same table <c>CharacterStatBlock.Set</c> goes through for the vitals push beside this record, so the two
		///     halves of a fall cannot disagree about units again.
		/// </remarks>
		internal static uint WireAmount(uint displayAmount, uint statId)
		{
			return displayAmount * StatDisplayScale.Of(statId);
		}
		public Task PushVitals(INetworkClient client, EntityVitals vitals)
		{
			// #586: every caller of this - the regen tick, fall damage, lava, drowning - moves a CURRENT and nothing
			// else, which is the case retail answers with the three-record currents-only block.
			//
			// This used to delegate to the listener's own IWorldOutbound.PushVitals. That port moved to WorldOutbound
			// (#826) and the cast `((IWorldOutbound)this)` went with it: it still COMPILED, because the class is not
			// sealed, and would have thrown InvalidCastException the first time an environmental hit landed.
			return GameWire.SendStatUpdate(client, vitals.ToCurrentsBlock(), client.Session.Character.Id);
		}
		public Task ApplySelfBuff(INetworkClient client, uint wireSpellId)
		{
			var self = client.Session.Character.Id;
			return _buffs.ApplySpell(client, self, self, wireSpellId);
		}
	}
}
