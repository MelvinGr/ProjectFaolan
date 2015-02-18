using System.Collections.Generic;
using System.Data;
using LibFaolan.Extentions;
using MySql.Data.MySqlClient;

namespace LibFaolan.Database
{
    // http://www.codeproject.com/Articles/43438/Connect-C-to-MySQL
    public class MySqlDatabase : IDatabase
    {
        private readonly MySqlConnection _connection;

        public MySqlDatabase(string server, uint port, string database, string user, string password)
        {
            _connection = new MySqlConnection("SERVER=" + server + ";PORT=" + port + ";DATABASE=" +
                                              database + ";UID=" + user + ";PASSWORD=" + password + ";");
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

        public object ExecuteReader(string query)
        {
            return new MySqlCommand(query, _connection).ExecuteReader();
        }

        public T ExecuteScalar<T>(string query)
        {
            return (T) new MySqlCommand(query, _connection).ExecuteScalar();
        }

        public IEnumerable<Dictionary<string, dynamic>> ExecuteDynamic(string query)
        {
            using (var command = new MySqlCommand(query, _connection))
            {
                using (var reader = command.ExecuteReader())
                {
                    foreach (IDataRecord record in reader)
                        yield return record.FieldsToDictionary();
                }
            }
        }

        //////////////////////////////////////////////////////////////////////////////////////////////

        public bool CheckLogin(string username, string password)
        {
            return ExecuteScalar<object>("SELECT account_id FROM accounts WHERE username = '" + username +
                                         "' AND password = '" + password + "'") != null;
        }
    }
}