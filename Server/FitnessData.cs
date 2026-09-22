using System.Collections.Concurrent;
using fitness_lib;

namespace Server;

public static class FitnessData
{
    public static ConcurrentBag<fitness_lib.Client> Clients { get; } = [];

    public static ConcurrentBag<Trainer> Trainers { get; } = [];

    public static ConcurrentBag<Membership> Memberships { get; } = [];

    public static ConcurrentBag<Training> Trainings { get; } = [];

    /// <summary>
    /// Общие ресурсы — свободные места конкретной тренировки.
    /// ConcurrentDictionary безопасен для получения/создания слота,
    /// но само изменение AvailablePlaces оставлено без синхронизации
    /// специально для демонстрации Race Condition.
    /// </summary>
    public static ConcurrentDictionary<string, TrainingSlot> TrainingSlots { get; } = [];

    static FitnessData()
    {
        PersonLFM person1 = new()
        {
            LastName = "Иванов",
            FirstName = "Иван",
            MiddleName = "Иванович"
        };

        Trainer trainer1 = new(person1, "Фитнес", 5);

        PersonLFM person2 = new()
        {
            LastName = "Петрова",
            FirstName = "Анна",
            MiddleName = "Сергеевна"
        };

        Trainer trainer2 = new(person2, "Йога", 3);

        Trainers.Add(trainer1);
        Trainers.Add(trainer2);
    }

    public static string GetSlotKey(
        string trainerLastName,
        string trainerFirstName,
        DateTime trainingTime)
    {
        return $"{trainerLastName.Trim().ToLowerInvariant()}|" +
               $"{trainerFirstName.Trim().ToLowerInvariant()}|" +
               trainingTime.Ticks;
    }

    public static TrainingSlot GetOrCreateTrainingSlot(
        string trainerLastName,
        string trainerFirstName,
        DateTime trainingTime)
    {
        string key = GetSlotKey(
            trainerLastName,
            trainerFirstName,
            trainingTime);

        return TrainingSlots.GetOrAdd(
            key,
            _ => new TrainingSlot
            {
                TrainerLastName = trainerLastName,
                TrainerFirstName = trainerFirstName,
                TrainingTime = trainingTime,
                Capacity = 1,
                AvailablePlaces = 1
            });
    }

    public static void ResetExperimentData()
    {
        Clients.Clear();
        Memberships.Clear();
        Trainings.Clear();
        TrainingSlots.Clear();

        foreach (Trainer trainer in Trainers)
            trainer.Trainings.Clear();
    }
}
