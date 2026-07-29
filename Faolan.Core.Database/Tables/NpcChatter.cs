using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// An ambient "chatter" bark an NPC TYPE periodically says (CharacterChat 0x0022 local-say, a chat bubble over the
	// NPC). Keyed by the NPC type, like NpcSpell: the dialogue is SPECIFIC per NPC - each references its own
	// conversation group - so lines are stored per type.
	//
	// COLUMNIZED, like its StockPhrase sibling (#410): the bark is a remoteformat root, usually parameter-less
	//   <remoteformat token="sayinchat(16)|conversation1(3)|RenownBystanderLovers" category="50015" key="..." knubot="0"  >
	// so these columns ARE the whole payload and the wire string is rebuilt with RemoteFormatXml.BuildChatter. The
	// raw blob it replaced is gone. Same root grammar as a dialogue screen, with `token=` where a screen has `id=`.
	//
	// The token decomposes further, and that is what makes the row addressable rather than an opaque string: a line
	// is one `sayinchat` entry within one CHANNEL of a named group. The runtime picks a line at random per bark
	// (WorldSimulation), so the set is what matters, not an order.
	[PrimaryKey(nameof(NpcTemplateId), nameof(Channel), nameof(SayInChat))]
	public class NpcChatter
	{
		// The NPC TYPE that says this line (loader reads via NpcTemplate.Chatter). A bark whose speaker has no
		// NpcTemplate type is dropped.
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The token's SECOND segment, `<channel>(K)` - stored as the channel's literal name because it is not always
		// a numbered conversation: the corpus carries `conversation1`, `conversation2` AND `sendtell` (a crier
		// addressing a detected player), so parsing an index out of it would only work for two of the three forms.
		// Part of the key - a `sayinchat` index is unique only WITHIN a channel (template 14164 says sayinchat(19)
		// in both conversation1 and conversation2).
		[MaxLength(32)]
		public string Channel { get; set; } = null!;

		// The line count the channel declares - the `(K)` in `conversation1(3)`.
		public uint ChannelCount { get; set; }

		// `sayinchat(N)`: the line's index within its conversation group's string table.
		public uint SayInChat { get; set; }

		// The conversation group the token names (e.g. "RenownBystanderLovers"). Shared ACROSS NPC types - 11852 and
		// 14165 carry byte-identical line sets because both are bystander lovers - so this is a normalisation seam:
		// the lines really belong to the group, with types referencing it (open work tracked in Gitea).
		[MaxLength(64)]
		public string Group { get; set; } = null!;

		public uint Category { get; set; } // always 50015 in the captures - the ambient-chatter category
		public uint Knubot { get; set; }

		// The remoteformat's own `key=` attribute: the harvested, client-validated per-line content hash. Stored as
		// the raw ATTRIBUTE TEXT the capture carries, XML entities and all (`&#39;`, `&quot;`) - the client is fed
		// these bytes verbatim and re-escaping is not round-trip safe. Same rule as StockPhrase.FormatKey.
		[MaxLength(64)]
		public string FormatKey { get; set; } = null!;

		// The bark's `<parameter>` slots in render order, empty for most lines (#493). Was a single name-only column
		// on the assumption that a bark carries at most one - the Damsel in Distress line carries two, one of them
		// CONTENT (`helptext`, a `<localized>` element) rather than session state, so it had to become a collection
		// that stores values as well as names. See NpcChatterParameter.
		public virtual ICollection<NpcChatterParameter> Parameters { get; set; } = [];
	}
}
