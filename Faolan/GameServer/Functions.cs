namespace Faolan.GameServer
{
    partial class GameServerListener
    {
        public void SaveAllCharacterData()
        {
            foreach (var client in NetworkClients)
                client.Account.Character.SaveDataToDatabase(Database);
        }
    }
}