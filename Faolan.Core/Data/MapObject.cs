using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Data
{
    public class MapObject : Base3DEntity
    {
        [ForeignKey(nameof(Map))]
        public uint MapId { get; set; }

        public virtual Map Map { get; set; }

        public uint Data0 { get; set; }
        public uint Data1 { get; set; }
        public uint Data2 { get; set; }
        public uint Data3 { get; set; }
        public uint Data4 { get; set; }
        public uint Data5 { get; set; }
        public string Sdat1 { get; set; }
        public string Sdat2 { get; set; }
        public uint Unk0 { get; set; }
        public uint Unk1 { get; set; }
    }
}