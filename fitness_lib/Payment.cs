namespace fitness_lib;

public class Payment
{
    private decimal _amount;
    private DateTime _paymentDate;
    private string _paymentMethod = "Карта";
    private Client _client;

    /// <summary>
    /// Сумма платежа
    /// </summary>
    public decimal Amount
    {
        get => _amount;
        set
        {
            if (value <= 0)
                throw new ArgumentException("Сумма платежа должна быть больше нуля");
            _amount = value;
        }
    }

    /// <summary>
    /// Дата совершения платежа
    /// </summary>
    public DateTime PaymentDate
    {
        get => _paymentDate;
        set
        {
            if (value > DateTime.Now.AddDays(1))
                throw new ArgumentException("Дата платежа не может быть в будущем");
            _paymentDate = value;
        }
    }

    /// <summary>
    /// Способ оплаты (например, "Карта", "Наличные", "СБП")
    /// </summary>
    public string PaymentMethod
    {
        get => _paymentMethod;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Способ оплаты должен быть указан");
            _paymentMethod = value;
        }
    }

    /// <summary>
    /// Клиент, совершивший платеж
    /// </summary>
    public required Client Client
    {
        get => _client;
        set => _client = value ?? throw new ArgumentException("У платежа должен быть клиент");
    }

    public Payment(Client client, decimal amount)
    {
        Client = client;
        Amount = amount;
        PaymentDate = DateTime.Now;
    }

    public Payment(Client client, decimal amount, string paymentMethod, DateTime paymentDate)
    { 
        Client = client; 
        Amount = amount; 
        PaymentMethod = paymentMethod; 
        PaymentDate = paymentDate; 
    }
}
