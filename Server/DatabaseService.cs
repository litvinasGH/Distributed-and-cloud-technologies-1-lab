using Npgsql;

namespace Server;

/// <summary>Создание подключений к PostgreSQL и ожидание готовности БД.</summary>
public sealed class DatabaseService
{
    public string ConnectionString { get; }

    public DatabaseService()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out int port) ? port : 5432,
            Database = Environment.GetEnvironmentVariable("DB_NAME") ?? "fitness_db",
            Username = Environment.GetEnvironmentVariable("DB_USER") ?? "fitness_user",
            Password = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "fitness_pass",
            Timeout = 5,
            CommandTimeout = 15,
            Pooling = true
        };

        ConnectionString = builder.ConnectionString;
    }

    public NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public void WaitUntilAvailable()
    {
        const int attempts = 30;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                using var command = new NpgsqlCommand("SELECT 1", connection);
                command.ExecuteScalar();
                Console.WriteLine("Подключение к PostgreSQL успешно.");
                return;
            }
            catch (NpgsqlException ex)
            {
                if (attempt == attempts) throw;
                Console.WriteLine($"Ожидание PostgreSQL ({attempt}/{attempts}): {ex.Message}");
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }
}
