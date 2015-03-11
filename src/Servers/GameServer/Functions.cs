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