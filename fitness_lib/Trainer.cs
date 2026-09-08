namespace fitness_lib;

/// <summary>
/// Тренер фитнес-центра
/// </summary>
public class Trainer
{
    private PersonLFM _lfmn;
    private string _specialization = string.Empty;
    private int _experienceYears;
    private List<Training> _trainings = [];

    /// <summary>
    /// ФИО тренера
    /// </summary>
    public PersonLFM Lfmn
    {
        get => _lfmn;
        set
        {
            _lfmn = value ?? throw new ArgumentException("ФИО тренера не может быть null");
        }
    }

    /// <summary>
    /// Специализация (например, "Йога", "Кроссфит")
    /// </summary>
    public string Specialization
    {
        get => _specialization;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Специализация не может быть пустой");
            _specialization = value;
        }
    }

    /// <summary>
    /// Опыт работы в годах
    /// </summary>
    public int ExperienceYears
    {
        get => _experienceYears;
        set
        {
            if (value < 0)
                throw new ArgumentException("Опыт работы не может быть отрицательным");
            _experienceYears = value;
        }
    }

    /// <summary>
    /// Список тренировок, которые ведет этот тренер
    /// </summary>
    public List<Training> Trainings
    {
        get => _trainings;
        set => _trainings = value ?? throw new ArgumentException("Список тренировок не может быть null");
    }

    public Trainer(PersonLFM lfmn, string specialization, int experienceYears)
    {
        Lfmn = lfmn;
        Specialization = specialization;
        ExperienceYears = experienceYears;
    }
}