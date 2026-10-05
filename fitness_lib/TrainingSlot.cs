namespace fitness_lib;

public class TrainingSlot
{
    public required string TrainerLastName { get; init; }
    public required string TrainerFirstName { get; init; }
    public DateTime TrainingTime { get; init; }

    public int Capacity { get; init; } = 1;

    public int AvailablePlaces { get; set; }
}
