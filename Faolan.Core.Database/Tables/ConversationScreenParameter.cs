using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One <parameter value="7;{inner}" name="{Name}" /> of a ConversationScreen (#188). InnerRaw is the once-unescaped
	// inner element - a <localized .../> or <stringentry .../> - which RemoteFormatXml.Build re-escapes into the
	// parameter value. The "name" / "guildname" parameters are the live player substitution slots (regenerated at
	// send time); the rest (localized grammar tokens, npcname/npcguildname) are fixed dialogue content.
	public class ConversationScreenParameter : BaseEntity
	{
		public uint ConversationScreenId { get; set; }

		[ForeignKey(nameof(ConversationScreenId))]
		public virtual ConversationScreen Screen { get; set; } = null!;

		public int Sequence { get; set; } // order within the screen's parameter list

		[MaxLength(512)]
		public string InnerRaw { get; set; } = "";

		[MaxLength(64)]
		public string Name { get; set; } = "";
	}
}
