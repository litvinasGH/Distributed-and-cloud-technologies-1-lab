namespace fitness_lib;


/// <summary>
/// Клиент фитнес-центра
/// </summary>
public class Client
{
    private PersonLFM _lfmn;
    private DateTime _registered_datetime;
    private string _number = string.Empty;

    // Списки для связей
    private List<Training> _trainings = new List<Training>();
    private List<Payment> _payments = new List<Payment>();
    private Membership? _membership;

    /// <summary>
    /// ФИО клиента
    /// </summary>
    public PersonLFM Lfmn
    {
        get => _lfmn;
        set
        {
            _lfmn = value ?? throw new ArgumentException("ФИО не может быть null");
        }
    }

    /// <summary>
    /// Дата регистрации клиента
    /// </summary>
    public DateTime Registered_dateTime
    {
        get => _registered_datetime;
        set
        {
            if (value > DateTime.Now.AddDays(1))
                throw new ArgumentException("Дата регистрации не может быть в будущем");
            _registered_datetime = value;
        }
    }

    /// <summary>
    /// Номер телефона
    /// </summary>
    public string Number
    {
        get => _number;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Телефон не может быть пустым");
            _number = value;
        }
    }

    /// <summary>
    /// Текущий абонемент клиента (может отсутствовать)
    /// </summary>
    public Membership? Membership
    {
        get => _membership;
        set => _membership = value; 
    }

    /// <summary>
    /// Список тренировок клиента
    /// </summary>
    public List<Training> Trainings
    {
        get => _trainings;
        set => _trainings = value ?? [];
    }

    /// <summary>
    /// История оплат клиента
    /// </summary>
    public List<Payment> Payments
    {
        get => _payments;
        set => _payments = value ?? throw new ArgumentException("Список оплат не может быть null");
    }

    public Client(PersonLFM lfmn, string number)
    {
        Lfmn = lfmn;
        Number = number;
        Registered_dateTime = DateTime.Now;
    }

    public Client(PersonLFM lfmn, string number, DateTime registered_datetime)
    {
        Lfmn = lfmn;
        Number = number;
        Registered_dateTime = registered_datetime;
    }
}