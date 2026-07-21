using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One `remoteformat` dialogue screen of a ConversationPacket (#188 columnization): the STRUCTURED form of the
	// captured <remoteformat id="I" category="C" key="K" knubot="N" {antitag="A"} > ... </remoteformat> XML, so the
	// runtime builds the wire XML from typed fields (RemoteFormatXml.Build / ConversationScreenColumns.ToScreen)
	// instead of storing it as text. A Knubot packet carries an ordered list of these (0..N; OpenChatWindow has
	// none). Byte-exact vs the captured screen (ConversationConstructTests). The live player name/guild are
	// regenerated into the "name"/"guildname" parameters at send time (RemoteFormatXml.WithLivePlayer).
	public class ConversationScreen : BaseEntity
	{
		public uint ConversationPacketId { get; set; }

		[ForeignKey(nameof(ConversationPacketId))]
		public virtual ConversationPacket Packet { get; set; } = null!;

		public uint ScreenId { get; set; } // remoteformat @id (0 for the close screen)

		public virtual ICollection<ConversationScreenParameter> Parameters { get; set; } = [];

		public int Sequence { get; set; } // order within the packet's screen list

		[MaxLength(128)]
		public string Key { get; set; } = ""; // @key - the harvested, client-validated content hash (carried as-escaped)

		public uint Category { get; set; } // @category

		public uint Knubot { get; set; } // @knubot
		public uint? Antitag { get; set; } // @antitag (null = attribute absent -> the "knubot=\"0\"  >" double-space slot)
	}
}
