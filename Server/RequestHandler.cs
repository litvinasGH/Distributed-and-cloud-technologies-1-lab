using System.Text.Json;
using fitness_lib;
using Npgsql;
using Server.PubSub;
using Server.Consensus;

namespace Server;

/// <summary>
/// Обрабатывает JSON-запросы клиентов и использует Primary/Replica для постоянных данных.
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
        Response response = request.Operation switch
        {
            "RegisterClient" => RegisterClient(request),
            "BookTraining" => BookTraining(request, clientId, taskId),
            "CancelTraining" => CancelTraining(request, clientId, taskId),
            "BuyMembership" => BuyMembership(request),
            "CheckAvailability" => CheckAvailability(request),
            "DemoWithoutTransaction" => DemoWithoutTransaction(request),
            "DemoWithRollback" => DemoWithRollback(request),
            "TimeRequest" => await GetServerTimeAsync(),
            "ReplicationStatus" => Success(_database.GetStatusMessage()),
            "SimulatePrimaryFailure" => SimulatePrimaryFailure(),
            "RecoverPrimary" => RecoverPrimary(),
            "PubSubStatus" => Success(GetPubSubStatus()),
            "SetSubscriberAvailability" => SetSubscriberAvailability(request),
            "SetBrokerAvailability" => SetBrokerAvailability(request),
            "RegisterAnalyticsSubscriber" => RegisterAnalyticsSubscriber(),
            "RetryPendingEvents" => await RetryPendingEventsAsync(),
            "ConsensusStatus" => Success(ConsensusService.Cluster.GetStatus()),
            "ElectLeader" => Success(ConsensusService.Cluster.ElectLeader() + Environment.NewLine + ConsensusService.Cluster.GetStatus()),
            "SetConsensusNodeAvailability" => SetConsensusNodeAvailability(request),
            "CheckHeartbeats" => Success(ConsensusService.Cluster.CheckHeartbeats()),
            "ProposeConsensus" => ProposeConsensus(request),
            "SetConsensusNodeVote" => SetConsensusNodeVote(request),
            "ResetConsensus" => Success(ConsensusService.Cluster.Reset() + Environment.NewLine + ConsensusService.Cluster.GetStatus()),
            _ => Error($"Неизвестная операция: {request.Operation}")
        };

        // В том числе реплицируем DemoWithoutTransaction: он намеренно оставляет частичное изменение.
        // DemoWithRollback безопасно реплицировать тоже: после ROLLBACK состояние не изменилось.
        if (IsDataChangingOperation(request.Operation) &&
            (response.Success || request.Operation == "DemoWithoutTransaction"))
        {
            if (_database.ReplicatePrimaryToReplica(out string replicationError))
            {
                if (!_database.IsFailoverActive)
                    Console.WriteLine($"[{Program.Timestamp()}] {request.Operation} → Replication SUCCESS.");
            }
            else
            {
                Console.WriteLine($"[{Program.Timestamp()}] {request.Operation} → Replication FAIL: {replicationError}");
                response.Message += " ВНИМАНИЕ: операция обработана, но репликация завершилась ошибкой.";
            }
        }

        return response;
    }

    private Response SimulatePrimaryFailure()
    {
        return _database.SimulatePrimaryFailure(out string message)
            ? Success(message)
            : Error(message);
    }

    private Response RecoverPrimary()
    {
        return _database.RecoverPrimaryAndSynchronize(out string message)
            ? Success(message)
            : Error(message);
    }

    private static bool IsDataChangingOperation(string operation) => operation is
        "RegisterClient" or "BookTraining" or "CancelTraining" or "BuyMembership" or
        "DemoWithoutTransaction" or "DemoWithRollback";

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
            bool eventPublished = PublishFitnessEvent(
                "MembershipPurchased",
                membershipId,
                clientId,
                new Dictionary<string, string>
                {
                    ["ClientPhone"] = data.ClientPhone,
                    ["MembershipName"] = data.MembershipName,
                    ["Price"] = data.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            return Success($"Абонемент «{data.MembershipName}» куплен. Операции INSERT и UPDATE подтверждены COMMIT. " +
                           (eventPublished ? "Событие опубликовано через Pub/Sub." : "ВНИМАНИЕ: все Broker недоступны, событие не опубликовано."));
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

            long createdTrainingId = 0;
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
                        createdTrainingId = Convert.ToInt64(insertTraining.ExecuteScalar());
                        transaction.Commit();
                        Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | COMMIT training_id={createdTrainingId}; available_places=0; SUCCESS");
                    }

                }
                finally
                {
                    Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | UNLOCK — выход из критической секции");
                }
            }

            bool eventPublished = PublishFitnessEvent(
                "TrainingBooked",
                createdTrainingId,
                dbClientId.Value,
                new Dictionary<string, string>
                {
                    ["ClientPhone"] = data.ClientPhone,
                    ["Trainer"] = $"{data.TrainerLastName} {data.TrainerFirstName}",
                    ["TrainingTime"] = trainingTime.ToString("yyyy-MM-dd HH:mm")
                });
            return Success("Тренировка забронирована. INSERT в trainings и изменение свободных мест сохранены транзакцией COMMIT. " +
                           (eventPublished ? "Событие опубликовано через Pub/Sub." : "ВНИМАНИЕ: все Broker недоступны, событие не опубликовано."));
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

            long cancelledTrainingId;
            long cancelledClientId;
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
                cancelledTrainingId = trainingId;
                cancelledClientId = dbClientId.Value;
                Console.WriteLine($"[{Program.Timestamp()}] Client {clientId} | TaskId={taskId} | CANCEL training_id={trainingId}; COMMIT");
            }

            bool eventPublished = PublishFitnessEvent(
                "TrainingCancelled",
                cancelledTrainingId,
                cancelledClientId,
                new Dictionary<string, string>
                {
                    ["ClientPhone"] = data.ClientPhone,
                    ["TrainingTime"] = trainingTime.ToString("yyyy-MM-dd HH:mm")
                });
            return Success("Запись отменена. Статус изменён, место возвращено; COMMIT выполнен. " +
                           (eventPublished ? "Событие опубликовано через Pub/Sub." : "ВНИМАНИЕ: все Broker недоступны, событие не опубликовано."));
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


    private static string GetPubSubStatus()
    {
        EventPublisher publisher = FitnessPubSub.Publisher;
        return FitnessPubSub.Cluster.GetStatus() + Environment.NewLine +
               $"Publisher: published={publisher.PublishedCount}; errors={publisher.ErrorCount}";
    }

    private static bool PublishFitnessEvent(string eventType, long entityId, long clientId, Dictionary<string, string> data)
    {
        var domainEvent = new DomainEvent
        {
            EventType = eventType,
            EntityId = entityId,
            ClientId = clientId,
            Timestamp = DateTime.UtcNow,
            Data = data
        };

        // Публикация выполняется после COMMIT. Отказ брокера не отменяет уже сохранённую бизнес-операцию.
        bool published = FitnessPubSub.Publisher.PublishAsync(domainEvent).GetAwaiter().GetResult();
        if (!published)
            Console.WriteLine($"[{Program.Timestamp()}] WARNING: событие {eventType} не опубликовано; бизнес-операция уже сохранена.");
        return published;
    }

    private static Response SetSubscriberAvailability(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Не переданы параметры подписчика.");
        try
        {
            SubscriberAvailabilityRequest? data = JsonSerializer.Deserialize<SubscriberAvailabilityRequest>(request.Data);
            if (data is null || string.IsNullOrWhiteSpace(data.Name)) return Error("Некорректные параметры подписчика.");
            return FitnessPubSub.Cluster.SetSubscriberAvailability(data.Name, data.Available)
                ? Success($"Subscriber {data.Name}: {(data.Available ? "AVAILABLE" : "UNAVAILABLE")}")
                : Error($"Subscriber {data.Name} не зарегистрирован.");
        }
        catch (JsonException ex) { return Error($"Некорректный JSON: {ex.Message}"); }
    }

    private static Response SetBrokerAvailability(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Не переданы параметры Broker.");
        try
        {
            BrokerAvailabilityRequest? data = JsonSerializer.Deserialize<BrokerAvailabilityRequest>(request.Data);
            if (data is null || string.IsNullOrWhiteSpace(data.Name)) return Error("Некорректные параметры Broker.");
            return FitnessPubSub.Cluster.SetBrokerAvailability(data.Name, data.Available)
                ? Success($"Broker {data.Name}: {(data.Available ? "AVAILABLE" : "UNAVAILABLE")}")
                : Error($"Broker {data.Name} не найден.");
        }
        catch (JsonException ex) { return Error($"Некорректный JSON: {ex.Message}"); }
    }

    private static Response RegisterAnalyticsSubscriber()
    {
        bool registered = FitnessPubSub.Cluster.RegisterSubscriber(new AnalyticsServiceSubscriber());
        return registered
            ? Success("AnalyticsService зарегистрирован в обоих Broker и подписан на TrainingBooked, TrainingCancelled, MembershipPurchased.")
            : Success("AnalyticsService уже зарегистрирован.");
    }

    private static async Task<Response> RetryPendingEventsAsync()
    {
        try
        {
            await FitnessPubSub.Cluster.RetryPendingAsync();
            return Success("Повторная доставка завершена.\n" + FitnessPubSub.Cluster.GetStatus());
        }
        catch (Exception ex) { return Error($"Ошибка повторной доставки: {ex.Message}"); }
    }

    private static Response SetConsensusNodeAvailability(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Не переданы параметры узла.");
        try
        {
            var data = JsonSerializer.Deserialize<ConsensusNodeAvailabilityRequest>(request.Data);
            if (data is null || data.NodeId <= 0) return Error("Некорректный ID узла.");
            string message = ConsensusService.Cluster.SetNodeAvailability(data.NodeId, data.Available);
            if (message.Contains("не существует", StringComparison.OrdinalIgnoreCase)) return Error(message);
            return Success(message + Environment.NewLine + ConsensusService.Cluster.GetStatus());
        }
        catch (JsonException ex) { return Error($"Некорректный JSON: {ex.Message}"); }
    }

    private static Response ProposeConsensus(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Не передано предлагаемое значение.");
        try
        {
            var data = JsonSerializer.Deserialize<ConsensusProposalRequest>(request.Data);
            if (data is null || string.IsNullOrWhiteSpace(data.Value)) return Error("Предлагаемое значение не может быть пустым.");
            ConsensusResult result = ConsensusService.Cluster.Propose(data.Value.Trim());
            return result.Achieved ? Success(result.Message) : Error(result.Message);
        }
        catch (JsonException ex) { return Error($"Некорректный JSON: {ex.Message}"); }
    }

    private static Response SetConsensusNodeVote(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data)) return Error("Не переданы параметры голоса.");
        try
        {
            var data = JsonSerializer.Deserialize<ConsensusVoteRequest>(request.Data);
            if (data is null || data.NodeId <= 0) return Error("Некорректные параметры голоса.");
            string message = ConsensusService.Cluster.SetNodeVote(data.NodeId, data.VoteYes);
            if (message.Contains("не существует", StringComparison.OrdinalIgnoreCase)) return Error(message);
            return Success(message + Environment.NewLine + ConsensusService.Cluster.GetStatus());
        }
        catch (JsonException ex) { return Error($"Некорректный JSON: {ex.Message}"); }
    }

    public sealed class ConsensusNodeAvailabilityRequest
    {
        public int NodeId { get; set; }
        public bool Available { get; set; }
    }

    public sealed class ConsensusProposalRequest
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class ConsensusVoteRequest
    {
        public int NodeId { get; set; }
        public bool VoteYes { get; set; }
    }

    public sealed class SubscriberAvailabilityRequest
    {
        public string Name { get; set; } = string.Empty;
        public bool Available { get; set; }
    }

    public sealed class BrokerAvailabilityRequest
    {
        public string Name { get; set; } = string.Empty;
        public bool Available { get; set; }
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
