using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;

namespace Faolan.Core.Data
{
    public class Realm : BaseEntity
    {
        [MaxLength(128)]
        public string Name { get; set; }

        [MaxLength(64)]
        public string IpAddress { get; set; }

        public ushort Port { get; set; }

        public RealmCountry Country { get; set; }
        public RealmLoadStatus LoadStatus { get; set; }
        public RealmOnlineStatus OnlineStatus { get; set; }
        public RealmType Type { get; set; }

        public virtual ICollection<Character> Characters { get; set; }

        [NotMapped]
        public bool FullStatus => LoadStatus == RealmLoadStatus.Full;
    }
}