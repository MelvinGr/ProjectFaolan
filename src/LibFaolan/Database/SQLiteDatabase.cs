using System.Data;
using System.Data.Common;
using System.Data.SQLite;
using LibFaolan.Other;

namespace LibFaolan.Database
{
    public sealed class SqLiteDatabase : IDatabase
    {
        private readonly SQLiteConnection _connection;

        public SqLiteDatabase(string path, Logger logger)
        {
            Logger = logger;
            _connection = new SQLiteConnection("Data Source='" + path + "';Version=3;");
        }

        public Logger Logger { get; }

        public bool Connect()
        {
            try
            {
                _connection.Open();
                Logger.Info("Connected");
                return true;
            }
            catch
            {
                Logger.Info("Could not connect");
                return false;
            }
        }

        public void Disconnect()
        {
            _connection.Close();
        }

        public bool IsConnected()
        {
            return _connection.State == ConnectionState.Open || _connection.State == ConnectionState.Executing;
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

        public long LastInsertRowID()
            => (long) new SQLiteCommand("SELECT last_insert_rowid()", _connection).ExecuteScalar();
    }
}