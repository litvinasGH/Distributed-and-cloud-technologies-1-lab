using System.Text.Json;
using fitness_lib;
using Npgsql;

namespace Server;

/// <summary>
/// Обрабатывает JSON-запросы клиентов. Все постоянные данные хранятся в PostgreSQL.
/// </summary>
public sealed class RequestHandler
{
    private readonly DatabaseService _database;
    private readonly TimeSpan _bookingDelay;
    private static readonly object BookingLock = new();

    public RequestHandler(DatabaseService database, TimeSpan? bookingDelay = null)
    {
        _database = database;
        _bookingDelay = bookingDelay ?? TimeSpan.FromSeconds(4);
    }

    public async Task<Response> HandleAsync(Request request, int clientId = 0, int taskId = -1)
    {
        return request.Operation switch
        {
            "RegisterClient" => RegisterClient(request),
            "BookTraining" => BookTraining(request, clientId, taskId),
            "CancelTraining" => CancelTraining(request, clientId, taskId),
            "BuyMembership" => BuyMembership(request),
            "CheckAvailability" => CheckAvailability(request),
            "DemoWithoutTransaction" => DemoWithoutTransaction(request),
            "DemoWithRollback" => DemoWithRollback(request),
            "TimeRequest" => await GetServerTimeAsync(),
            _ => Error($"Неизвестная операция: {request.Operation}")
        };
    }

    private Response RegisterClient(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Данные клиента не переданы.");

        try
        {
            var data = JsonSerializer.Deserialize<RegisterClientData>(request.Data);
            if (data is null) return Error("Некорректные данные клиента.");
            if (string.IsNullOrWhiteSpace(data.LastName) || string.IsNullOrWhiteSpace(data.FirstName) || string.IsNullOrWhiteSpace(data.Phone))
                return Error("Фамилия, имя и телефон обязательны.");

            using var connection = _database.OpenConnection();
            using var command = new NpgsqlCommand(
                "INSERT INTO clients(last_name, first_name, middle_name, phone) " +
                "VALUES (@lastName, @firstName, @middleName, @phone) RETURNING id", connection);
            command.Parameters.AddWithValue("lastName", data.LastName.Trim());
            command.Parameters.AddWithValue("firstName", data.FirstName.Trim());
            command.Parameters.AddWithValue("middleName", data.MiddleName?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("phone", data.Phone.Trim());
            long id = Convert.ToInt64(command.ExecuteScalar());

            FitnessEvents.OnClientRegistered($"Зарегистрирован клиент #{id}: {data.LastName} {data.FirstName}");
            return Success($"Клиент успешно зарегистрирован. ID: {id}.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Error("Клиент с таким номером телефона уже существует.");
        }
        catch (Exception ex) { return Error($"Не удалось зарегистрировать клиента: {ex.Message}"); }
    }

    private Response BuyMembership(Request request)
    {
        if (!TryReadMembershipData(request, out var data, out var error)) return Error(error);

        try
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            long clientId = GetClientId(connection, transaction, data!.ClientPhone)
                ?? throw new InvalidOperationException("Клиент с таким телефоном не найден.");

            long membershipId = InsertMembership(connection, transaction, clientId, data);
            Console.WriteLine($"[{Program.Timestamp()}] TRANSACTION: операция 1 — абонемент #{membershipId} добавлен.");

            using (var update = new NpgsqlCommand(
                "UPDATE clients SET current_membership_id = @membershipId WHERE id = @clientId",
                connection, transaction))
            {
                update.Parameters.AddWithValue("membershipId", membershipId);
                update.Parameters.AddWithValue("clientId", clientId);
                if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Не удалось назначить абонемент клиенту.");
            }

            Console.WriteLine($"[{Program.Timestamp()}] TRANSACTION: операция 2 — абонемент назначен клиенту #{clientId}.");
            transaction.Commit();
            Console.WriteLine($"[{Program.Timestamp()}] COMMIT — обе операции сохранены.");
            FitnessEvents.OnMembershipPurchased($"Клиент {data.ClientPhone} приобрёл абонемент «{data.MembershipName}»");
            return Success($"Абонемент «{data.MembershipName}» куплен. Операции INSERT и UPDATE подтверждены COMMIT.");
        }
        catch (Exception ex) { return Error($"Покупка абонемента отменена: {ex.Message}"); }
    }

    /// <summary>
    /// Демонстрация частичного изменения: INSERT выполняется в autocommit,
    /// затем моделируется ошибка до UPDATE клиента.
    /// </summary>
    private Response DemoWithoutTransaction(Request request)
    {
        if (!TryReadMembershipData(request, out var data, out var error)) return Error(error);

        long membershipId;
        try
        {
            using var connection = _database.OpenConnection();
            long? clientId = GetClientId(connection, null, data!.ClientPhone);
            if (clientId is null) return Error("Клиент с таким телефоном не найден.");

            // В autocommit эта операция сохраняется сразу после выполнения INSERT.
            membershipId = InsertMembership(connection, null, clientId.Value, data);
        }
        catch (Exception ex)
        {
            return Error($"Первая операция не выполнена, частичного изменения нет: {ex.Message}");
        }

        Console.WriteLine($"[{Program.Timestamp()}] БЕЗ ТРАНЗАКЦИИ: операция 1 сохранена — membership #{membershipId}.");
        try
        {
            // Искусственная ошибка между INSERT и предполагаемым UPDATE клиента.
            throw new InvalidOperationException("Искусственная ошибка между INSERT memberships и UPDATE clients.");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[{Program.Timestamp()}] ОЖИДАЕМАЯ ОШИБКА БЕЗ ТРАНЗАКЦИИ: {ex.Message}");
            return Error("Демонстрация завершена: INSERT абонемента уже сохранён, но назначение клиенту не выполнено. " +
                         "Это частичное изменение БД. Проверьте memberships и clients.current_membership_id.");
        }
    }

    /// <summary>Та же составная операция, но ошибка откатывается транзакцией.</summary>
    private Response DemoWithRollback(Request request)
    {
        if (!TryReadMembershipData(request, out var data, out var error)) return Error(error);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        bool insertCompleted = false;
        try
        {
            long clientId = GetClientId(connection, transaction, data!.ClientPhone)
                ?? throw new InvalidOperationException("Клиент с таким телефоном не найден.");

            long membershipId = InsertMembership(connection, transaction, clientId, data);
            insertCompleted = true;
            Console.WriteLine($"[{Program.Timestamp()}] TRANSACTION: операция 1 — абонемент #{membershipId} добавлен внутри транзакции.");

            throw new InvalidOperationException("Искусственная ошибка для проверки ROLLBACK.");
        }
        catch (Exception ex)
        {
            try
            {
                transaction.Rollback();
                Console.WriteLine($"[{Program.Timestamp()}] ROLLBACK — изменения отменены.");
            }
            catch (Exception rollbackEx)
            {
                Console.WriteLine($"[{Program.Timestamp()}] Ошибка ROLLBACK: {rollbackEx.Message}");
                return Error($"Ошибка внутри транзакции ({ex.Message}); также не удалось выполнить ROLLBACK: {rollbackEx.Message}");
            }

            return insertCompleted
                ? Error("Искусственная ошибка вызвана намеренно. Выполнен ROLLBACK: INSERT отменён, частичных изменений нет.")
                : Error($"Транзакция отменена из-за ошибки до первой операции: {ex.Message}");
        }
    }

    private Response BookTraining(Request request, int clientId, int taskId)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Данные тренировки не переданы.");

        try
        {
            var data = JsonSerializer.Deserialize<BookTrainingData>(request.Data);
            if (data is null) return Error("Некорректные данные тренировки.");
            DateTime trainingTime = DateTime.SpecifyKind(data.TrainingTime, DateTimeKind.Unspecified);

            using var connection = _database.OpenConnection();
            long? dbClientId = GetClientId(connection, null, data.ClientPhone);
            if (dbClientId is null) return Error("Клиент не найден. Сначала зарегистрируйте его.");
            long? trainerId = GetTrainerId(connection, null, data.TrainerLastName, data.TrainerFirstName);
            if (trainerId is null) return Error("Тренер не найден.");

            Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | START Reserve | Slot={trainingTime:yyyy-MM-dd HH:mm}");

            // lock защищает проверку доступности, уменьшение мест и создание записи в рамках процесса сервера.
            // PostgreSQL FOR UPDATE и транзакция защищают строку БД дополнительно.
            lock (BookingLock)
            {
                Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | LOCK — вход в критическую секцию");
                try
                {
                    using var transaction = connection.BeginTransaction();
                    using (var createSlot = new NpgsqlCommand(
                        "INSERT INTO training_slots(trainer_id, training_time, capacity, available_places) " +
                        "VALUES (@trainerId, @trainingTime, 1, 1) " +
                        "ON CONFLICT (trainer_id, training_time) DO NOTHING", connection, transaction))
                    {
                        createSlot.Parameters.AddWithValue("trainerId", trainerId.Value);
                        createSlot.Parameters.AddWithValue("trainingTime", trainingTime);
                        createSlot.ExecuteNonQuery();
                    }

                    int available;
                    long slotId;
                    using (var selectSlot = new NpgsqlCommand(
                        "SELECT id, available_places FROM training_slots " +
                        "WHERE trainer_id = @trainerId AND training_time = @trainingTime FOR UPDATE",
                        connection, transaction))
                    {
                        selectSlot.Parameters.AddWithValue("trainerId", trainerId.Value);
                        selectSlot.Parameters.AddWithValue("trainingTime", trainingTime);
                        using var reader = selectSlot.ExecuteReader();
                        if (!reader.Read()) throw new InvalidOperationException("Не удалось получить слот тренировки.");
                        slotId = reader.GetInt64(0);
                        available = reader.GetInt32(1);
                    }

                    Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | CHECK available_places={available}");
                    if (available <= 0)
                    {
                        transaction.Rollback();
                        Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | FAIL — мест нет");
                        return Error("Свободных мест на тренировке нет. Запись отклонена.");
                    }

                    if (_bookingDelay > TimeSpan.Zero) Thread.Sleep(_bookingDelay);

                    using (var updateSlot = new NpgsqlCommand(
                        "UPDATE training_slots SET available_places = available_places - 1 WHERE id = @slotId AND available_places > 0",
                        connection, transaction))
                    {
                        updateSlot.Parameters.AddWithValue("slotId", slotId);
                        if (updateSlot.ExecuteNonQuery() != 1) throw new InvalidOperationException("Место уже занято другим запросом.");
                    }

                    using (var insertTraining = new NpgsqlCommand(
                        "INSERT INTO trainings(client_id, slot_id, status) VALUES (@clientId, @slotId, 'Запланирована') RETURNING id",
                        connection, transaction))
                    {
                        insertTraining.Parameters.AddWithValue("clientId", dbClientId.Value);
                        insertTraining.Parameters.AddWithValue("slotId", slotId);
                        long trainingId = Convert.ToInt64(insertTraining.ExecuteScalar());
                        transaction.Commit();
                        Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | COMMIT training_id={trainingId}; available_places=0; SUCCESS");
                    }

                    FitnessEvents.OnTrainingBooked($"Клиент {data.ClientPhone} записан к {data.TrainerLastName} {data.TrainerFirstName} на {trainingTime:dd.MM.yyyy HH:mm}");
                    return Success("Тренировка забронирована. INSERT в trainings и изменение свободных мест сохранены транзакцией COMMIT.");
                }
                finally
                {
                    Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | UNLOCK — выход из критической секции");
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            return Error("Не найдены клиент, тренер или слот, связанные с этой тренировкой.");
        }
        catch (Exception ex) { return Error($"Не удалось забронировать тренировку: {ex.Message}"); }
    }

    private Response CancelTraining(Request request, int clientId, int taskId)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Данные тренировки не переданы.");

        try
        {
            var data = JsonSerializer.Deserialize<CancelTrainingData>(request.Data);
            if (data is null) return Error("Некорректные данные тренировки.");
            DateTime trainingTime = DateTime.SpecifyKind(data.TrainingTime, DateTimeKind.Unspecified);

            lock (BookingLock)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                long? dbClientId = GetClientId(connection, transaction, data.ClientPhone);
                if (dbClientId is null) return Error("Клиент не найден.");

                long trainingId;
                long slotId;
                using (var find = new NpgsqlCommand(
                    "SELECT t.id, t.slot_id FROM trainings t JOIN training_slots s ON s.id = t.slot_id " +
                    "WHERE t.client_id = @clientId AND s.training_time = @trainingTime AND t.status <> 'Отменена' " +
                    "ORDER BY t.id DESC LIMIT 1 FOR UPDATE OF t, s", connection, transaction))
                {
                    find.Parameters.AddWithValue("clientId", dbClientId.Value);
                    find.Parameters.AddWithValue("trainingTime", trainingTime);
                    using var reader = find.ExecuteReader();
                    if (!reader.Read()) return Error("Активная запись на эту тренировку не найдена.");
                    trainingId = reader.GetInt64(0);
                    slotId = reader.GetInt64(1);
                }

                using (var updateTraining = new NpgsqlCommand("UPDATE trainings SET status = 'Отменена' WHERE id = @id", connection, transaction))
                {
                    updateTraining.Parameters.AddWithValue("id", trainingId);
                    updateTraining.ExecuteNonQuery();
                }
                using (var updateSlot = new NpgsqlCommand(
                    "UPDATE training_slots SET available_places = LEAST(capacity, available_places + 1) WHERE id = @id", connection, transaction))
                {
                    updateSlot.Parameters.AddWithValue("id", slotId);
                    updateSlot.ExecuteNonQuery();
                }
                transaction.Commit();
                Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | CANCEL training_id={trainingId}; COMMIT");
            }

            FitnessEvents.OnTrainingCancelled($"Клиент {data.ClientPhone} отменил тренировку на {trainingTime:dd.MM.yyyy HH:mm}");
            return Success("Запись отменена. Статус изменён, место возвращено; COMMIT выполнен.");
        }
        catch (Exception ex) { return Error($"Не удалось отменить тренировку: {ex.Message}"); }
    }

    private Response CheckAvailability(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Данные тренировки не переданы.");

        try
        {
            var data = JsonSerializer.Deserialize<CheckAvailabilityData>(request.Data);
            if (data is null) return Error("Некорректные данные тренировки.");
            DateTime trainingTime = DateTime.SpecifyKind(data.TrainingTime, DateTimeKind.Unspecified);

            using var connection = _database.OpenConnection();
            long? trainerId = GetTrainerId(connection, null, data.TrainerLastName, data.TrainerFirstName);
            if (trainerId is null) return Error("Тренер не найден.");

            using var command = new NpgsqlCommand(
                "SELECT COALESCE(s.capacity, 1), COALESCE(s.available_places, 1), " +
                "(SELECT COUNT(*) FROM trainings t WHERE t.slot_id = s.id AND t.status <> 'Отменена') " +
                "FROM (SELECT 1) seed LEFT JOIN training_slots s " +
                "ON s.trainer_id = @trainerId AND s.training_time = @trainingTime",
                connection);
            command.Parameters.AddWithValue("trainerId", trainerId.Value);
            command.Parameters.AddWithValue("trainingTime", trainingTime);
            using var reader = command.ExecuteReader();
            reader.Read();
            int capacity = reader.GetInt32(0);
            int available = reader.GetInt32(1);
            long bookings = reader.GetInt64(2);
            return Success($"Тренер: {data.TrainerLastName} {data.TrainerFirstName}; время: {trainingTime:dd.MM.yyyy HH:mm}; вместимость: {capacity}; свободных мест: {available}; записей: {bookings}.");
        }
        catch (Exception ex) { return Error($"Не удалось проверить доступность: {ex.Message}"); }
    }

    private static async Task<Response> GetServerTimeAsync()
    {
        DateTime serverTime = DateTime.UtcNow;
        const int delayMs = 1000;
        Console.WriteLine($"[{Program.Timestamp()}] TIME_REQUEST → {serverTime:HH:mm:ss.fff} UTC");
        await Task.Delay(delayMs);
        return Success("Время сервера получено", new TimeResponseData { ServerTime = serverTime, ArtificialDelayMs = delayMs });
    }

    private static bool TryReadMembershipData(Request request, out BuyMembershipData? data, out string error)
    {
        data = null;
        error = "";
        if (string.IsNullOrWhiteSpace(request.Data)) { error = "Данные абонемента не переданы."; return false; }
        try { data = JsonSerializer.Deserialize<BuyMembershipData>(request.Data); }
        catch (JsonException) { error = "Некорректный JSON абонемента."; return false; }
        if (data is null) { error = "Некорректные данные абонемента."; return false; }
        if (string.IsNullOrWhiteSpace(data.ClientPhone) || string.IsNullOrWhiteSpace(data.MembershipName) || data.Price < 0 || data.DurationDays <= 0)
        { error = "Укажите телефон, название абонемента, цену не меньше нуля и положительный срок действия."; return false; }
        return true;
    }

    private static long? GetClientId(NpgsqlConnection connection, NpgsqlTransaction? transaction, string phone)
    {
        using var command = new NpgsqlCommand("SELECT id FROM clients WHERE phone = @phone", connection, transaction);
        command.Parameters.AddWithValue("phone", phone.Trim());
        object? result = command.ExecuteScalar();
        return result is null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private static long? GetTrainerId(NpgsqlConnection connection, NpgsqlTransaction? transaction, string lastName, string firstName)
    {
        using var command = new NpgsqlCommand("SELECT id FROM trainers WHERE last_name = @lastName AND first_name = @firstName", connection, transaction);
        command.Parameters.AddWithValue("lastName", lastName.Trim());
        command.Parameters.AddWithValue("firstName", firstName.Trim());
        object? result = command.ExecuteScalar();
        return result is null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private static long InsertMembership(NpgsqlConnection connection, NpgsqlTransaction? transaction, long clientId, BuyMembershipData data)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO memberships(client_id, name, price, duration_days) " +
            "VALUES (@clientId, @name, @price, @duration) RETURNING id", connection, transaction);
        command.Parameters.AddWithValue("clientId", clientId);
        command.Parameters.AddWithValue("name", data.MembershipName.Trim());
        command.Parameters.AddWithValue("price", data.Price);
        command.Parameters.AddWithValue("duration", data.DurationDays);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static Response Success(string message, object? data = null) => new() { Success = true, Message = message, Data = data };
    private static Response Error(string message) => new() { Success = false, Message = message };
}
