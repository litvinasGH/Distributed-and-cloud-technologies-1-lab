using System.Net;
using System.Net.Sockets;
using fitness_lib;
using Server;

internal partial class Program
{
    private const int port = 5000;

    static async Task Main()
    {

        FitnessEvents.ClientRegistered += message =>
            Console.WriteLine($"[EVENT] {message}");

        FitnessEvents.TrainingBooked += message =>
            Console.WriteLine($"[EVENT] {message}");

        FitnessEvents.TrainingCancelled += message =>
            Console.WriteLine($"[EVENT] {message}");

        FitnessEvents.MembershipPurchased += message =>
            Console.WriteLine($"[EVENT] {message}");


        TcpListener server = new(IPAddress.Any, port);

        server.Start();

        Console.WriteLine($"Сервер запущен на порту {port}");
        Console.WriteLine("Ожидание подключения клиента...");

        while (true)
        {
            using TcpClient client = await server.AcceptTcpClientAsync();

            Console.WriteLine("Клиент подключился!");

            using NetworkStream stream = client.GetStream();

            RequestHandler handler = new();

            while (client.Connected)
            {
                try
                {
                    Request request =
                        await JsonTcpHelper.ReceiveAsync<Request>(stream);

                    Console.WriteLine(
                        $"Получена операция: {request.Operation}"
                    );

                    Response response =
                        handler.Handle(request);

                    await JsonTcpHelper.SendAsync(
                        stream,
                        response
                    );

                    Console.WriteLine("Ответ отправлен клиенту.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Ошибка при обработке запроса: {ex.Message}"
                    );

                    break;
                }
            }

            Console.WriteLine("Клиент отключился.");
            Console.WriteLine("Ожидание подключения клиента...");
        }
    }
}