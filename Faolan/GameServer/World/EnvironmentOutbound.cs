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
		public Task SendEnvironmentalDamage(INetworkClient client, uint targetHandle, uint amount, uint statId, uint school)
		{
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(StatDamageCodec.Build(StatDamages.Environmental(targetHandle, amount, statId, school)))));
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
