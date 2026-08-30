using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Same one path, reached from the other side (#681): a no-mount volume unseats through exactly the record pair
		// the button press and a thrown rider do.
		Task IEnvironmentOutbound.SendDismount(INetworkClient client)
		{
			return SendDismount(client);
		}

		// IEnvironmentOutbound (#495/#504). The StatDamageIIR ENVIRONMENTAL form - no caster, no spell, and NO paired
		// DamageResult, because retail renders no floating combat number for a fall (CombatEffectBuilder).
		Task IEnvironmentOutbound.SendEnvironmentalDamage(INetworkClient client, uint targetHandle, uint amount, uint statId, uint school)
		{
			return client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(StatDamageCodec.Build(StatDamages.Environmental(targetHandle, amount, statId, school)))));
		}

		Task IEnvironmentOutbound.PushVitals(INetworkClient client, EntityVitals vitals)
		{
			// #586: every caller of this - the regen tick, fall damage, lava, drowning - moves a CURRENT and nothing
			// else, which is the case retail answers with the three-record currents-only block.
			//
			// This used to delegate to the listener's own IWorldOutbound.PushVitals. That port moved to WorldOutbound
			// (#826) and the cast `((IWorldOutbound)this)` went with it: it still COMPILED, because the class is not
			// sealed, and would have thrown InvalidCastException the first time an environmental hit landed.
			return SendStatUpdate(client, vitals.ToCurrentsBlock(), client.Session.Character.Id);
		}

		Task IEnvironmentOutbound.ApplySelfBuff(INetworkClient client, uint wireSpellId)
		{
			return ((ISpellEffectOutbound)this).ApplySelfBuff(client, wireSpellId);
		}
	}
}
