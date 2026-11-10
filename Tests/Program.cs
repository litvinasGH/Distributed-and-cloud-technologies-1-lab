using System.Net.Sockets;
using System.Text.Json;
using fitness_lib;
using Npgsql;
using Server.PubSub;

internal static class Program
{
    private static readonly string ServerHost = Environment.GetEnvironmentVariable("SERVER_HOST") ?? "localhost";
    private static readonly int ServerPort = int.TryParse(Environment.GetEnvironmentVariable("SERVER_PORT"), out int port) ? port : 5000;
    private static readonly string ConnectionString = BuildConnectionString(
        Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost",
        int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out int dbPort) ? dbPort : 5432);

    private static readonly string ReplicaConnectionString = BuildConnectionString(
        Environment.GetEnvironmentVariable("REPLICA_DB_HOST") ?? "localhost",
        int.TryParse(Environment.GetEnvironmentVariable("REPLICA_DB_PORT"), out int replicaPort) ? replicaPort : 5433);

    private static string BuildConnectionString(string host, int port) => new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = port,
        Database = Environment.GetEnvironmentVariable("DB_NAME") ?? "fitness_db",
        Username = Environment.GetEnvironmentVariable("DB_USER") ?? "fitness_user",
        Password = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "fitness_pass",
        Timeout = 5
    }.ConnectionString;

    public static async Task<int> Main()
    {
        try
        {
            await RunPubSubUnitTests();
            await WaitForServerAsync();
            await WaitForDatabaseAsync();

            string unique = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            string phone1 = $"test-{unique}-A";
            string phone2 = $"test-{unique}-B";

            AssertSuccess(await SendAsync(new Request
            {
                Operation = "RegisterClient",
                Data = JsonSerializer.Serialize(new RegisterClientData
                {
                    LastName = "Тестов",
                    FirstName = "Первый",
                    MiddleName = "",
                    Phone = phone1
                })
            }), "Регистрация клиента A");

            AssertSuccess(await SendAsync(new Request
            {
                Operation = "RegisterClient",
                Data = JsonSerializer.Serialize(new RegisterClientData
                {
                    LastName = "Тестова",
                    FirstName = "Вторая",
                    MiddleName = "",
                    Phone = phone2
                })
            }), "Регистрация клиента B");
            Assert(await ClientExistsAsync(phone1, ReplicaConnectionString), "Новая регистрация должна реплицироваться в Replica.");
            Assert(await ClientExistsAsync(phone2, ReplicaConnectionString), "Вторая регистрация должна реплицироваться в Replica.");
            Console.WriteLine("PASS: регистрация клиентов сохраняется в Primary и реплицируется в Replica.");

            var demoData = new BuyMembershipData
            {
                ClientPhone = phone1,
                MembershipName = $"NO-TX-{unique}",
                Price = 10,
                DurationDays = 7
            };
            Response noTransaction = await SendAsync(new Request
            {
                Operation = "DemoWithoutTransaction",
                Data = JsonSerializer.Serialize(demoData)
            });
            Assert(!noTransaction.Success, "Демонстрация без транзакции должна сообщать об искусственной ошибке.");
            long membershipsAfterNoTx = await MembershipCountAsync(phone1);
            Assert(membershipsAfterNoTx == 1, $"Без транзакции ожидалась 1 сохранённая запись, получено {membershipsAfterNoTx}.");
            Assert(await MembershipCountAsync(phone1, ReplicaConnectionString) == 1, "Частичное изменение тоже должно реплицироваться в Replica.");
            Assert(await CurrentMembershipIsNullAsync(phone1), "Без транзакции current_membership_id должен остаться NULL.");
            Console.WriteLine("PASS: без транзакции наблюдается частичное изменение.");

            demoData.MembershipName = $"ROLLBACK-{unique}";
            Response rollback = await SendAsync(new Request
            {
                Operation = "DemoWithRollback",
                Data = JsonSerializer.Serialize(demoData)
            });
            Assert(!rollback.Success, "Демонстрация ROLLBACK должна сообщать об искусственной ошибке.");
            Assert(await MembershipCountAsync(phone1) == 1, "ROLLBACK должен отменить новую запись абонемента.");
            Assert(await MembershipCountAsync(phone1, ReplicaConnectionString) == 1, "После ROLLBACK копии должны оставаться согласованными.");
            Assert(await CurrentMembershipIsNullAsync(phone1), "После ROLLBACK current_membership_id должен оставаться NULL.");
            Console.WriteLine("PASS: ROLLBACK отменяет вставку и не оставляет частичного изменения.");

            demoData.MembershipName = $"COMMIT-{unique}";
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "BuyMembership",
                Data = JsonSerializer.Serialize(demoData)
            }), "Покупка абонемента с COMMIT");
            Assert(await MembershipCountAsync(phone1) == 2, "После COMMIT должна появиться запись абонемента.");
            Assert(await MembershipCountAsync(phone1, ReplicaConnectionString) == 2, "После COMMIT новая запись должна быть в Replica.");
            Assert(!await CurrentMembershipIsNullAsync(phone1), "После COMMIT абонемент должен быть назначен клиенту.");
            Assert(!await CurrentMembershipIsNullAsync(phone1, ReplicaConnectionString), "Ссылка на абонемент должна реплицироваться в Replica.");
            Console.WriteLine("PASS: COMMIT сохраняет обе связанные операции.");

            Response pubSubInitial = await SendAsync(new Request { Operation = "PubSubStatus" });
            AssertSuccess(pubSubInitial, "Начальное состояние Pub/Sub");
            Assert(pubSubInitial.Message.Contains("Logger") &&
                   pubSubInitial.Message.Contains("ScheduleService") &&
                   pubSubInitial.Message.Contains("NotificationService"),
                "При запуске должны быть зарегистрированы три основных Subscriber.");
            Assert(pubSubInitial.Message.Contains("ScheduleService: AVAILABLE; handled=0") &&
                   pubSubInitial.Message.Contains("Logger: AVAILABLE; handled=1"),
                "После MembershipPurchased Logger должен получить событие, а ScheduleService — нет.");
            Console.WriteLine("PASS: Broker хранит подписки и не доставляет MembershipPurchased неподписанному ScheduleService.");

            AssertSuccess(await SendAsync(new Request
            {
                Operation = "SetSubscriberAvailability",
                Data = JsonSerializer.Serialize(new { Name = "ScheduleService", Available = false })
            }), "Имитация отказа ScheduleService");

            DateTime trainingTime = DateTime.SpecifyKind(DateTime.Now.AddDays(2).Date.AddHours(18), DateTimeKind.Unspecified);
            var bookingA = new Request
            {
                Operation = "BookTraining",
                Data = JsonSerializer.Serialize(new BookTrainingData
                {
                    ClientPhone = phone1,
                    TrainerLastName = "Иванов",
                    TrainerFirstName = "Иван",
                    TrainingTime = trainingTime
                })
            };
            var bookingB = new Request
            {
                Operation = "BookTraining",
                Data = JsonSerializer.Serialize(new BookTrainingData
                {
                    ClientPhone = phone2,
                    TrainerLastName = "Иванов",
                    TrainerFirstName = "Иван",
                    TrainingTime = trainingTime
                })
            };

            Response[] bookingResults = await Task.WhenAll(SendAsync(bookingA), SendAsync(bookingB));
            Assert(bookingResults.Count(x => x.Success) == 1, "Из двух одновременных запросов бронирования ровно один должен завершиться успешно.");
            Assert(bookingResults.Count(x => !x.Success) == 1, "Второе одновременное бронирование должно быть отклонено.");
            string winnerPhone = bookingResults[0].Success ? phone1 : phone2;
            Assert(await SlotAvailablePlacesAsync(trainingTime) == 0, "После бронирования свободных мест должно быть 0.");
            Assert(await SlotAvailablePlacesAsync(trainingTime, ReplicaConnectionString) == 0, "Состояние свободных мест должно реплицироваться.");
            Assert(await ActiveBookingsAsync(trainingTime) == 1, "В БД должна быть одна активная запись на слот.");
            Assert(await ActiveBookingsAsync(trainingTime, ReplicaConnectionString) == 1, "Запись на тренировку должна существовать в Replica.");
            Console.WriteLine("PASS: два конкурентных клиента — ровно один SUCCESS и один FAIL.");

            Response failedSubscriberStatus = await SendAsync(new Request { Operation = "PubSubStatus" });
            Assert(failedSubscriberStatus.Message.Contains("ScheduleService: UNAVAILABLE") &&
                   failedSubscriberStatus.Message.Contains("pending=1"),
                "Отказ ScheduleService должен оставить недоставленное событие в очереди Broker.");
            Assert(failedSubscriberStatus.Message.Contains("Logger: AVAILABLE") &&
                   failedSubscriberStatus.Message.Contains("NotificationService: AVAILABLE"),
                "Отказ одного Subscriber не должен останавливать остальные.");
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "SetSubscriberAvailability",
                Data = JsonSerializer.Serialize(new { Name = "ScheduleService", Available = true })
            }), "Восстановление ScheduleService");
            AssertSuccess(await SendAsync(new Request { Operation = "RetryPendingEvents" }), "Повторная доставка после восстановления Subscriber");
            Response retriedStatus = await SendAsync(new Request { Operation = "PubSubStatus" });
            Assert(retriedStatus.Message.Contains("pending=0"), "После повторной доставки очередь должна быть пустой.");
            Console.WriteLine("PASS: Broker сохранил событие и доставил его после восстановления Subscriber.");

            AssertSuccess(await SendAsync(new Request
            {
                Operation = "CancelTraining",
                Data = JsonSerializer.Serialize(new CancelTrainingData { ClientPhone = winnerPhone, TrainingTime = trainingTime })
            }), "Отмена бронирования");
            Assert(await SlotAvailablePlacesAsync(trainingTime) == 1, "После отмены свободное место должно вернуться.");
            Assert(await SlotAvailablePlacesAsync(trainingTime, ReplicaConnectionString) == 1, "Освобождение места должно реплицироваться.");
            Assert(await ActiveBookingsAsync(trainingTime) == 0, "После отмены активных записей на слот быть не должно.");
            Assert(await ActiveBookingsAsync(trainingTime, ReplicaConnectionString) == 0, "Отмена тренировки должна быть в Replica.");
            Console.WriteLine("PASS: отмена бронирования возвращает место.");

            AssertSuccess(await SendAsync(new Request { Operation = "RegisterAnalyticsSubscriber" }), "Регистрация AnalyticsService");
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "BookTraining",
                Data = JsonSerializer.Serialize(new BookTrainingData
                {
                    ClientPhone = winnerPhone,
                    TrainerLastName = "Иванов",
                    TrainerFirstName = "Иван",
                    TrainingTime = trainingTime
                })
            }), "Повторное бронирование для проверки нового Subscriber");
            Response analyticsStatus = await SendAsync(new Request { Operation = "PubSubStatus" });
            Assert(analyticsStatus.Message.Contains("AnalyticsService: AVAILABLE; handled=1"),
                "Новый AnalyticsService должен получить событие без изменения Publisher.");
            Console.WriteLine("PASS: новый AnalyticsService зарегистрирован и получил событие.");

            AssertSuccess(await SendAsync(new Request
            {
                Operation = "SetBrokerAvailability",
                Data = JsonSerializer.Serialize(new { Name = "Broker-1", Available = false })
            }), "Отключение основного Broker");
            demoData.MembershipName = $"BROKER-FAILOVER-{unique}";
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "BuyMembership",
                Data = JsonSerializer.Serialize(demoData)
            }), "Публикация через резервный Broker");
            Response brokerFailoverStatus = await SendAsync(new Request { Operation = "PubSubStatus" });
            Assert(brokerFailoverStatus.Message.Contains("Last used: Broker-2"),
                "При отказе Broker-1 событие должно быть опубликовано через Broker-2.");
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "SetBrokerAvailability",
                Data = JsonSerializer.Serialize(new { Name = "Broker-1", Available = true })
            }), "Восстановление основного Broker");
            Console.WriteLine("PASS: при отказе Broker-1 публикация переключается на Broker-2.");

            Response initialStatus = await SendAsync(new Request { Operation = "ReplicationStatus" });
            AssertSuccess(initialStatus, "Проверка состояния Primary/Replica");
            Assert(initialStatus.Message.Contains("Primary: AVAILABLE") && initialStatus.Message.Contains("Replica: AVAILABLE"),
                "В штатном режиме оба экземпляра должны быть доступны.");
            AssertSuccess(await SendAsync(new Request { Operation = "SimulatePrimaryFailure" }), "Искусственный отказ Primary");
            Response failedStatus = await SendAsync(new Request { Operation = "ReplicationStatus" });
            Assert(failedStatus.Message.Contains("Primary: UNAVAILABLE") && failedStatus.Message.Contains("Replica: AVAILABLE"),
                "После отказа Primary должен быть UNAVAILABLE, а Replica — AVAILABLE.");

            // Read после отказа должен обслуживаться Replica.
            DateTime readTime = DateTime.SpecifyKind(DateTime.Now.AddDays(4).Date.AddHours(19), DateTimeKind.Unspecified);
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "CheckAvailability",
                Data = JsonSerializer.Serialize(new CheckAvailabilityData
                {
                    TrainerLastName = "Иванов",
                    TrainerFirstName = "Иван",
                    TrainingTime = readTime
                })
            }), "Чтение через Replica после Failover");

            string failoverPhone = $"test-{unique}-FAILOVER";
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "RegisterClient",
                Data = JsonSerializer.Serialize(new RegisterClientData
                {
                    LastName = "Резервный",
                    FirstName = "Клиент",
                    MiddleName = "",
                    Phone = failoverPhone
                })
            }), "Изменение данных через Replica после Failover");
            Assert(await ClientExistsAsync(failoverPhone, ReplicaConnectionString), "Во время отказа изменение должно попасть в Replica.");
            Assert(!await ClientExistsAsync(failoverPhone, ConnectionString), "Во время отказа изменения не должны записываться в Primary.");
            Console.WriteLine("PASS: после отказа запросы обслуживаются через Replica.");

            AssertSuccess(await SendAsync(new Request { Operation = "RecoverPrimary" }), "Recovery и синхронизация Replica → Primary");
            Response recoveredStatus = await SendAsync(new Request { Operation = "ReplicationStatus" });
            Assert(recoveredStatus.Message.Contains("Primary: AVAILABLE") && recoveredStatus.Message.Contains("Replica: AVAILABLE"),
                "После Recovery оба экземпляра должны быть доступны.");
            Assert(await ClientExistsAsync(failoverPhone, ConnectionString), "После Recovery данные из Replica должны появиться в Primary.");
            Assert(await ClientExistsAsync(failoverPhone, ReplicaConnectionString), "После Recovery данные должны сохраниться в Replica.");

            string postRecoveryPhone = $"test-{unique}-AFTER-RECOVERY";
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "RegisterClient",
                Data = JsonSerializer.Serialize(new RegisterClientData
                {
                    LastName = "После",
                    FirstName = "Восстановления",
                    MiddleName = "",
                    Phone = postRecoveryPhone
                })
            }), "Контрольное изменение после Recovery");
            Assert(await ClientExistsAsync(postRecoveryPhone, ReplicaConnectionString), "После Recovery репликация Primary → Replica должна продолжить работать.");
            Console.WriteLine("PASS: Primary восстановлен, данные синхронизированы, репликация возобновилась.");

            Console.WriteLine("\nALL TESTS PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nTEST FAILED: {ex.Message}");
            return 1;
        }
    }

    private static async Task RunPubSubUnitTests()
    {
        var cluster = new PubSubCluster();
        var logger = new ProbeSubscriber("UnitLogger", "TrainingBooked", "MembershipPurchased");
        var schedule = new ProbeSubscriber("UnitSchedule", "TrainingBooked");
        var uninterested = new ProbeSubscriber("UnitUninterested", "TrainingCancelled");
        Assert(cluster.RegisterSubscriber(logger), "Регистрация UnitLogger должна пройти.");
        Assert(cluster.RegisterSubscriber(schedule), "Регистрация UnitSchedule должна пройти.");
        Assert(cluster.RegisterSubscriber(uninterested), "Регистрация неподписанного компонента должна пройти.");

        var firstEvent = new DomainEvent
        {
            EventType = "TrainingBooked",
            EntityId = 101,
            ClientId = 7,
            Timestamp = DateTime.UtcNow,
            Data = new Dictionary<string, string> { ["Source"] = "UnitTest" }
        };
        Assert(firstEvent.EventId != Guid.Empty && firstEvent.EntityId == 101 && firstEvent.ClientId == 7,
            "Модель Event должна содержать EventId, EntityId и ClientId.");
        Assert(await cluster.PublishAsync(firstEvent), "Публикация события через доступный Broker должна пройти.");
        Assert(logger.HandledCount == 1 && schedule.HandledCount == 1,
            "Одно событие должно быть доставлено нескольким подписчикам.");
        Assert(uninterested.HandledCount == 0,
            "Подписчик без нужного типа события не должен получить сообщение.");

        cluster.SetSubscriberAvailability("UnitSchedule", false);
        var failedEvent = new DomainEvent { EventType = "TrainingBooked", EntityId = 102, ClientId = 7 };
        Assert(await cluster.PublishAsync(failedEvent), "Ошибка Subscriber не должна считаться отказом Broker.");
        Assert(cluster.GetStatus().Contains("pending=1"),
            "После трёх неудачных попыток сообщение должно остаться в очереди.");
        cluster.SetSubscriberAvailability("UnitSchedule", true);
        await cluster.RetryPendingAsync();
        Assert(schedule.HandledCount == 2 && cluster.GetStatus().Contains("pending=0"),
            "После восстановления подписчика повторная доставка должна очистить очередь.");

        var lateAnalytics = new ProbeSubscriber("UnitAnalytics", "TrainingBooked", "MembershipPurchased");
        Assert(cluster.RegisterSubscriber(lateAnalytics), "Новый Subscriber должен регистрироваться во время работы.");
        var membershipEvent = new DomainEvent { EventType = "MembershipPurchased", EntityId = 201, ClientId = 7 };
        Assert(await cluster.PublishAsync(membershipEvent), "Публикация второго типа события должна пройти.");
        Assert(lateAnalytics.HandledCount == 1 && logger.HandledCount == 3 && uninterested.HandledCount == 0,
            "Новый Subscriber должен получать свои события, один Subscriber — несколько типов событий, Publisher менять не требуется.");

        cluster.SetBrokerAvailability("Broker-1", false);
        var failoverEvent = new DomainEvent { EventType = "MembershipPurchased", EntityId = 202, ClientId = 7 };
        Assert(await cluster.PublishAsync(failoverEvent) && cluster.LastUsedBroker == "Broker-2",
            "При отказе первого Broker публикация должна перейти на второй.");

        cluster.SetBrokerAvailability("Broker-2", false);
        var publisher = new EventPublisher(cluster);
        bool allBrokersDown = await publisher.PublishAsync(
            new DomainEvent { EventType = "TrainingBooked", EntityId = 999, ClientId = 7 });
        Assert(!allBrokersDown && publisher.ErrorCount == 1,
            "Publisher должен вернуть ошибку, если все Broker недоступны.");

        Console.WriteLine("PASS: Pub/Sub unit tests — Event, подписки, фильтрация, fan-out, retry, новый Subscriber, Broker failover и ошибки Publisher.");
    }

    private sealed class ProbeSubscriber : EventSubscriberBase
    {
        public ProbeSubscriber(string name, params string[] subscriptions)
            : base(name, subscriptions, TimeSpan.FromMilliseconds(5)) { }
    }

    private static async Task<Response> SendAsync(Request request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(ServerHost, ServerPort);
        using NetworkStream stream = client.GetStream();
        await JsonTcpHelper.SendAsync(stream, request);
        return await JsonTcpHelper.ReceiveAsync<Response>(stream);
    }

    private static async Task WaitForServerAsync()
    {
        for (int i = 1; i <= 30; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(ServerHost, ServerPort);
                Console.WriteLine("Test runner подключился к серверу.");
                return;
            }
            catch (SocketException)
            {
                if (i == 30) throw new TimeoutException("Сервер не стал доступен в течение 30 секунд.");
                await Task.Delay(1000);
            }
        }
    }

    private static async Task WaitForDatabaseAsync()
    {
        for (int i = 1; i <= 20; i++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync();
                Console.WriteLine("Test runner подключился к PostgreSQL.");
                return;
            }
            catch (NpgsqlException)
            {
                if (i == 20) throw new TimeoutException("PostgreSQL не стал доступен.");
                await Task.Delay(1000);
            }
        }
    }

    private static async Task<long> MembershipCountAsync(string phone, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM memberships m JOIN clients c ON c.id = m.client_id WHERE c.phone = @phone", connection);
        command.Parameters.AddWithValue("phone", phone);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> ClientExistsAsync(string phone, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM clients WHERE phone = @phone)", connection);
        command.Parameters.AddWithValue("phone", phone);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> CurrentMembershipIsNullAsync(string phone, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_membership_id FROM clients WHERE phone = @phone", connection);
        command.Parameters.AddWithValue("phone", phone);
        object? result = await command.ExecuteScalarAsync();
        return result is null || result == DBNull.Value;
    }

    private static async Task<int> SlotAvailablePlacesAsync(DateTime trainingTime, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT s.available_places FROM training_slots s JOIN trainers tr ON tr.id = s.trainer_id " +
            "WHERE tr.last_name = 'Иванов' AND tr.first_name = 'Иван' AND s.training_time = @time", connection);
        command.Parameters.AddWithValue("time", trainingTime);
        object? result = await command.ExecuteScalarAsync();
        if (result is null || result == DBNull.Value) throw new InvalidOperationException("Слот не найден в БД.");
        return Convert.ToInt32(result);
    }

    private static async Task<long> ActiveBookingsAsync(DateTime trainingTime, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM trainings t JOIN training_slots s ON s.id = t.slot_id " +
            "JOIN trainers tr ON tr.id = s.trainer_id WHERE tr.last_name = 'Иванов' AND tr.first_name = 'Иван' " +
            "AND s.training_time = @time AND t.status <> 'Отменена'", connection);
        command.Parameters.AddWithValue("time", trainingTime);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void AssertSuccess(Response response, string operation)
    {
        if (!response.Success) throw new InvalidOperationException($"{operation}: {response.Message}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
