using System.Text.Json;
using fitness_lib;

namespace Server;

public class RequestHandler
{
    private readonly TimeSpan _bookingDelay;

    public RequestHandler(TimeSpan? bookingDelay = null)
    {
        _bookingDelay = bookingDelay ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Обработка запроса является асинхронной.
    /// Именно BookTraining содержит искусственную задержку 10 секунд,
    /// требуемую заданием для доказательства параллельной работы.
    /// </summary>
    public async Task<Response> HandleAsync(
        Request request,
        int clientId = 0,
        int taskId = -1)
    {
        return request.Operation switch
        {
            "RegisterClient" => RegisterClient(request),
            "BookTraining" => await BookTrainingAsync(request, clientId, taskId),
            "CancelTraining" => CancelTraining(request),
            "BuyMembership" => BuyMembership(request),
            "CheckAvailability" => CheckAvailability(request),

            _ => new Response
            {
                Success = false,
                Message = $"Неизвестная операция: {request.Operation}"
            }
        };
    }

    private Response RegisterClient(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data))
            return Error("Данные клиента не переданы");

        try
        {
            RegisterClientData? data =
                JsonSerializer.Deserialize<RegisterClientData>(request.Data);

            if (data == null)
                return Error("Некорректные данные клиента");

            PersonLFM person = new()
            {
                LastName = data.LastName,
                FirstName = data.FirstName,
                MiddleName = data.MiddleName
            };

            Client client = new(person, data.Phone);

            if (FitnessData.Clients.Any(c => c.Number == client.Number))
                return Error("Клиент с таким номером телефона уже существует");

            FitnessData.Clients.Add(client);

            FitnessEvents.OnClientRegistered(
                $"Зарегистрирован клиент: {client.Lfmn.LastName} {client.Lfmn.FirstName}");

            return Success("Клиент успешно зарегистрирован");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private async Task<Response> BookTrainingAsync(
        Request request,
        int clientId,
        int taskId)
    {
        if (string.IsNullOrWhiteSpace(request.Data))
            return Error("Данные тренировки не переданы");

        try
        {
            BookTrainingData? data =
                JsonSerializer.Deserialize<BookTrainingData>(request.Data);

            if (data == null)
                return Error("Некорректные данные тренировки");

            Client? client = FitnessData.Clients
                .FirstOrDefault(c => c.Number == data.ClientPhone);

            if (client == null)
                return Error("Клиент не найден");

            Trainer? trainer = FitnessData.Trainers
                .FirstOrDefault(t =>
                    t.Lfmn.LastName == data.TrainerLastName &&
                    t.Lfmn.FirstName == data.TrainerFirstName);

            if (trainer == null)
                return Error("Тренер не найден");

            TrainingSlot slot = FitnessData.GetOrCreateTrainingSlot(
                data.TrainerLastName,
                data.TrainerFirstName,
                data.TrainingTime);

            int availableBefore = slot.AvailablePlaces;

            Console.WriteLine(
                $"[{Program.Timestamp()}] Client {clientId} | " +
                $"TaskId={taskId} | SLOT CHECK: " +
                $"{slot.TrainerLastName} {slot.TrainerFirstName}, " +
                $"{slot.TrainingTime:dd.MM.yyyy HH:mm}, " +
                $"AvailablePlaces={availableBefore}");

            if (availableBefore <= 0)
            {
                return Error(
                    "Свободных мест на тренировке нет. " +
                    "Запись отклонена.");
            }

            // ================================================
            // НАМЕРЕННАЯ RACE CONDITION
            Console.WriteLine(
                $"[{Program.Timestamp()}] Client {clientId} | " +
                $"TaskId={taskId} | WAIT 10 sec before updating resource...");

            await Task.Delay(_bookingDelay);

            // Намеренно НЕ используется lock/Interlocked.
            // Это ключевой участок эксперимента Race Condition.
            slot.AvailablePlaces = availableBefore - 1;

            Console.WriteLine(
                $"[{Program.Timestamp()}] Client {clientId} | " +
                $"TaskId={taskId} | SLOT UPDATE: " +
                $"AvailablePlaces={slot.AvailablePlaces}");

            Training training = new(
                client,
                trainer,
                data.TrainingTime);

            FitnessData.Trainings.Add(training);
            client.Trainings.Add(training);
            trainer.Trainings.Add(training);

            int bookings = FitnessData.Trainings.Count(t =>
                t.Trainer.Lfmn.LastName == trainer.Lfmn.LastName &&
                t.Trainer.Lfmn.FirstName == trainer.Lfmn.FirstName &&
                t.TrainingTime == data.TrainingTime &&
                t.Status != "Отменена");

            FitnessEvents.OnTrainingBooked(
                $"Забронирована тренировка: " +
                $"{client.Lfmn.LastName} {client.Lfmn.FirstName}, " +
                $"тренер {trainer.Lfmn.LastName} {trainer.Lfmn.FirstName}, " +
                $"{training.TrainingTime:dd.MM.yyyy HH:mm}");

            return Success(
                "Тренировка успешно забронирована. " +
                $"Свободных мест по счётчику: {slot.AvailablePlaces}. " +
                $"Записей на слот: {bookings}");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private Response CancelTraining(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data))
            return Error("Данные тренировки не переданы");

        try
        {
            CancelTrainingData? data =
                JsonSerializer.Deserialize<CancelTrainingData>(request.Data);

            if (data == null)
                return Error("Некорректные данные тренировки");

            Client? client = FitnessData.Clients
                .FirstOrDefault(c => c.Number == data.ClientPhone);

            if (client == null)
                return Error("Клиент не найден");

            Training? training = client.Trainings
                .FirstOrDefault(t => t.TrainingTime == data.TrainingTime);

            if (training == null)
                return Error("Тренировка не найдена");

            if (training.Status == "Отменена")
                return Error("Тренировка уже отменена");

            training.Status = "Отменена";

            FitnessEvents.OnTrainingCancelled(
                $"Отменена тренировка: " +
                $"{client.Lfmn.LastName} {client.Lfmn.FirstName} " +
                $"на {training.TrainingTime:dd.MM.yyyy HH:mm}");

            return Success("Тренировка успешно отменена");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private Response BuyMembership(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data))
            return Error("Данные абонемента не переданы");

        try
        {
            BuyMembershipData? data =
                JsonSerializer.Deserialize<BuyMembershipData>(request.Data);

            if (data == null)
                return Error("Некорректные данные абонемента");

            Client? client = FitnessData.Clients
                .FirstOrDefault(c => c.Number == data.ClientPhone);

            if (client == null)
                return Error("Клиент не найден");

            Membership membership = new(
                data.MembershipName,
                data.Price,
                data.DurationDays);

            client.Membership = membership;
            FitnessData.Memberships.Add(membership);

            FitnessEvents.OnMembershipPurchased(
                $"Клиент {client.Lfmn.LastName} {client.Lfmn.FirstName} " +
                $"приобрёл абонемент \"{membership.Name}\"");

            return Success(
                $"Абонемент \"{membership.Name}\" успешно приобретён");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private Response CheckAvailability(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data))
            return Error("Данные тренировки не переданы");

        try
        {
            CheckAvailabilityData? data =
                JsonSerializer.Deserialize<CheckAvailabilityData>(request.Data);

            if (data == null)
                return Error("Некорректные данные тренировки");

            Trainer? trainer = FitnessData.Trainers
                .FirstOrDefault(t =>
                    t.Lfmn.LastName == data.TrainerLastName &&
                    t.Lfmn.FirstName == data.TrainerFirstName);

            if (trainer == null)
                return Error("Тренер не найден");

            TrainingSlot slot = FitnessData.GetOrCreateTrainingSlot(
                data.TrainerLastName,
                data.TrainerFirstName,
                data.TrainingTime);

            int bookings = FitnessData.Trainings.Count(t =>
                t.Trainer.Lfmn.LastName == trainer.Lfmn.LastName &&
                t.Trainer.Lfmn.FirstName == trainer.Lfmn.FirstName &&
                t.TrainingTime == data.TrainingTime &&
                t.Status != "Отменена");

            return Success(
                $"Тренер: {trainer.Lfmn.LastName} {trainer.Lfmn.FirstName}; " +
                $"Время: {data.TrainingTime:dd.MM.yyyy HH:mm}; " +
                $"Вместимость: {slot.Capacity}; " +
                $"Свободных мест: {slot.AvailablePlaces}; " +
                $"Записей: {bookings}");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static Response Success(string message) => new()
    {
        Success = true,
        Message = message
    };

    private static Response Error(string message) => new()
    {
        Success = false,
        Message = message
    };
}
