using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Data
{
    public class CharacterEquipment : BaseEntity
    {
        [ForeignKey(nameof(Character))]
        public uint CharacterId { get; set; }

        public virtual Character Character { get; set; }

        public virtual Item Neck { get; set; }
        public virtual Item Head { get; set; }
        public virtual Item Tokens { get; set; }
        public virtual Item Chest { get; set; }
        public virtual Item MainHandLeft { get; set; }
        public virtual Item MainHandRight { get; set; }
        public virtual Item OffHandLeft { get; set; }
        public virtual Item OffHandRight { get; set; }
        public virtual Item Belt { get; set; }
        public virtual Item RingLeft { get; set; }
        public virtual Item RingRight { get; set; }
        public virtual Item Cloak { get; set; }
        public virtual Item Shoulder { get; set; }
        public virtual Item Boots { get; set; }
        public virtual Item Hands { get; set; }
        public virtual Item Legs { get; set; }
        public virtual Item FirstPosBag { get; set; }
        public virtual Item Wrist { get; set; }
        public string Bag { get; set; }
        public string Quest { get; set; }
        public string Resources { get; set; }
    }

    /*public class CharacterAbility : BaseObject
    {
        public string Name { get; set; }

        public uint? Level { get; set; }

        public uint? Data0 { get; set; }

        public uint? Data1 { get; set; }

        public uint? Data2 { get; set; }

        public uint? Data3 { get; set; }

        public uint? Data4 { get; set; }
    }*/
}