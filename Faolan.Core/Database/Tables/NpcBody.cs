using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	public class NpcBody : BaseEntity
	{
		[ForeignKey(nameof(NpcId))]
		public virtual Npc Npc { get; set; }

		public uint BeardMesh { get; set; }

		public uint BodyMesh { get; set; }
		public uint HairMesh { get; set; }
		public uint HeadMesh { get; set; }
		public uint NpcId { get; set; }
		public uint Size { get; set; }
	}
}
