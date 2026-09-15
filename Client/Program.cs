using System.Net.Sockets;
using System.Text.Json;
using fitness_lib;

internal partial class Program
{
    private const string host = "127.0.0.1";
    private const int port = 5000;

    static async Task Main()
    {
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
            Console.WriteLine(
                $"Ошибка подключения к серверу: {ex.Message}"
            );

            return;
        }

        Console.WriteLine("Клиент подключен!");

        using NetworkStream stream = client.GetStream();

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("\tФИТНЕС-ЦЕНТР");
            Console.WriteLine("1 - Зарегистрировать клиента");
            Console.WriteLine("2 - Купить абонемент");
            Console.WriteLine("3 - Забронировать тренировку");
            Console.WriteLine("4 - Отменить тренировку");
            Console.WriteLine("0 - Выход");
            Console.Write("Выберите операцию: ");

            string? choice = Console.ReadLine();

            if (choice == "0")
            {
                break;
            }

            Request? request = choice switch
            {
                "1" => CreateRegisterClientRequest(),
                "2" => CreateBuyMembershipRequest(),
                "3" => CreateBookTrainingRequest(),
                "4" => CreateCancelTrainingRequest(),
                _ => new Request
                {
                    Operation = "Unknow"
                }
            };

            // if (request == null)
            // {
            //     Console.WriteLine(
            //         "Ошибка: такой операции нет. " +
            //         "Выберите пункт от 0 до 4."
            //     );
            //     continue;
            // }

            try
            {
                await JsonTcpHelper.SendAsync(
                    stream,
                    request
                );

                Console.WriteLine();
                Console.WriteLine("Запрос отправлен.");

                Response response =
                    await JsonTcpHelper.ReceiveAsync<Response>(
                        stream
                    );

                Console.WriteLine(
                    $"Результат: {(response.Success ? "УСПЕШНО" : "ОШИБКА")}"
                );

                Console.WriteLine(
                    $"Сообщение: {response.Message}"
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Ошибка сетевого взаимодействия: {ex.Message}"
                );

                break;
            }
        }

        Console.WriteLine("Клиент завершён.");
    }

    private static Request CreateRegisterClientRequest()
    {
        Console.WriteLine();
        Console.WriteLine("=== Регистрация клиента ===");
        Console.WriteLine("Пример: Иванов");
        string lastName = ReadRequired("Фамилия: ");

        Console.WriteLine("Пример: Иван");
        string firstName = ReadRequired("Имя: ");

        Console.WriteLine("Пример: Иванович");
        string middleName = ReadOptional("Отчество (можно пропустить): ");

        Console.WriteLine("Пример: +37060000000");
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

        Console.WriteLine("Пример: +37060000000");
        string phone = ReadRequired("Номер телефона клиента: ");

        Console.WriteLine("Пример: Месячный");
        string name = ReadRequired("Название абонемента: ");

        decimal price = ReadDecimal(
            "Цена (например, 30): "
        );

        int duration = ReadInt(
            "Срок действия в днях (например, 30): "
        );

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

        Console.WriteLine("Пример: +37060000000");
        string phone = ReadRequired("Номер телефона клиента: ");

        Console.WriteLine("Пример: Иванов");
        string trainerLastName =
            ReadRequired("Фамилия тренера: ");

        Console.WriteLine("Пример: Иван");
        string trainerFirstName =
            ReadRequired("Имя тренера: ");

        Console.WriteLine(
            "Введите дату и время в формате: 09.09.2026 18:30"
        );

        DateTime trainingTime = ReadDateTime(
            "Дата и время тренировки: "
        );

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

        Console.WriteLine("Пример: +37060000000");
        string phone = ReadRequired("Номер телефона клиента: ");

        Console.WriteLine(
            "Введите дату и время той тренировки, " +
            "которую нужно отменить."
        );

        Console.WriteLine(
            "Формат: 09.09.2026 18:30"
        );

        DateTime trainingTime = ReadDateTime(
            "Дата и время тренировки: "
        );

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

    private static string ReadRequired(string message)
    {
        while (true)
        {
            Console.Write(message);

            string? value = Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            Console.WriteLine(
                "Поле не может быть пустым. Попробуйте ещё раз."
            );
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

            if (decimal.TryParse(input, out decimal value) &&
                value >= 0)
            {
                return value;
            }

            Console.WriteLine(
                "Введите корректное число, например: 30"
            );
        }
    }

    private static int ReadInt(string message)
    {
        while (true)
        {
            Console.Write(message);

            string? input = Console.ReadLine();

            if (int.TryParse(input, out int value) &&
                value > 0)
            {
                return value;
            }

            Console.WriteLine(
                "Введите положительное целое число, например: 30"
            );
        }
    }

    private static DateTime ReadDateTime(string message)
    {
        while (true)
        {
            Console.Write(message);

            string? input = Console.ReadLine();

            if (DateTime.TryParse(input, out DateTime value))
            {
                return value;
            }

            Console.WriteLine(
                "Неверный формат. Пример: 09.09.2026 18:30"
            );
        }
    }
}