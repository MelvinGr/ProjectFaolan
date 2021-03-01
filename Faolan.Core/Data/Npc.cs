using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Data
{
    public class Npc : Base3DEntity
    {
        [MaxLength(255)]
        public string Name { get; set; }

        //public uint NameId { get; set; }
        public uint Action { get; set; }
        public ushort Level { get; set; }
        public uint Fraction { get; set; }
        public uint Health { get; set; }
        public uint Stamina { get; set; }
        public uint Mana { get; set; }
        public uint GetExp { get; set; }
        public string Extras { get; set; }

        [ForeignKey(nameof(Map))]
        public uint MapId { get; set; }

        public virtual Map Map { get; set; }
        public virtual NpcBody Body { get; set; }
        public virtual NpcEquipment Equipment { get; set; }
    }
}