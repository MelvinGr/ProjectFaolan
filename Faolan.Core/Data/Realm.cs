using System.ComponentModel.DataAnnotations;
using Faolan.Core.Enums;

namespace Faolan.Core.Data
{
    public class Realm : BaseObject
    {
        [MaxLength(128)]
        public string Name { get; set; }

        [MaxLength(64)]
        public string IpAddress { get; set; }

        public ushort Port { get; set; }

        public RealmCountry Country { get; set; }
        public bool FullStatus { get; set; }
        public RealmLoadStatus LoadStatus { get; set; }
        public RealmOnlineStatus OnlineStatus { get; set; }
        public ushort Type { get; set; } // realmtype - 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction
    }
}