using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;

namespace Faolan.Core.Data
{
    public class Character : Base3DEntity
    {
        [MaxLength(128)]
        public string Name { get; set; }

        public Class Class { get; set; }
        public ushort Level { get; set; }
        public Sex Sex { get; set; }
        public Race Race { get; set; }
        public uint Experience { get; set; }

        [MaxLength(4)]
        public string Language { get; set; }

        public DateTime? LastConnection { get; set; }
        public DateTime CreationDate { get; set; }

        [MaxLength(45)] // INET6_ADDRSTRLEN 46
        public string LastIpAddress { get; set; }

        [ForeignKey(nameof(Map))]
        public uint? MapId { get; set; }

        public virtual Map Map { get; set; }

        [ForeignKey(nameof(Account))]
        public uint AccountId { get; set; }

        public virtual Account Account { get; set; }

        [ForeignKey(nameof(Realm))]
        public uint? RealmId { get; set; }

        public virtual Realm Realm { get; set; }

        public virtual CharacterBody Body { get; set; }
        public virtual CharacterEquipment Equipment { get; set; }

        //[NotMapped]
        //public string LastConnectionRealmString => DateTimeOffset.FromUnixTimeSeconds(LastConnection).ToString("MM/dd/yyyy HH:mm:ss");

        public override string ToString()
        {
            return $"{base.ToString()}: {Name}";
        }
    }
}