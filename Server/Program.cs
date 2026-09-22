using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using fitness_lib;

namespace Server;

internal class Program
{
    private const int Port = 5000;
    private static int _clientCounter;

    static async Task Main()
    {
        SubscribeToEvents();

        TcpListener server = new(IPAddress.Any, Port);
        server.Start();

        Console.WriteLine($"Сервер запущен на порту {Port}");
        Console.WriteLine("Задержка бронирования для эксперимента: 10 секунд.");
        Console.WriteLine("Ожидание подключения клиента...\n");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            server.Stop();
        };

        try
        {
            while (true)
            {
                TcpClient client = await server.AcceptTcpClientAsync();
                int clientId = Interlocked.Increment(ref _clientCounter);

                Task clientTask = Task.Run(async () =>
                {
                    int taskId = Task.CurrentId ?? -1;
                    await ProcessClientAsync(client, clientId, taskId);
                });

                Console.WriteLine(
                    $"[{Timestamp()}] Client {clientId} подключён. " +
                    $"Создана Task #{clientTask.Id}.");
            }
        }
        catch (ObjectDisposedException)
        {
            // Сервер был остановлен.
        }
        catch (SocketException)
        {
            // Listener был закрыт во время остановки.
        }
        finally
        {
            server.Stop();
            Console.WriteLine($"[{Timestamp()}] Сервер остановлен.");
        }
    }

    private static async Task ProcessClientAsync(
        TcpClient client,
        int clientId,
        int taskId)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            Console.WriteLine(
                $"[{Timestamp()}] Client {clientId} START. " +
                $"TaskId={taskId}, Thread={Environment.CurrentManagedThreadId}");

            RequestHandler handler = new();

            while (true)
            {
                Request request;

                try
                {
                    request = await JsonTcpHelper.ReceiveAsync<Request>(stream);
                }
                catch (EndOfStreamException)
                {
                    Console.WriteLine(
                        $"[{Timestamp()}] Client {clientId} отключился во время работы. " +
                        $"TaskId={taskId}, Thread={Environment.CurrentManagedThreadId}");
                    break;
                }
                catch (IOException ex)
                {
                    Console.WriteLine(
                        $"[{Timestamp()}] Client {clientId} — ошибка чтения: {ex.Message}");
                    break;
                }
                catch (SocketException ex)
                {
                    Console.WriteLine(
                        $"[{Timestamp()}] Client {clientId} — сетевая ошибка: {ex.Message}");
                    break;
                }
                catch (JsonException ex)
                {
                    Console.WriteLine(
                        $"[{Timestamp()}] Client {clientId} — некорректный JSON: {ex.Message}");

                    try
                    {
                        await JsonTcpHelper.SendAsync(
                            stream,
                            new Response
                            {
                                Success = false,
                                Message = "Некорректный JSON-запрос"
                            });
                    }
                    catch
                    {
                        // Клиент мог уже отключиться.
                    }

                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[{Timestamp()}] Client {clientId} — ошибка получения запроса: {ex.Message}");
                    break;
                }

                string operation = request.Operation;
                DateTime start = DateTime.Now;

                Console.WriteLine(
                    $"[{Timestamp()}] Client {clientId} → START " +
                    $"Operation={operation}, TaskId={taskId}, " +
                    $"Thread={Environment.CurrentManagedThreadId}");

                Response response;

                try
                {
                    response = await handler.HandleAsync(request, clientId, taskId);
                }
                catch (Exception ex)
                {
                    response = new Response
                    {
                        Success = false,
                        Message = $"Ошибка обработки: {ex.Message}"
                    };
                }

                TimeSpan elapsed = DateTime.Now - start;

                Console.WriteLine(
                    $"[{Timestamp()}] Client {clientId} → END " +
                    $"Operation={operation}, Success={response.Success}, " +
                    $"Elapsed={elapsed.TotalSeconds:F3}s, " +
                    $"TaskId={taskId}, Thread={Environment.CurrentManagedThreadId}");

                try
                {
                    await JsonTcpHelper.SendAsync(stream, response);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[{Timestamp()}] Client {clientId} — ошибка отправки ответа: {ex.Message}");
                    break;
                }
            }

            Console.WriteLine(
                $"[{Timestamp()}] Client {clientId} завершён. " +
                $"TaskId={taskId}, Thread={Environment.CurrentManagedThreadId}");
        }
    }

    private static void SubscribeToEvents()
    {
        FitnessEvents.ClientRegistered += message =>
            Console.WriteLine($"[{Timestamp()}] [EVENT] {message}");

        FitnessEvents.TrainingBooked += message =>
            Console.WriteLine($"[{Timestamp()}] [EVENT] {message}");

        FitnessEvents.TrainingCancelled += message =>
            Console.WriteLine($"[{Timestamp()}] [EVENT] {message}");

        FitnessEvents.MembershipPurchased += message =>
            Console.WriteLine($"[{Timestamp()}] [EVENT] {message}");
    }

    public static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}
