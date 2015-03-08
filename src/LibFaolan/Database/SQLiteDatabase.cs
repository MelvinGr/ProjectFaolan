using System.Data;
using System.Data.Common;
using System.Data.SQLite;

namespace LibFaolan.Database
{
    public sealed class SqLiteDatabase : IDatabase
    {
        private readonly SQLiteConnection _connection;

        public SqLiteDatabase(string path)
        {
            _connection = new SQLiteConnection("Data Source='" + path + "';Version=3;");
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

        public void Disconnect()
        {
            _connection.Close();
        }

        public bool IsConnected()
        {
            return (_connection.State == ConnectionState.Open || _connection.State == ConnectionState.Executing);
        }

        public int ExecuteNonQuery(string query)
        {
            return new SQLiteCommand(query, _connection).ExecuteNonQuery();
        }

        public DbDataReader ExecuteReader(string query)
        {
            return new SQLiteCommand(query, _connection).ExecuteReader();
        }

        public T ExecuteScalar<T>(string query)
        {
            return (T) new SQLiteCommand(query, _connection).ExecuteScalar();
        }
    }
}