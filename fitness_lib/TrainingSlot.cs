namespace fitness_lib;

/// <summary>
/// Общий ресурс варианта 7: свободные места на конкретной тренировке.
/// В ЛР №2 изменение AvailablePlaces намеренно выполняется без синхронизации,
/// чтобы продемонстрировать Race Condition.
/// </summary>
public class TrainingSlot
{
    public required string TrainerLastName { get; init; }
    public required string TrainerFirstName { get; init; }
    public DateTime TrainingTime { get; init; }

    public int Capacity { get; init; } = 1;

    public int AvailablePlaces { get; set; }
}
