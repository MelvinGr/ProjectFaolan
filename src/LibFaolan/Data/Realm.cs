using System;

namespace LibFaolan.Data
{
    public sealed class Realm
    {
        public UInt32 Country; //Country: 0 England, 1 France, 2 Spain, 3 German
        public UInt32 FullStatus; // server full status 1 = full
        public string IpAddress;
        public UInt32 LoadStatus; //load status -- 0 = medium load 1 = medium load 2 = heavy load 3 = full
        public UInt32 OnlineStatus; // onlineStatus - 2 = online, other num = offline
        public UInt32 Port;
        public UInt32 RealmId;
        public string RealmName;
        public UInt16 RealmType; // realmtype - 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask
    }
}