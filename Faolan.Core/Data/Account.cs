using Faolan.Core.Database;
using Faolan.Core.Network;

namespace Faolan.Core.Data
{
    public sealed class Account
    {
        public readonly string AuthChallenge = "2bac5cd78ee0e5a37395991bfbeeeab8";
        public uint AuthStatus;
        public Character Character;
        public uint ClientInstance;
        public int CreateCounter;
        public int CreateState;
        public uint Id; // PlayerInstance
        public string Name;
        public AccountState State;
        public AccountType Type;
        public ulong LongId => 0x0000271200000000u + Id; // As used by the client (why?)

        public bool IsBanned { get; set; }

        public void LoadDetailsFromDatabase(IDatabase database)
        {
            /*Dictionary<string, dynamic> obj;
            if (Id != 0)
                obj = database.ExecuteReader($"SELECT * FROM accounts WHERE account_id={Id}").ToDictionary();
            else if (Name != null)
                obj = database.ExecuteReader($"SELECT * FROM accounts WHERE username=\'{Name}\'").ToDictionary();
            else
                throw new Exception("Id == 0 && Username == null");

            if (obj.Count == 0)
                throw new Exception("obj.Count == 0");

            Id = (uint) obj["account_id"];
            Name = obj["username"];
            Type = (AccountType) obj["type"];
            State = (AccountState) obj["state"];*/
        }

        public bool CheckLogin(IDatabase database, string password)
        {
            return true; //database.ExecuteScalar<object>("SELECT account_id FROM accounts " +
            //$"WHERE username = \'{Name}\' AND password = \'{password}\'") != null;
        }

        //(IDatabase database) => database.ExecuteScalar<dynamic>("SELECT state FROM accounts WHERE account_id=" + Id) == 1;

        public Character[] GetCharacters(IDatabase database)
        {
            //var chars = database.ExecuteReader("SELECT character_id FROM characters WHERE account_id=" + Id)
            //    .ToIEnumerable().Select(c => new Character((uint) c["character_id"])).ToArray();

            //foreach (var c in chars)
            //    c.LoadDetailsFromDatabase(database);

            return null;
        }

        public bool UpdateLastInfo(IDatabase database, INetworkClient client)
        {
            return true; //database.ExecuteNonQuery($"UPDATE accounts SET last_connection={Functions.Time()}," +
            //$"last_ip=\'{client.IpAddress}\'WHERE account_id={Id}") == 1;
        }

        public override string ToString()
        {
            return $"{Id}: {Name}";
        }
    }
}