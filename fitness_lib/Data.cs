namespace fitness_lib;

public class RegisterClientData
{
    public string LastName { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string MiddleName { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;
}

public class BuyMembershipData
{
    public string ClientPhone { get; set; } = string.Empty;

    public string MembershipName { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int DurationDays { get; set; }
}

public class BookTrainingData
{
    public string ClientPhone { get; set; } = string.Empty;

    public string TrainerLastName { get; set; } = string.Empty;

    public string TrainerFirstName { get; set; } = string.Empty;

    public DateTime TrainingTime { get; set; }
}

public class CancelTrainingData
{
    public string ClientPhone { get; set; } = string.Empty;

    public DateTime TrainingTime { get; set; }
}