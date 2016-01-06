namespace LibFaolan.Data
{
    public sealed class Realm
    {
        public byte Country; //Country: 0 England, 1 France, 2 Spain, 3 German
        public byte FullStatus; // server full status 1 = full
        public byte Id;
        public string IpAddress;
        public byte LoadStatus; //load status -- 0 = medium load 1 = medium load 2 = heavy load 3 = full
        public string Name;
        public byte OnlineStatus; // onlineStatus - 2 = online, other num = offline
        public ushort Port;
        public ushort Type; // realmtype - 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask

        public override string ToString()
        {
            return Id + ": " + Name;
        }
    }
}