namespace fitness_lib;
/// <summary>
/// Тарифный план / Абонемент
/// </summary>
public class Membership
{
    private string _name = string.Empty;
    private decimal _price;
    private int _durationDays;

    /// <summary>
    /// Название абонемента (например, "Годовой Безлимит")
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Название абонемента не может быть пустым");
            _name = value;
        }
    }

    /// <summary>
    /// Стоимость абонемента
    /// </summary>
    public decimal Price
    {
        get => _price;
        set
        {
            if (value < 0)
                throw new ArgumentException("Цена не может быть меньше нуля");
            _price = value;
        }
    }

    /// <summary>
    /// Срок действия абонемента в днях
    /// </summary>
    public int DurationDays
    {
        get => _durationDays;
        set
        {
            if (value <= 0)
                throw new ArgumentException("Срок действия должен быть больше 0 дней");
            _durationDays = value;
        }
    }

    public Membership(string name, decimal price, int durationDays)
    {
        Name = name;
        Price = price;
        DurationDays = durationDays;
    }
}
