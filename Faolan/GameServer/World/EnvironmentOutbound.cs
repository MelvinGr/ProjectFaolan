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
	///     The wire for non-combat damage: fall, lava and drowning.
	/// </summary>
	/// <remarks>
	///     <see cref="PushVitals" /> is written out here rather than delegating through a self-cast to another port,
	///     because such a cast compiles on an unsealed class whether or not the class still implements the interface
	///     and throws only at runtime. <see cref="ApplySelfBuff" /> likewise calls <see cref="BuffService" /> directly
	///     rather than bouncing through a second port.
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

		// The same path reached from the other side: a no-mount volume unseats through exactly the record pair the
		// button press and a thrown rider do.
		public Task SendDismount(INetworkClient client)
		{
			return _sendDismount(client);
		}
		/// <summary>
		///     The StatDamageIIR environmental form: no caster, no spell, and no paired DamageResult, because retail
		///     renders no floating combat number for a fall.
		/// </summary>
		/// <remarks>
		///     <paramref name="displayAmount" /> is in display units, like everything on <see cref="EntityVitals" />,
		///     and the record carries the wire value, so it is scaled here. Unscaled, the health bar moves by the right
		///     amount - the vitals push scales through <c>CharacterStatBlock.Set</c> - while the floating number reads
		///     a hundredth of it. The combat path scales at the same point.
		/// </remarks>
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
		///     Per stat rather than a flat 100, because that is what the client applies: <c>StatDisplayScale.Of</c> is
		///     the same table <c>CharacterStatBlock.Set</c> goes through for the vitals push beside this record, so the
		///     two halves of a fall cannot disagree about units.
		/// </remarks>
		internal static uint WireAmount(uint displayAmount, uint statId)
		{
			return displayAmount * StatDisplayScale.Of(statId);
		}
		public Task PushVitals(INetworkClient client, EntityVitals vitals)
		{
			// Every caller of this - the regen tick, fall damage, lava, drowning - moves a current and nothing else,
			// which is the case retail answers with the three-record currents-only block.
			return GameWire.SendStatUpdate(client, vitals.ToCurrentsBlock(), client.Session.Character.Id);
		}
		public Task ApplySelfBuff(INetworkClient client, uint wireSpellId)
		{
			var self = client.Session.Character.Id;
			return _buffs.ApplySpell(client, self, self, wireSpellId);
		}
	}
}
