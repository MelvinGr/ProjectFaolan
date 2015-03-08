using System.Data.Common;
using MySql.Data.MySqlClient;

namespace LibFaolan.Database
{
    public sealed class MySqlDatabase : IDatabase
    {
        private readonly MySqlConnection _connection;

        public MySqlDatabase(string server, uint port, string database, string user, string password)
        {
            _connection = new MySqlConnection("SERVER=" + server + ";PORT=" + port + ";DATABASE='" +
                                              database + "';UID='" + user + "';PASSWORD='" + password + "';");
        }

        public bool Connect()
        {
            try
            {
                _connection.Open();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool IsConnected()
        {
            return _connection.Ping();
        }

        public void Disconnect()
        {
            _connection.Close();
        }

        public int ExecuteNonQuery(string query)
        {
            return new MySqlCommand(query, _connection).ExecuteNonQuery();
        }

        public DbDataReader ExecuteReader(string query)
        {
            return new MySqlCommand(query, _connection).ExecuteReader();
        }

        public T ExecuteScalar<T>(string query)
        {
            return (T) new MySqlCommand(query, _connection).ExecuteScalar();
        }
    }
}