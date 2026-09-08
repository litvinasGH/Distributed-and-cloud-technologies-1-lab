namespace fitness_lib;

/// <summary>
/// Вспомогательный класс для ФИО (Фамилия, Имя, Отчество)
/// </summary>
public class PersonLFM
{
    private string _lastName = string.Empty;
    private string _firstName = string.Empty;
    private string _middleName = string.Empty;

    public required string LastName
    {
        get => _lastName;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Фамилия не может быть пустой");
            _lastName = value;
        }
    }

    public required string FirstName
    {
        get => _firstName;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Имя не может быть пустым");
            _firstName = value;
        }
    }

    public string MiddleName
    {
        get => _middleName;
        set => _middleName = value ?? string.Empty;
    }

    public PersonLFM() { }

    public PersonLFM(string lastName, string firstName, string middleName = "")
    {
        LastName = lastName;
        FirstName = firstName;
        MiddleName = middleName;
    }
}