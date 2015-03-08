using System.Collections.Generic;
using System.Linq;
using LibFaolan.Data;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        public void SaveAllCharacterData()
        {
            foreach (var account in Accounts)
                account.Character.SaveDataToDatabase(Database);
        }
    }
}