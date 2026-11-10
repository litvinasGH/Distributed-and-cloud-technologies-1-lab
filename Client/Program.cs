using System.Net.Sockets;
using System.Text.Json;
using fitness_lib;

internal partial class Program
{
    private static readonly string host = Environment.GetEnvironmentVariable("SERVER_HOST") ?? "127.0.0.1";
    private static readonly int port = int.TryParse(Environment.GetEnvironmentVariable("SERVER_PORT"), out int configuredPort) ? configuredPort : 5000;

    // Моделируем часы клиента. Системное время компьютера НЕ изменяется.
    private static TimeSpan clockOffset = TimeSpan.FromSeconds(7);
    private static TimeSpan synchronizationCorrection = TimeSpan.Zero;

    static async Task Main(string[] args)
    {
        if (args.Length > 0 && double.TryParse(
            args[0],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double offsetSeconds))
        {
            clockOffset = TimeSpan.FromSeconds(offsetSeconds);
        }

        using TcpClient client = new();
        Console.WriteLine("Подключение к серверу...");

        try
        {
            await client.ConnectAsync(host, port);
        }
        catch (SocketException)
        {
            Console.WriteLine("Ошибка: сервер недоступен.");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка подключения к серверу: {ex.Message}");
            return;
        }

        Console.WriteLine("Клиент подключен!");

        using NetworkStream stream = client.GetStream();

        string[] menuItems =
        {
            "1 - Зарегистрировать клиента",
            "2 - Купить абонемент",
            "3 - Забронировать тренировку",
            "4 - Отменить тренировку",
            "5 - Проверить свободные места на тренировке",
            "6 - Синхронизировать время с сервером",
            "7 - Демо ошибки БЕЗ транзакции (частичное изменение)",
            "8 - Демо ошибки С транзакцией (ROLLBACK)",
            "9 - Отправить некорректный запрос (тест ошибки)",
            "10 - Показать состояние Primary / Replica",
            "11 - Имитировать отказ Primary (Failover на Replica)",
            "12 - Восстановить Primary и синхронизировать данные",
            "13 - Показать состояние Pub/Sub",
            "14 - Отключить ScheduleService (имитация отказа)",
            "15 - Восстановить ScheduleService",
            "16 - Добавить AnalyticsService",
            "17 - Повторить доставку событий из очереди",
            "18 - Отключить Broker-1",
            "19 - Восстановить Broker-1",
            "20 - Показать состояние узлов консенсуса",
            "21 - Запустить выбор лидера",
            "22 - Имитировать отказ узла",
            "23 - Восстановить узел",
            "24 - Проверить heartbeat всех узлов",
            "25 - Предложить значение и провести голосование",
            "26 - Установить голос узла YES / NO",
            "27 - Сбросить состояние кластера",
            "0 - Выход"
        };

        int selectedIndex = 0;
        Console.CursorVisible = false;

        while (true)
        {
            Console.Clear();
            Console.WriteLine("\tФИТНЕС-ЦЕНТР");
            Console.WriteLine();
            Console.WriteLine("Используйте ↑/↓ для навигации, Enter для выбора:");
            Console.WriteLine();

            for (int i = 0; i < menuItems.Length; i++)
            {
                if (i == selectedIndex)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"> {menuItems[i]}");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine($"  {menuItems[i]}");
                }
            }

            ConsoleKeyInfo key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.UpArrow)
            {
                selectedIndex = (selectedIndex - 1 + menuItems.Length) % menuItems.Length;
                continue;
            }
            if (key.Key == ConsoleKey.DownArrow)
            {
                selectedIndex = (selectedIndex + 1) % menuItems.Length;
                continue;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Clear();
                Console.CursorVisible = true;

                string choice = selectedIndex == menuItems.Length - 1
                    ? "0"
                    : (selectedIndex + 1).ToString();

                if (choice == "0")
                    break;

                Request request;

                try
                {
                    request = choice switch
                    {
                        "1" => CreateRegisterClientRequest(),
                        "2" => CreateBuyMembershipRequest(),
                        "3" => CreateBookTrainingRequest(),
                        "4" => CreateCancelTrainingRequest(),
                        "5" => CreateCheckAvailabilityRequest(),
                        "6" => CreateTimeRequest(),
                        "7" => CreateTransactionDemoRequest("DemoWithoutTransaction"),
                        "8" => CreateTransactionDemoRequest("DemoWithRollback"),
                        "9" => new Request { Operation = "UnknownOperation", Data = "{\"invalid\":true}" },
                        "10" => CreateSimpleRequest("ReplicationStatus"),
                        "11" => CreateSimpleRequest("SimulatePrimaryFailure"),
                        "12" => CreateSimpleRequest("RecoverPrimary"),
                        "13" => CreateSimpleRequest("PubSubStatus"),
                        "14" => CreateAvailabilityRequest("ScheduleService", false),
                        "15" => CreateAvailabilityRequest("ScheduleService", true),
                        "16" => CreateSimpleRequest("RegisterAnalyticsSubscriber"),
                        "17" => CreateSimpleRequest("RetryPendingEvents"),
                        "18" => CreateBrokerAvailabilityRequest("Broker-1", false),
                        "19" => CreateBrokerAvailabilityRequest("Broker-1", true),
                        "20" => CreateSimpleRequest("ConsensusStatus"),
                        "21" => CreateSimpleRequest("ElectLeader"),
                        "22" => CreateConsensusNodeAvailabilityRequest(false),
                        "23" => CreateConsensusNodeAvailabilityRequest(true),
                        "24" => CreateSimpleRequest("CheckHeartbeats"),
                        "25" => CreateConsensusProposalRequest(),
                        "26" => CreateConsensusVoteRequest(),
                        "27" => CreateSimpleRequest("ResetConsensus"),
                        _ => throw new ArgumentException("Такой пункт меню отсутствует.")
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка ввода: {ex.Message}");
                    Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                    Console.ReadKey(true);
                    Console.CursorVisible = false;
                    continue;
                }

                try
                {
                    if (request.Operation == "TimeRequest")
                    {
                        await SynchronizeTimeAsync(stream);
                        Console.WriteLine();
                        Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                        Console.ReadKey(true);
                        Console.CursorVisible = false;
                        continue;
                    }

                    await JsonTcpHelper.SendAsync(stream, request);
                    Console.WriteLine("Запрос отправлен.");

                    Response response = await JsonTcpHelper.ReceiveAsync<Response>(stream);
                    Console.WriteLine($"Результат: {(response.Success ? "УСПЕШНО" : "ОШИБКА")}");
                    Console.WriteLine($"Сообщение: {response.Message}");

                    Console.WriteLine();
                    Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                    Console.ReadKey(true);
                    Console.CursorVisible = false;
                }
                catch (EndOfStreamException)
                {
                    Console.WriteLine("Сервер закрыл соединение.");
                    break;
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"Сетевая ошибка: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка сетевого взаимодействия: {ex.Message}");
                    break;
                }
            }
        }

        Console.CursorVisible = true;
        Console.WriteLine("Клиент завершён.");
    }

    private static Request CreateTimeRequest() => new() { Operation = "TimeRequest", Data = null };
    private static Request CreateSimpleRequest(string operation) => new() { Operation = operation, Data = null };

    private static Request CreateAvailabilityRequest(string subscriberName, bool available) => new()
    {
        Operation = "SetSubscriberAvailability",
        Data = JsonSerializer.Serialize(new { Name = subscriberName, Available = available })
    };

    private static Request CreateBrokerAvailabilityRequest(string brokerName, bool available) => new()
    {
        Operation = "SetBrokerAvailability",
        Data = JsonSerializer.Serialize(new { Name = brokerName, Available = available })
    };

    private static Request CreateConsensusNodeAvailabilityRequest(bool available)
    {
        Console.WriteLine();
        Console.WriteLine(available ? "=== Восстановление узла ===" : "=== Имитация отказа узла ===");
        int nodeId = ReadInt("ID узла (1–5): ");
        if (nodeId > 5) throw new ArgumentException("В этой лабораторной используется пять узлов: ID от 1 до 5.");
        return new Request
        {
            Operation = "SetConsensusNodeAvailability",
            Data = JsonSerializer.Serialize(new { NodeId = nodeId, Available = available })
        };
    }

    private static Request CreateConsensusProposalRequest()
    {
        Console.WriteLine();
        Console.WriteLine("=== Предложение значения для консенсуса ===");
        string value = ReadRequired("Введите значение (например, TrainingStatus=Confirmed): ");
        return new Request
        {
            Operation = "ProposeConsensus",
            Data = JsonSerializer.Serialize(new { Value = value })
        };
    }

    private static Request CreateConsensusVoteRequest()
    {
        Console.WriteLine();
        Console.WriteLine("=== Настройка голоса узла ===");
        int nodeId = ReadInt("ID узла (1–5): ");
        if (nodeId > 5) throw new ArgumentException("В этой лабораторной используется пять узлов: ID от 1 до 5.");
        string vote;
        do
        {
            vote = ReadRequired("Голос YES или NO: ").ToUpperInvariant();
            if (vote is not ("YES" or "NO")) Console.WriteLine("Введите YES или NO.");
        } while (vote is not ("YES" or "NO"));

        return new Request
        {
            Operation = "SetConsensusNodeVote",
            Data = JsonSerializer.Serialize(new { NodeId = nodeId, VoteYes = vote == "YES" })
        };
    }

    private static Request CreateTransactionDemoRequest(string operation)
    {
        Console.WriteLine();
        Console.WriteLine(operation == "DemoWithoutTransaction"
            ? "=== Ошибка без транзакции: частичное изменение ==="
            : "=== Ошибка в транзакции: проверка ROLLBACK ===");

        BuyMembershipData data = new()
        {
            ClientPhone = ReadRequired("Телефон зарегистрированного клиента: "),
            MembershipName = ReadRequired("Название тестового абонемента: "),
            Price = ReadDecimal("Цена (например, 30): "),
            DurationDays = ReadInt("Срок действия в днях (например, 30): ")
        };

        return new Request
        {
            Operation = operation,
            Data = JsonSerializer.Serialize(data)
        };
    }

    private static async Task SynchronizeTimeAsync(NetworkStream stream)
    {
        Console.WriteLine();
        Console.WriteLine("=== Синхронизация времени ===");

        DateTime localBefore = GetLocalTime();
        Console.WriteLine($"Локальное время клиента ДО синхронизации: {localBefore:HH:mm:ss.fff} UTC");
        Console.WriteLine($"Искусственное смещение клиента: {clockOffset.TotalSeconds:+0;-0;0} сек.");

        DateTime requestTime = DateTime.UtcNow;
        await JsonTcpHelper.SendAsync(stream, CreateTimeRequest());
        Console.WriteLine($"Request time:  {requestTime:HH:mm:ss.fff} UTC");

        Response response = await JsonTcpHelper.ReceiveAsync<Response>(stream);
        DateTime responseTime = DateTime.UtcNow;
        TimeSpan rtt = responseTime - requestTime;
        TimeSpan networkDelay = TimeSpan.FromTicks(rtt.Ticks / 2);

        if (!response.Success || response.Data == null)
        {
            Console.WriteLine($"Ошибка синхронизации: {response.Message}");
            return;
        }

        TimeResponseData? data = response.Data is JsonElement element
            ? element.Deserialize<TimeResponseData>()
            : null;

        if (data == null)
        {
            Console.WriteLine("Ошибка: некорректные данные времени от сервера.");
            return;
        }

        DateTime serverTime = data.ServerTime;
        DateTime localAtResponse = GetLocalTime();

        DateTime estimatedServerTime = serverTime + networkDelay;
        TimeSpan calculatedOffset = estimatedServerTime - localAtResponse;
        synchronizationCorrection = calculatedOffset;
        DateTime correctedTime = localAtResponse + calculatedOffset;
        TimeSpan differenceBefore = localBefore - serverTime;
        TimeSpan differenceAfter = correctedTime - estimatedServerTime;

        Console.WriteLine($"Server time:                 {serverTime:HH:mm:ss.fff} UTC");
        Console.WriteLine($"Response time:               {responseTime:HH:mm:ss.fff} UTC");
        Console.WriteLine($"RTT:                         {rtt.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Estimated network delay:     {networkDelay.TotalMilliseconds:F0} ms (RTT/2)");
        Console.WriteLine($"Calculated offset:           {calculatedOffset.TotalMilliseconds:+0;-0;0} ms");
        Console.WriteLine($"Corrected time:              {correctedTime:HH:mm:ss.fff} UTC");
        Console.WriteLine($"Difference BEFORE:           {differenceBefore.TotalMilliseconds:+0;-0;0} ms");
        Console.WriteLine($"Difference AFTER:            {differenceAfter.TotalMilliseconds:+0;-0;0} ms");
        Console.WriteLine($"Artificial server delay:     {data.ArtificialDelayMs} ms");
    }

    private static DateTime GetLocalTime() => DateTime.UtcNow + clockOffset + synchronizationCorrection;

    private static Request CreateRegisterClientRequest()
    {
        Console.WriteLine();
        Console.WriteLine("=== Регистрация клиента ===");

        string lastName = ReadRequired("Фамилия: ");
        string firstName = ReadRequired("Имя: ");
        string middleName = ReadOptional("Отчество (можно пропустить): ");
        string phone = ReadRequired("Номер телефона: ");

        RegisterClientData data = new()
        {
            LastName = lastName,
            FirstName = firstName,
            MiddleName = middleName,
            Phone = phone
        };

        return new Request
        {
            Operation = "RegisterClient",
            Data = JsonSerializer.Serialize(data)
        };
    }

    private static Request CreateBuyMembershipRequest()
    {
        Console.WriteLine();
        Console.WriteLine("=== Покупка абонемента ===");

        string phone = ReadRequired("Номер телефона клиента: ");
        string name = ReadRequired("Название абонемента: ");
        decimal price = ReadDecimal("Цена (например, 30): ");
        int duration = ReadInt("Срок действия в днях (например, 30): ");

        BuyMembershipData data = new()
        {
            ClientPhone = phone,
            MembershipName = name,
            Price = price,
            DurationDays = duration
        };

        return new Request
        {
            Operation = "BuyMembership",
            Data = JsonSerializer.Serialize(data)
        };
    }

    private static Request CreateBookTrainingRequest()
    {
        Console.WriteLine();
        Console.WriteLine("=== Бронирование тренировки ===");

        string phone = ReadRequired("Номер телефона клиента: ");
        string trainerLastName = ReadRequired("Фамилия тренера: ");
        string trainerFirstName = ReadRequired("Имя тренера: ");
        DateTime trainingTime = ReadDateTime("Дата и время тренировки: ");

        BookTrainingData data = new()
        {
            ClientPhone = phone,
            TrainerLastName = trainerLastName,
            TrainerFirstName = trainerFirstName,
            TrainingTime = trainingTime
        };

        return new Request
        {
            Operation = "BookTraining",
            Data = JsonSerializer.Serialize(data)
        };
    }

    private static Request CreateCancelTrainingRequest()
    {
        Console.WriteLine();
        Console.WriteLine("=== Отмена тренировки ===");

        string phone = ReadRequired("Номер телефона клиента: ");
        DateTime trainingTime = ReadDateTime("Дата и время тренировки: ");

        CancelTrainingData data = new()
        {
            ClientPhone = phone,
            TrainingTime = trainingTime
        };

        return new Request
        {
            Operation = "CancelTraining",
            Data = JsonSerializer.Serialize(data)
        };
    }

    private static Request CreateCheckAvailabilityRequest()
    {
        Console.WriteLine();
        Console.WriteLine("=== Проверка свободных мест ===");

        string trainerLastName = ReadRequired("Фамилия тренера: ");
        string trainerFirstName = ReadRequired("Имя тренера: ");
        DateTime trainingTime = ReadDateTime("Дата и время тренировки: ");

        CheckAvailabilityData data = new()
        {
            TrainerLastName = trainerLastName,
            TrainerFirstName = trainerFirstName,
            TrainingTime = trainingTime
        };

        return new Request
        {
            Operation = "CheckAvailability",
            Data = JsonSerializer.Serialize(data)
        };
    }

    private static string ReadRequired(string message)
    {
        while (true)
        {
            Console.Write(message);
            string? value = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
            Console.WriteLine("Поле не может быть пустым. Попробуйте ещё раз.");
        }
    }

    private static string ReadOptional(string message)
    {
        Console.Write(message);
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    private static decimal ReadDecimal(string message)
    {
        while (true)
        {
            Console.Write(message);
            string? input = Console.ReadLine();
            if (decimal.TryParse(input, out decimal value) && value >= 0)
                return value;
            Console.WriteLine("Введите корректное число, например: 30");
        }
    }

    private static int ReadInt(string message)
    {
        while (true)
        {
            Console.Write(message);
            string? input = Console.ReadLine();
            if (int.TryParse(input, out int value) && value > 0)
                return value;
            Console.WriteLine("Введите положительное целое число, например: 30");
        }
    }

    private static DateTime ReadDateTime(string message)
    {
        while (true)
        {
            Console.Write(message);
            string? input = Console.ReadLine();
            if (DateTime.TryParse(input, out DateTime value))
                return value;
            Console.WriteLine("Неверный формат. Пример: 25.09.2026 18:30");
        }
    }
}
