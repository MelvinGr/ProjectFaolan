using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// An ambient "chatter" bark an NPC TYPE periodically says (CharacterChat 0x0022 local-say, a chat bubble over the
	// NPC). Keyed by NpcId (the NPC type), like NpcSpell. The dialogue is SPECIFIC per NPC - each NPC references its
	// own conversation group; they do NOT share one table - so lines are stored per NPC type.
	//
	// Harvested from captures by Faolan.Importer (NpcChatterModule): the bark is a
	// `<remoteformat token="sayinchat(N)|conversationM(K)|<Group>" category="50015" key="..." knubot="0" >` the client
	// resolves to a localized line. `RemoteFormat` is that XML verbatim - the client needs the exact token + per-line
	// key, so we store + replay it byte-for-byte rather than reconstructing it.
	public class NpcChatter : BaseEntity
	{
		// The NPC TYPE that says this line (loader reads via Npc.Chatter). A bark whose speaker has no Npc type is dropped.
		public uint NpcId { get; set; }

		[ForeignKey(nameof(NpcId))]
		public virtual Npc Npc { get; set; } = null!;

		// The verbatim <remoteformat> XML sent as the CharacterChat (0x0022) message.
		public string RemoteFormat { get; set; } = null!;
	}
}
