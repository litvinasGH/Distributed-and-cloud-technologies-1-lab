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

        var database = new DatabaseService();
        try
        {
            database.WaitUntilAvailable();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось подключиться к PostgreSQL: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        TimeSpan bookingDelay = int.TryParse(Environment.GetEnvironmentVariable("BOOKING_DELAY_SECONDS"), out int seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 30))
            : TimeSpan.FromSeconds(4);
        var handler = new RequestHandler(database, bookingDelay);

        TcpListener server = new(IPAddress.Any, Port);
        server.Start();
        Console.WriteLine($"Сервер фитнес-центра запущен на порту {Port}.");
        Console.WriteLine("Хранилища: PostgreSQL Primary + Replica; репликация после изменения данных.");
        Console.WriteLine($"Состояние: {database.GetStatusMessage()}");
        Console.WriteLine("Общий ресурс: место на тренировке.");
        Console.WriteLine($"Задержка бронирования для демонстрации lock: {bookingDelay.TotalSeconds:0} сек.");
        Console.WriteLine("Ожидание клиентов...\n");

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
                Task clientTask = Task.Run(() => ProcessClientAsync(client, clientId, handler));
                Console.WriteLine($"[{Timestamp()}] Client {clientId} подключён. Task #{clientTask.Id}.");
            }
        }
        catch (ObjectDisposedException) { /* listener закрыт при штатной остановке */ }
        catch (SocketException) { /* listener закрыт при штатной остановке */ }
        finally
        {
            server.Stop();
            Console.WriteLine($"[{Timestamp()}] Сервер остановлен.");
        }
    }

    private static async Task ProcessClientAsync(TcpClient client, int clientId, RequestHandler handler)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            Console.WriteLine($"[{Timestamp()}] Client {clientId} START, Thread={Environment.CurrentManagedThreadId}");
            while (true)
            {
                Request request;
                try { request = await JsonTcpHelper.ReceiveAsync<Request>(stream); }
                catch (EndOfStreamException) { break; }
                catch (IOException ex)
                {
                    Console.WriteLine($"[{Timestamp()}] Client {clientId} — ошибка чтения: {ex.Message}");
                    break;
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[{Timestamp()}] Client {clientId} — некорректный JSON: {ex.Message}");
                    try { await JsonTcpHelper.SendAsync(stream, new Response { Success = false, Message = "Некорректный JSON-запрос." }); }
                    catch { /* соединение могло закрыться */ }
                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Timestamp()}] Client {clientId} — ошибка получения запроса: {ex.Message}");
                    break;
                }

                DateTime start = DateTime.Now;
                int taskId = Task.CurrentId ?? -1;
                Console.WriteLine($"[{Timestamp()}] Client {clientId} → START {request.Operation}, TaskId={taskId}");

                Response response;
                try { response = await handler.HandleAsync(request, clientId, taskId); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Timestamp()}] Необработанная ошибка операции {request.Operation}: {ex}");
                    response = new Response { Success = false, Message = $"Ошибка обработки запроса: {ex.Message}" };
                }

                Console.WriteLine($"[{Timestamp()}] Client {clientId} → END {request.Operation}, Success={response.Success}, Elapsed={(DateTime.Now - start).TotalSeconds:F3}s");
                try { await JsonTcpHelper.SendAsync(stream, response); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Timestamp()}] Client {clientId} — ошибка отправки ответа: {ex.Message}");
                    break;
                }
            }
            Console.WriteLine($"[{Timestamp()}] Client {clientId} отключён.");
        }
    }

    private static void SubscribeToEvents()
    {
        FitnessEvents.ClientRegistered += message => Console.WriteLine($"[{Timestamp()}] [EVENT] {message}");
        FitnessEvents.TrainingBooked += message => Console.WriteLine($"[{Timestamp()}] [EVENT] {message}");
        FitnessEvents.TrainingCancelled += message => Console.WriteLine($"[{Timestamp()}] [EVENT] {message}");
        FitnessEvents.MembershipPurchased += message => Console.WriteLine($"[{Timestamp()}] [EVENT] {message}");
    }

    public static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}
