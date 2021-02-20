using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Data
{
    public class NpcEquipment : BaseObject
    {
        [ForeignKey(nameof(Npc))]
        public uint NpcId { get; set; }

        public virtual Npc Npc { get; set; }

        public virtual Item Neck { get; set; }
        public virtual Item Head { get; set; }
        public virtual Item Tokens { get; set; }
        public virtual Item Chest { get; set; }
        public virtual Item MainHandLeft { get; set; }
        public virtual Item MainHandRight { get; set; }
        public virtual Item Belt { get; set; }
        public virtual Item OffHandLeft { get; set; }
        public virtual Item OffHandRight { get; set; }
        public virtual Item RingLeft { get; set; }
        public virtual Item RingRight { get; set; }
        public virtual Item Cloak { get; set; }
        public virtual Item Shoulder { get; set; }
        public virtual Item Boots { get; set; }
        public virtual Item Hands { get; set; }
        public virtual Item Legs { get; set; }
        public virtual Item Wrist { get; set; }
        public virtual Item FirstPosBag { get; set; }
    }
}