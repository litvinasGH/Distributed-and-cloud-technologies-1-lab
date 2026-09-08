using fitness_lib;

namespace Server;

public static class FitnessData
{
    public static List<fitness_lib.Client> Clients { get; } = [];

    public static List<Trainer> Trainers { get; } = [];

    public static List<Membership> Memberships { get; } = [];

    public static List<Training> Trainings { get; } = [];

    public static List<Payment> Payments { get; } = [];

    static FitnessData()
    {
        PersonLFM person1 = new()
        {
            LastName = "Иванов",
            FirstName = "Иван",
            MiddleName = "Иванович"
        };

        Trainer trainer1 = new(
                    person1,
                    "Фитнес",
                    5
                );


        PersonLFM person2 = new()
        {
            LastName = "Петрова",
            FirstName = "Анна",
            MiddleName = "Сергеевна"
        };
    
        Trainer trainer2 = new(
            person2,
            "Йога",
            3
        );

        Trainers.Add(trainer1);
        Trainers.Add(trainer2);
    }
}