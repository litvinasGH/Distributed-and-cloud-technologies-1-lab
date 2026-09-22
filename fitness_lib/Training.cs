namespace fitness_lib;

// <summary>
/// Запланированная или прошедшая тренировка
/// </summary>
public class Training
{
    private DateTime _trainingTime;
    private string _status = "Запланирована";
    private Client _client;
    private Trainer _trainer;

    /// <summary>
    /// Дата и время проведения тренировки
    /// </summary>
    public DateTime TrainingTime
    {
        get => _trainingTime;
        set
        {
            if (value < DateTime.Now.AddYears(-1))
                throw new ArgumentException("Неверная дата тренировки (слишком старая)");
            _trainingTime = value;
        }
    }

    /// <summary>
    /// Статус ("Запланирована", "Завершена", "Отменена")
    /// </summary>
    public string Status
    {
        get => _status;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Статус не может быть пустым");
            _status = value;
        }
    }

    /// <summary>
    /// Клиент, который записан на тренировку
    /// </summary>
    public Client Client
    {
        get => _client;
        set => _client = value ?? throw new ArgumentException("Клиент для тренировки не задан");
    }

    /// <summary>
    /// Тренер, который проводит занятие
    /// </summary>
    public Trainer Trainer
    {
        get => _trainer;
        set => _trainer = value ?? throw new ArgumentException("Тренер для тренировки не задан");
    }

    public Training(Client client, Trainer trainer, DateTime trainingTime)
    {
        Client = client;
        Trainer = trainer;
        TrainingTime = trainingTime;
        Status = "Запланирована";
    }
}