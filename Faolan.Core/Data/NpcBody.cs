using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Data
{
    public class NpcBody : BaseEntity
    {
        [ForeignKey(nameof(Npc))]
        public uint NpcId { get; set; }

        public virtual Npc Npc { get; set; }

        public uint BodyMesh { get; set; }
        public uint HeadMesh { get; set; }
        public uint HairMesh { get; set; }
        public uint BeardMesh { get; set; }
        public uint Size { get; set; }
    }
}