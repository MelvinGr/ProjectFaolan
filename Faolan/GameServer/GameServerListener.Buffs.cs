using System;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;

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
			return await _pets.SummonPetForPlayer(client, template, combat, pointCost) != null;
		}

		Task ISpellEffectOutbound.SummonTimedPet(INetworkClient client, string template, TimeSpan duration, TimeSpan? cap, uint pointCost)
		{
			return _pets.SummonTimedPetForPlayer(client, template, duration, cap, pointCost);
		}

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// tools/capture/decode_learn_spell.py, byte-exact in LearnSpellTests). The client pops the "learned a new spell"
		// toast itself. (The sibling DispatchForgetSpell 0x200B / RemoveSpecialAbility 0x2053 / UntrainSingleFeat
		// 0x2011 have no capture oracle yet, so they are not built - see #73.)
		internal static Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchLearnSpell,
				LearnSpellCodec.Build(spellId)));
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
	}
}
