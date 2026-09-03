using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Constants;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.Progression
{
	// Achievement TAGS (#748): granting one to a character and telling the client about it, once. Off the listener
	// because two callers need it - the vista award in the listener and the `.tag` GM command in GmCommandService -
	// and a service cannot take a listener method (#928 item 5).
	public sealed class TagService(IDatabaseRepository database)
	{
		// Awards a TAG (#748): `DispatchAddTag`, body = the tag id and nothing else, 4 bytes. That covers two
		// phenomena sharing one id space - an ACHIEVEMENT (the catalog is LDB 55701/55700, imported into
		// `Achievements`: 7917 "Seeing Red" / *"Achieve a kill streak."*, 716 "Old Tarantia Docks") and the item-shop
		// "bundle shown" flags whose `HasTag` gates live in the client's own shop Lua (6617-6620, 7316, 7416-7421).
		// [V2: NecroL80_..._gameplay_annotated_2 t+285.5 tag 0x2CC; 23 awards over 14 captures]
		//
		// ⚠️ There is no login re-send, and nothing needs one: NOT ONE of the 23 captured awards lands in a login
		// burst, because the client learns the set it already holds from the FullChar snapshot (SendBurstlessWorldEntry
		// unions `GetCharacterTags` into it). So this message is strictly the moment-of-award event, which is why the
		// caller must not send it for a tag the character already has.
		//
		// A REMOVE twin exists in the client's method table - `DispatchRemoveTag` (0x17) - with zero captured frames, so
		// nothing is built for it.
		private static Task SendAddTag(INetworkClient client, uint tagId)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddTag,
				b => b.WriteUInt32(tagId)));
		}

		// Grants a tag and tells the client, once. Persists first (so a relog keeps it, via the snapshot) and only
		// sends the award if the row was new - re-awarding a held tag would pop the client's banner again.
		// Returns the achievement it named, or null for an id the client's catalog does not carry (an item-shop flag,
		// or an entry the catalog is short of); false in `Granted` means they already had it.
		public async Task<(bool Granted, Achievement? Achievement)> GrantTag(INetworkClient client, uint tagId)
		{
			var granted = await database.AddCharacterTag(client.Session.Character.Id, tagId);
			if (granted)
				await SendAddTag(client, tagId);

			return (granted, await database.GetAchievement(tagId));
		}
	}
}
