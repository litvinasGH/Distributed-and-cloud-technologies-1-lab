namespace Server;

public static class FitnessEvents
{
    public static event Action<string>? ClientRegistered;
    public static event Action<string>? TrainingBooked;
    public static event Action<string>? TrainingCancelled;
    public static event Action<string>? MembershipPurchased;

    public static void OnClientRegistered(string message)
    {
        ClientRegistered?.Invoke(message);
    }

    public static void OnTrainingBooked(string message)
    {
        TrainingBooked?.Invoke(message);
    }

    public static void OnTrainingCancelled(string message)
    {
        TrainingCancelled?.Invoke(message);
    }

    public static void OnMembershipPurchased(string message)
    {
        MembershipPurchased?.Invoke(message);
    }
}