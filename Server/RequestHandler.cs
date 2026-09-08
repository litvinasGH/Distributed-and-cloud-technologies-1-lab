using System.Text.Json;
using fitness_lib;

namespace Server;

public class RequestHandler
{
    public Response Handle(Request request)
    {
        return request.Operation switch
        {
            "RegisterClient" => RegisterClient(request),
            "BookTraining" => BookTraining(request),
            "CancelTraining" => CancelTraining(request),
            "BuyMembership" => BuyMembership(request),

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
        {
            return new Response
            {
                Success = false,
                Message = "Данные клиента не переданы"
            };
        }

        try
        {
            RegisterClientData? data =
                JsonSerializer.Deserialize<RegisterClientData>(
                    request.Data
                );

            if (data == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Некорректные данные клиента"
                };
            }

            PersonLFM person = new()
            {
                LastName = data.LastName,
                FirstName = data.FirstName,
                MiddleName = data.MiddleName
            };

            Client client = new(
                person,
                data.Phone
            );

            FitnessData.Clients.Add(client);

            // Событие регистрации клиента
            FitnessEvents.OnClientRegistered(
                $"Зарегистрирован клиент: " +
                $"{client.Lfmn.LastName} {client.Lfmn.FirstName}"
            );

            return new Response
            {
                Success = true,
                Message = "Клиент успешно зарегистрирован"
            };
        }
        catch (Exception ex)
        {
            return new Response
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    private Response BookTraining(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data))
        {
            return new Response
            {
                Success = false,
                Message = "Данные тренировки не переданы"
            };
        }

        try
        {
            BookTrainingData? data =
                JsonSerializer.Deserialize<BookTrainingData>(
                    request.Data
                );

            if (data == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Некорректные данные тренировки"
                };
            }

            Client? client = FitnessData.Clients
                .FirstOrDefault(c => c.Number == data.ClientPhone);

            if (client == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Клиент не найден"
                };
            }

            Trainer? trainer = FitnessData.Trainers
                .FirstOrDefault(t =>
                    t.Lfmn.LastName == data.TrainerLastName &&
                    t.Lfmn.FirstName == data.TrainerFirstName);

            if (trainer == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Тренер не найден"
                };
            }

            Training training = new(
                client,
                trainer,
                data.TrainingTime
            );

            FitnessData.Trainings.Add(training);

            client.Trainings.Add(training);

            trainer.Trainings.Add(training);

            // Событие бронирования тренировки
            FitnessEvents.OnTrainingBooked(
                $"Забронирована тренировка: " +
                $"{client.Lfmn.LastName} {client.Lfmn.FirstName} " +
                $"с тренером " +
                $"{trainer.Lfmn.LastName} {trainer.Lfmn.FirstName} " +
                $"на {training.TrainingTime}"
            );

            return new Response
            {
                Success = true,
                Message = "Тренировка успешно забронирована"
            };
        }
        catch (Exception ex)
        {
            return new Response
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    private Response CancelTraining(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data))
        {
            return new Response
            {
                Success = false,
                Message = "Данные тренировки не переданы"
            };
        }

        try
        {
            CancelTrainingData? data =
                JsonSerializer.Deserialize<CancelTrainingData>(
                    request.Data
                );

            if (data == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Некорректные данные тренировки"
                };
            }

            Client? client = FitnessData.Clients
                .FirstOrDefault(c => c.Number == data.ClientPhone);

            if (client == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Клиент не найден"
                };
            }

            Training? training = client.Trainings
                .FirstOrDefault(t =>
                    t.TrainingTime == data.TrainingTime);

            if (training == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Тренировка не найдена"
                };
            }

            if (training.Status == "Отменена")
            {
                return new Response
                {
                    Success = false,
                    Message = "Тренировка уже отменена"
                };
            }

            training.Status = "Отменена";

            // Событие отмены тренировки
            FitnessEvents.OnTrainingCancelled(
                $"Отменена тренировка: " +
                $"{client.Lfmn.LastName} {client.Lfmn.FirstName} " +
                $"на {training.TrainingTime}"
            );

            return new Response
            {
                Success = true,
                Message = "Тренировка успешно отменена"
            };
        }
        catch (Exception ex)
        {
            return new Response
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    private Response BuyMembership(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Data))
        {
            return new Response
            {
                Success = false,
                Message = "Данные абонемента не переданы"
            };
        }

        try
        {
            BuyMembershipData? data =
                JsonSerializer.Deserialize<BuyMembershipData>(
                    request.Data
                );

            if (data == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Некорректные данные абонемента"
                };
            }

            Client? client = FitnessData.Clients
                .FirstOrDefault(c => c.Number == data.ClientPhone);

            if (client == null)
            {
                return new Response
                {
                    Success = false,
                    Message = "Клиент не найден"
                };
            }

            Membership membership = new(
                data.MembershipName,
                data.Price,
                data.DurationDays
            );

            client.Membership = membership;

            FitnessData.Memberships.Add(membership);

            // Событие покупки абонемента
            FitnessEvents.OnMembershipPurchased(
                $"Клиент " +
                $"{client.Lfmn.LastName} {client.Lfmn.FirstName} " +
                $"приобрёл абонемент \"{membership.Name}\""
            );

            return new Response
            {
                Success = true,
                Message =
                    $"Абонемент \"{membership.Name}\" успешно приобретён"
            };
        }
        catch (Exception ex)
        {
            return new Response
            {
                Success = false,
                Message = ex.Message
            };
        }
    }
}