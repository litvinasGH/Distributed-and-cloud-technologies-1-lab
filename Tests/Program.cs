using System.Net.Sockets;
using System.Text.Json;
using fitness_lib;
using Npgsql;

internal static class Program
{
    private static readonly string ServerHost = Environment.GetEnvironmentVariable("SERVER_HOST") ?? "localhost";
    private static readonly int ServerPort = int.TryParse(Environment.GetEnvironmentVariable("SERVER_PORT"), out int port) ? port : 5000;
    private static readonly string ConnectionString = new NpgsqlConnectionStringBuilder
    {
        Host = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost",
        Port = int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out int dbPort) ? dbPort : 5432,
        Database = Environment.GetEnvironmentVariable("DB_NAME") ?? "fitness_db",
        Username = Environment.GetEnvironmentVariable("DB_USER") ?? "fitness_user",
        Password = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "fitness_pass",
        Timeout = 5
    }.ConnectionString;

    public static async Task<int> Main()
    {
        try
        {
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
            Console.WriteLine("PASS: регистрация клиентов и сохранение в PostgreSQL.");

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
            Assert(await CurrentMembershipIsNullAsync(phone1), "После ROLLBACK current_membership_id должен оставаться NULL.");
            Console.WriteLine("PASS: ROLLBACK отменяет вставку и не оставляет частичного изменения.");

            demoData.MembershipName = $"COMMIT-{unique}";
            AssertSuccess(await SendAsync(new Request
            {
                Operation = "BuyMembership",
                Data = JsonSerializer.Serialize(demoData)
            }), "Покупка абонемента с COMMIT");
            Assert(await MembershipCountAsync(phone1) == 2, "После COMMIT должна появиться запись абонемента.");
            Assert(!await CurrentMembershipIsNullAsync(phone1), "После COMMIT абонемент должен быть назначен клиенту.");
            Console.WriteLine("PASS: COMMIT сохраняет обе связанные операции.");

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
            Assert(await ActiveBookingsAsync(trainingTime) == 1, "В БД должна быть одна активная запись на слот.");
            Console.WriteLine("PASS: два конкурентных клиента — ровно один SUCCESS и один FAIL.");

            AssertSuccess(await SendAsync(new Request
            {
                Operation = "CancelTraining",
                Data = JsonSerializer.Serialize(new CancelTrainingData { ClientPhone = winnerPhone, TrainingTime = trainingTime })
            }), "Отмена бронирования");
            Assert(await SlotAvailablePlacesAsync(trainingTime) == 1, "После отмены свободное место должно вернуться.");
            Assert(await ActiveBookingsAsync(trainingTime) == 0, "После отмены активных записей на слот быть не должно.");
            Console.WriteLine("PASS: отмена бронирования возвращает место.");

            Console.WriteLine("\nALL TESTS PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nTEST FAILED: {ex.Message}");
            return 1;
        }
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

    private static async Task<long> MembershipCountAsync(string phone)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM memberships m JOIN clients c ON c.id = m.client_id WHERE c.phone = @phone", connection);
        command.Parameters.AddWithValue("phone", phone);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> CurrentMembershipIsNullAsync(string phone)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_membership_id FROM clients WHERE phone = @phone", connection);
        command.Parameters.AddWithValue("phone", phone);
        object? result = await command.ExecuteScalarAsync();
        return result is null || result == DBNull.Value;
    }

    private static async Task<int> SlotAvailablePlacesAsync(DateTime trainingTime)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT s.available_places FROM training_slots s JOIN trainers tr ON tr.id = s.trainer_id " +
            "WHERE tr.last_name = 'Иванов' AND tr.first_name = 'Иван' AND s.training_time = @time", connection);
        command.Parameters.AddWithValue("time", trainingTime);
        object? result = await command.ExecuteScalarAsync();
        if (result is null || result == DBNull.Value) throw new InvalidOperationException("Слот не найден в БД.");
        return Convert.ToInt32(result);
    }

    private static async Task<long> ActiveBookingsAsync(DateTime trainingTime)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
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
