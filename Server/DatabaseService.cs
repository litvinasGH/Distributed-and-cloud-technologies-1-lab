using System.Data;
using Npgsql;

namespace Server;

/// <summary>
/// Учебная модель Primary/Replica для двух независимых экземпляров PostgreSQL.
/// Изменения переносятся копированием согласованного снимка данных.
/// Это демонстрационная репликация на уровне приложения, а не кластер PostgreSQL.
/// </summary>
public sealed class DatabaseService
{
    private readonly string _primaryConnectionString;
    private readonly string _replicaConnectionString;
    private readonly SemaphoreSlim _replicationLock = new(1, 1);
    private volatile bool _forcePrimaryUnavailable;
    private volatile bool _primaryConnectionFailed;
    private volatile bool _replicaConnectionFailed;

    public bool IsFailoverActive => _forcePrimaryUnavailable || _primaryConnectionFailed;

    public DatabaseService()
    {
        _primaryConnectionString = BuildConnectionString(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost",
            ParsePort(Environment.GetEnvironmentVariable("DB_PORT"), 5432));
        _replicaConnectionString = BuildConnectionString(
            Environment.GetEnvironmentVariable("REPLICA_DB_HOST") ?? "localhost",
            ParsePort(Environment.GetEnvironmentVariable("REPLICA_DB_PORT"), 5433));
    }

    private static int ParsePort(string? value, int fallback) =>
        int.TryParse(value, out int port) ? port : fallback;

    private static string BuildConnectionString(string host, int port)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = Environment.GetEnvironmentVariable("DB_NAME") ?? "fitness_db",
            Username = Environment.GetEnvironmentVariable("DB_USER") ?? "fitness_user",
            Password = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "fitness_pass",
            Timeout = 3,
            CommandTimeout = 15,
            // Для учебной проверки отказа подключение должно обнаруживать остановленный экземпляр, а не возвращать старое соединение из пула.
            Pooling = false
        };
        return builder.ConnectionString;
    }

    /// <summary>Открывает соединение с Primary, а при отказе автоматически использует Replica.</summary>
    public NpgsqlConnection OpenConnection()
    {
        // После обнаруженного отказа остаёмся на Replica, пока оператор не выполнит Recovery.
        if (!_forcePrimaryUnavailable && !_primaryConnectionFailed)
        {
            var primary = new NpgsqlConnection(_primaryConnectionString);
            try
            {
                primary.Open();
                _primaryConnectionFailed = false;
                return primary;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                primary.Dispose();
                _primaryConnectionFailed = true;
                Console.WriteLine($"[{Timestamp()}] Primary → UNAVAILABLE: {ex.Message}");
            }
        }

        try
        {
            var replica = new NpgsqlConnection(_replicaConnectionString);
            replica.Open();
            _replicaConnectionFailed = false;
            Console.WriteLine($"[{Timestamp()}] Failover → запрос направлен в Replica.");
            return replica;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _replicaConnectionFailed = true;
            Console.WriteLine($"[{Timestamp()}] Replica → UNAVAILABLE: {ex.Message}");
            throw new InvalidOperationException(
                "Primary недоступен, и подключение к Replica также не удалось. Запрос не выполнен.", ex);
        }
    }

    public void WaitUntilAvailable()
    {
        WaitForDatabase(_primaryConnectionString, "Primary");
        WaitForDatabase(_replicaConnectionString, "Replica");
        Console.WriteLine($"[{Timestamp()}] Primary и Replica доступны.");

        // При каждом запуске сверяем копии, чтобы начать работу с одинаковыми данными.
        if (!CopyData(_primaryConnectionString, _replicaConnectionString, "INITIAL SYNC", out string error))
            throw new InvalidOperationException($"Не удалось инициализировать Replica: {error}");
    }

    private static void WaitForDatabase(string connectionString, string name)
    {
        const int attempts = 30;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                using var command = new NpgsqlCommand("SELECT 1", connection);
                command.ExecuteScalar();
                Console.WriteLine($"[{Timestamp()}] {name} → AVAILABLE.");
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                if (attempt == attempts) throw;
                Console.WriteLine($"[{Timestamp()}] Ожидание {name} ({attempt}/{attempts}): {ex.Message}");
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>Скопировать Primary в Replica после изменения данных.</summary>
    public bool ReplicatePrimaryToReplica(out string error)
    {
        error = string.Empty;
        if (_forcePrimaryUnavailable || _primaryConnectionFailed)
        {
            Console.WriteLine($"[{Timestamp()}] Replication → пропущена: активный экземпляр — Replica, изменения ожидают Recovery.");
            return true;
        }

        _replicationLock.Wait();
        try
        {
            bool result = CopyData(_primaryConnectionString, _replicaConnectionString, "REPLICATION", out error);
            if (!result)
            {
                _primaryConnectionFailed = true;
                Console.WriteLine($"[{Timestamp()}] Replication → FAIL: {error}");
                return false;
            }

            Console.WriteLine($"[{Timestamp()}] Replication → SUCCESS (Primary = Replica).");
            return true;
        }
        finally
        {
            _replicationLock.Release();
        }
    }

    public string GetStatusMessage()
    {
        bool primaryReachable = CanConnect(_primaryConnectionString);
        bool replicaAvailable = CanConnect(_replicaConnectionString);
        _replicaConnectionFailed = !replicaAvailable;

        bool primaryUnavailableByModel = _forcePrimaryUnavailable || !primaryReachable;
        bool recoveryRequired = !_forcePrimaryUnavailable && _primaryConnectionFailed && primaryReachable;
        bool primaryAvailableForRequests = !primaryUnavailableByModel && !_primaryConnectionFailed;
        string primaryState = primaryUnavailableByModel
            ? "UNAVAILABLE"
            : recoveryRequired ? "AVAILABLE (требуется Recovery)" : "AVAILABLE";
        string active = primaryAvailableForRequests
            ? "Primary"
            : replicaAvailable ? "Replica (failover)" : "нет доступного экземпляра";

        return $"Primary: {primaryState}; Replica: {(replicaAvailable ? "AVAILABLE" : "UNAVAILABLE")}; " +
               $"Активный экземпляр: {active}.";
    }

    /// <summary>Учебная имитация отказа: Primary остаётся запущен, но сервер перестаёт направлять к нему запросы.</summary>
    public bool SimulatePrimaryFailure(out string message)
    {
        if (!CanConnect(_replicaConnectionString))
        {
            message = "Отказ не включён: Replica недоступна, переключаться некуда.";
            return false;
        }

        _forcePrimaryUnavailable = true;
        _primaryConnectionFailed = true;
        Console.WriteLine($"[{Timestamp()}] Primary → UNAVAILABLE (искусственный отказ для лабораторной).");
        Console.WriteLine($"[{Timestamp()}] Replica → AVAILABLE; следующие запросы будут обслуживаться через Replica.");
        message = "Primary → UNAVAILABLE; Replica → AVAILABLE. Failover активирован.";
        return true;
    }

    /// <summary>Возвращает Primary в работу, копирует в него актуальные данные Replica и снимает failover.</summary>
    public bool RecoverPrimaryAndSynchronize(out string message)
    {
        _replicationLock.Wait();
        try
        {
            Console.WriteLine($"[{Timestamp()}] Recovery → проверка Primary.");
            if (!CanConnect(_primaryConnectionString))
            {
                message = "Primary всё ещё недоступен. Recovery не завершён; Replica продолжает обслуживать запросы.";
                _primaryConnectionFailed = true;
                Console.WriteLine($"[{Timestamp()}] Recovery → FAIL: Primary недоступен.");
                return false;
            }

            if (!CanConnect(_replicaConnectionString))
            {
                message = "Replica недоступна; невозможно определить актуальные данные для синхронизации.";
                _replicaConnectionFailed = true;
                return false;
            }

            if (!CopyData(_replicaConnectionString, _primaryConnectionString, "RECOVERY SYNC", out string error))
            {
                message = $"Primary доступен, но синхронизация не выполнена: {error}";
                return false;
            }

            _forcePrimaryUnavailable = false;
            _primaryConnectionFailed = false;
            _replicaConnectionFailed = false;
            Console.WriteLine($"[{Timestamp()}] Recovery → SUCCESS; Primary = Replica; активен Primary.");
            message = "Primary восстановлен. Выполнена синхронизация Replica → Primary; обе копии согласованы, активен Primary.";
            return true;
        }
        finally
        {
            _replicationLock.Release();
        }
    }

    private static bool CanConnect(string connectionString)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = new NpgsqlCommand("SELECT 1", connection);
            command.ExecuteScalar();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Переносит согласованный снимок пяти таблиц. Таблицы очищаются и заполняются в одной транзакции.
    /// Нужен только для учебной модели с небольшим объёмом данных.
    /// </summary>
    private static bool CopyData(string sourceConnectionString, string targetConnectionString, string label, out string error)
    {
        error = string.Empty;
        try
        {
            using var source = new NpgsqlConnection(sourceConnectionString);
            source.Open();
            using var sourceTransaction = source.BeginTransaction(IsolationLevel.RepeatableRead);

            var trainers = ReadRows(source, sourceTransaction,
                "SELECT id, last_name, first_name, middle_name, specialization, experience_years FROM trainers ORDER BY id");
            var clients = ReadRows(source, sourceTransaction,
                "SELECT id, last_name, first_name, middle_name, phone, registered_at, current_membership_id FROM clients ORDER BY id");
            var memberships = ReadRows(source, sourceTransaction,
                "SELECT id, client_id, name, price, duration_days, purchased_at FROM memberships ORDER BY id");
            var slots = ReadRows(source, sourceTransaction,
                "SELECT id, trainer_id, training_time, capacity, available_places FROM training_slots ORDER BY id");
            var trainings = ReadRows(source, sourceTransaction,
                "SELECT id, client_id, slot_id, status, booked_at FROM trainings ORDER BY id");

            using var target = new NpgsqlConnection(targetConnectionString);
            target.Open();
            using var transaction = target.BeginTransaction();
            using (var truncate = new NpgsqlCommand(
                "TRUNCATE TABLE trainings, training_slots, memberships, clients, trainers RESTART IDENTITY CASCADE", target, transaction))
            {
                truncate.ExecuteNonQuery();
            }

            InsertRows(target, transaction, trainers,
                "INSERT INTO trainers(id,last_name,first_name,middle_name,specialization,experience_years) VALUES (@p0,@p1,@p2,@p3,@p4,@p5)");
            // current_membership_id временно не вставляем, поскольку memberships ещё не созданы.
            InsertRows(target, transaction, clients,
                "INSERT INTO clients(id,last_name,first_name,middle_name,phone,registered_at,current_membership_id) VALUES (@p0,@p1,@p2,@p3,@p4,@p5,NULL)", skipLastValue: true);
            InsertRows(target, transaction, memberships,
                "INSERT INTO memberships(id,client_id,name,price,duration_days,purchased_at) VALUES (@p0,@p1,@p2,@p3,@p4,@p5)");

            foreach (object?[] client in clients)
            {
                if (client[6] is null || client[6] == DBNull.Value) continue;
                using var update = new NpgsqlCommand(
                    "UPDATE clients SET current_membership_id = @membershipId WHERE id = @clientId", target, transaction);
                update.Parameters.AddWithValue("membershipId", client[6]!);
                update.Parameters.AddWithValue("clientId", client[0]!);
                update.ExecuteNonQuery();
            }

            InsertRows(target, transaction, slots,
                "INSERT INTO training_slots(id,trainer_id,training_time,capacity,available_places) VALUES (@p0,@p1,@p2,@p3,@p4)");
            InsertRows(target, transaction, trainings,
                "INSERT INTO trainings(id,client_id,slot_id,status,booked_at) VALUES (@p0,@p1,@p2,@p3,@p4)");

            ResetSequence(target, transaction, "trainers", "id");
            ResetSequence(target, transaction, "clients", "id");
            ResetSequence(target, transaction, "memberships", "id");
            ResetSequence(target, transaction, "training_slots", "id");
            ResetSequence(target, transaction, "trainings", "id");

            transaction.Commit();
            sourceTransaction.Commit();
            Console.WriteLine($"[{Timestamp()}] {label} → trainers={trainers.Count}, clients={clients.Count}, memberships={memberships.Count}, training_slots={slots.Count}, trainings={trainings.Count}.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<object?[]> ReadRows(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        var rows = new List<object?[]>();
        using var command = new NpgsqlCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static void InsertRows(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<object?[]> rows,
        string sql,
        bool skipLastValue = false)
    {
        if (rows.Count == 0) return;
        using var command = new NpgsqlCommand(sql, connection, transaction);
        int parameterCount = rows[0].Length - (skipLastValue ? 1 : 0);
        for (int i = 0; i < parameterCount; i++) command.Parameters.Add(new NpgsqlParameter { ParameterName = $"p{i}" });

        foreach (object?[] row in rows)
        {
            for (int i = 0; i < parameterCount; i++)
                command.Parameters[i].Value = row[i] ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private static void ResetSequence(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string column)
    {
        using var command = new NpgsqlCommand(
            $"SELECT setval(pg_get_serial_sequence('public.{table}', '{column}'), " +
            $"GREATEST(COALESCE((SELECT MAX({column}) FROM {table}), 1), 1), " +
            $"EXISTS (SELECT 1 FROM {table}))", connection, transaction);
        command.ExecuteScalar();
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}
