using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// An ambient "chatter" bark an NPC TYPE periodically says (CharacterChat 0x0022 local-say, a chat bubble over the
	// NPC). Keyed by NpcTemplateId (the NPC type), like NpcSpell. The dialogue is SPECIFIC per NPC - each NPC references its
	// own conversation group; they do NOT share one table - so lines are stored per NPC type.
	//
	// Harvested from captures by Faolan.Importer (NpcChatterModule): the bark is a
	// `<remoteformat token="sayinchat(N)|conversationM(K)|<Group>" category="50015" key="..." knubot="0" >` the client
	// resolves to a localized line. `RemoteFormat` is that XML verbatim - the client needs the exact token + per-line
	// key, so we store + replay it byte-for-byte rather than reconstructing it.
	public class NpcChatter : BaseEntity
	{
		// The NPC TYPE that says this line (loader reads via NpcTemplate.Chatter). A bark whose speaker has no NpcTemplate type is dropped.
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The verbatim <remoteformat> XML sent as the CharacterChat (0x0022) message. ~150 chars in practice (the
		// token + group name + per-line key); 512 leaves generous headroom for longer groups/keys.
		[MaxLength(512)]
		public string RemoteFormat { get; set; } = null!;
	}
}
