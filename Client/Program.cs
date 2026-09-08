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
            Console.WriteLine(
                "Ошибка: сервер недоступен."
            );

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
            Console.WriteLine("Введите операцию:");
            Console.WriteLine("1 - RegisterClient");
            Console.WriteLine("2 - BuyMembership");
            Console.WriteLine("3 - BookTraining");
            Console.WriteLine("4 - CancelTraining");
            Console.WriteLine("0 - Выход");
            Console.Write("Ваш выбор: ");

            string? choice = Console.ReadLine();

            if (choice == "0")
            {
                break;
            }

            Request request;

            switch (choice)
            {
                case "1":
                    RegisterClientData clientData = new()
                    {
                        LastName = "Иванов",
                        FirstName = "Иван",
                        MiddleName = "Иванович",
                        Phone = "+37060000000"
                    };

                    request = new Request
                    {
                        Operation = "RegisterClient",
                        Data = JsonSerializer.Serialize(clientData)
                    };

                    break;

                case "2":
                    BuyMembershipData membershipData = new()
                    {
                        ClientPhone = "+37060000000",
                        MembershipName = "Месячный",
                        Price = 30,
                        DurationDays = 30
                    };

                    request = new Request
                    {
                        Operation = "BuyMembership",
                        Data = JsonSerializer.Serialize(membershipData)
                    };

                    break;

                case "3":
                    BookTrainingData trainingData = new()
                    {
                        ClientPhone = "+37060000000",
                        TrainerLastName = "Иванов",
                        TrainerFirstName = "Иван",
                        TrainingTime = DateTime.Now.AddDays(1)
                    };

                    request = new Request
                    {
                        Operation = "BookTraining",
                        Data = JsonSerializer.Serialize(trainingData)
                    };

                    break;

                case "4":
                    CancelTrainingData cancelData = new()
                    {
                        ClientPhone = "+37060000000",
                        TrainingTime = DateTime.Now.AddDays(1)
                    };

                    request = new Request
                    {
                        Operation = "CancelTraining",
                        Data = JsonSerializer.Serialize(cancelData)
                    };

                    break;

                default:
                    Console.WriteLine("Неизвестный пункт меню.");
                    continue;
            }

            try
            {
                await JsonTcpHelper.SendAsync(
                    stream,
                    request
                );

                Console.WriteLine("Запрос отправлен.");

                Response response =
                    await JsonTcpHelper.ReceiveAsync<Response>(
                        stream
                    );

                Console.WriteLine($"Success: {response.Success}");
                Console.WriteLine($"Message: {response.Message}");
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
}