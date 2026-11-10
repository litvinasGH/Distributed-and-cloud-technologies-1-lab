using System.Collections.Concurrent;
using System.Diagnostics;

namespace Server.PubSub;

/// <summary>Факт, произошедший в предметной области фитнес-центра.</summary>
public sealed class DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public string EventType { get; init; } = string.Empty;
    public long EntityId { get; init; }
    public long ClientId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Data { get; init; } = new();
}

public interface IEventSubscriber
{
    string Name { get; }
    IReadOnlyCollection<string> Subscriptions { get; }
    bool IsAvailable { get; }
    int HandledCount { get; }
    Task HandleAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default);
}

/// <summary>Общая реализация подписчика с задержкой и возможностью имитации отказа.</summary>
public abstract class EventSubscriberBase : IEventSubscriber
{
    private int _available = 1;
    private int _handledCount;

    protected EventSubscriberBase(string name, IEnumerable<string> subscriptions, TimeSpan processingDelay)
    {
        Name = name;
        Subscriptions = subscriptions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ProcessingDelay = processingDelay;
    }

    public string Name { get; }
    public IReadOnlyCollection<string> Subscriptions { get; }
    public bool IsAvailable => Volatile.Read(ref _available) == 1;
    public int HandledCount => Volatile.Read(ref _handledCount);
    protected TimeSpan ProcessingDelay { get; }

    public void SetAvailability(bool available)
        => Interlocked.Exchange(ref _available, available ? 1 : 0);

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException($"Subscriber {Name} недоступен.");

        var stopwatch = Stopwatch.StartNew();
        string time = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"[{time}] {Name} → START {domainEvent.EventType}, EventId={domainEvent.EventId}, ClientId={domainEvent.ClientId}");

        if (ProcessingDelay > TimeSpan.Zero)
            await Task.Delay(ProcessingDelay, cancellationToken);

        // Компонент мог завершиться уже во время обработки события.
        if (!IsAvailable)
            throw new InvalidOperationException($"Subscriber {Name} стал недоступен во время обработки.");

        Interlocked.Increment(ref _handledCount);
        stopwatch.Stop();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → SUCCESS {domainEvent.EventType}, EntityId={domainEvent.EntityId}, " +
                          $"ClientId={domainEvent.ClientId}, elapsed={stopwatch.Elapsed.TotalSeconds:F3}s");
    }
}

public sealed class LoggerSubscriber : EventSubscriberBase
{
    public LoggerSubscriber() : base(
        "Logger",
        new[] { "TrainingBooked", "TrainingCancelled", "MembershipPurchased" },
        TimeSpan.FromSeconds(1)) { }
}

public sealed class ScheduleServiceSubscriber : EventSubscriberBase
{
    public ScheduleServiceSubscriber() : base(
        "ScheduleService",
        new[] { "TrainingBooked", "TrainingCancelled" },
        TimeSpan.FromSeconds(3)) { }
}

public sealed class NotificationServiceSubscriber : EventSubscriberBase
{
    public NotificationServiceSubscriber() : base(
        "NotificationService",
        new[] { "TrainingBooked", "TrainingCancelled", "MembershipPurchased" },
        TimeSpan.FromSeconds(2)) { }
}

public sealed class AnalyticsServiceSubscriber : EventSubscriberBase
{
    public AnalyticsServiceSubscriber() : base(
        "AnalyticsService",
        new[] { "TrainingBooked", "TrainingCancelled", "MembershipPurchased" },
        TimeSpan.FromMilliseconds(500)) { }
}

internal sealed record PendingDelivery(DomainEvent Event, IEventSubscriber Subscriber);

/// <summary>Брокер хранит подписки, маршрутизирует события и временно сохраняет недоставленные сообщения.</summary>
public sealed class EventBroker
{
    private const int MaxAttempts = 3;
    private readonly ConcurrentDictionary<string, IEventSubscriber> _subscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<PendingDelivery> _pending = new();
    private int _available = 1;
    private int _publishedCount;
    private int _deliveredCount;
    private int _failedCount;

    public EventBroker(string name) => Name = name;

    public string Name { get; }
    public bool IsAvailable => Volatile.Read(ref _available) == 1;
    public int PendingCount => _pending.Count;
    public int PublishedCount => Volatile.Read(ref _publishedCount);
    public int DeliveredCount => Volatile.Read(ref _deliveredCount);
    public int FailedCount => Volatile.Read(ref _failedCount);

    public void SetAvailability(bool available)
    {
        Interlocked.Exchange(ref _available, available ? 1 : 0);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → {(available ? "AVAILABLE" : "UNAVAILABLE")}");
    }

    public void RegisterSubscriber(IEventSubscriber subscriber)
    {
        _subscribers[subscriber.Name] = subscriber;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → SUBSCRIBE {subscriber.Name}: {string.Join(", ", subscriber.Subscriptions)}");
    }

    public async Task PublishAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException($"{Name} недоступен.");

        Interlocked.Increment(ref _publishedCount);
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → EVENT RECEIVED {domainEvent.EventType}, " +
                          $"EntityId={domainEvent.EntityId}, ClientId={domainEvent.ClientId}, EventId={domainEvent.EventId}");

        IEventSubscriber[] recipients = _subscribers.Values
            .Where(s => s.Subscriptions.Contains(domainEvent.EventType, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();

        if (recipients.Length == 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → NO SUBSCRIBERS for {domainEvent.EventType}");
            return;
        }

        // Все получатели обрабатывают одно событие параллельно и независимо друг от друга.
        await Task.WhenAll(recipients.Select(subscriber =>
            DeliverWithRetryAsync(new PendingDelivery(domainEvent, subscriber), enqueueOnFailure: true, cancellationToken)));
        stopwatch.Stop();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → DELIVERY COMPLETE {domainEvent.EventType}; " +
                          $"subscribers={recipients.Length}; elapsed={stopwatch.Elapsed.TotalSeconds:F3}s");
    }

    public async Task<int> RetryPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → RETRY SKIPPED: broker unavailable");
            return 0;
        }

        int itemsToProcess = _pending.Count;
        if (itemsToProcess == 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → очередь пуста.");
            return 0;
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → RETRY QUEUE, items={itemsToProcess}");
        var retryTasks = new List<Task>();
        for (int i = 0; i < itemsToProcess; i++)
        {
            if (_pending.TryDequeue(out PendingDelivery delivery))
                retryTasks.Add(DeliverWithRetryAsync(delivery, enqueueOnFailure: true, cancellationToken));
        }

        await Task.WhenAll(retryTasks);
        return itemsToProcess;
    }

    private async Task DeliverWithRetryAsync(
        PendingDelivery delivery,
        bool enqueueOnFailure,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (!IsAvailable)
                    throw new InvalidOperationException($"{Name} стал недоступен.");

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → {delivery.Subscriber.Name}, attempt {attempt}/{MaxAttempts}");
                await delivery.Subscriber.HandleAsync(delivery.Event, cancellationToken);
                Interlocked.Increment(ref _deliveredCount);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → {delivery.Subscriber.Name} → FAIL: {ex.Message}");
                if (attempt < MaxAttempts)
                    await Task.Delay(200, cancellationToken);
            }
        }

        Interlocked.Increment(ref _failedCount);
        if (enqueueOnFailure)
        {
            _pending.Enqueue(delivery);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Name} → QUEUED {delivery.Event.EventType} for {delivery.Subscriber.Name}; pending={PendingCount}");
        }
    }
}

/// <summary>Два брокера: основной и резервный. При отказе первого используется второй.</summary>
public sealed class PubSubCluster
{
    private readonly ConcurrentDictionary<string, EventSubscriberBase> _subscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly EventBroker[] _brokers;
    private string _lastUsedBroker = "не использовался";

    public PubSubCluster()
    {
        _brokers = new[] { new EventBroker("Broker-1"), new EventBroker("Broker-2") };
    }

    public IReadOnlyCollection<EventBroker> Brokers => _brokers;
    public string LastUsedBroker => Volatile.Read(ref _lastUsedBroker);

    public bool RegisterSubscriber(EventSubscriberBase subscriber)
    {
        if (!_subscribers.TryAdd(subscriber.Name, subscriber))
            return false;

        foreach (EventBroker broker in _brokers)
            broker.RegisterSubscriber(subscriber);

        return true;
    }

    public bool SetSubscriberAvailability(string name, bool available)
    {
        if (!_subscribers.TryGetValue(name, out EventSubscriberBase? subscriber))
            return false;

        subscriber.SetAvailability(available);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {subscriber.Name} → {(available ? "AVAILABLE" : "UNAVAILABLE")}");
        return true;
    }

    public bool SetBrokerAvailability(string name, bool available)
    {
        EventBroker? broker = _brokers.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (broker is null) return false;
        broker.SetAvailability(available);
        return true;
    }

    public async Task<bool> PublishAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        foreach (EventBroker broker in _brokers)
        {
            if (!broker.IsAvailable) continue;
            try
            {
                await broker.PublishAsync(domainEvent, cancellationToken);
                Volatile.Write(ref _lastUsedBroker, broker.Name);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ROUTER → {broker.Name} error: {ex.Message}");
            }
        }

        return false;
    }

    public async Task RetryPendingAsync(CancellationToken cancellationToken = default)
    {
        foreach (EventBroker broker in _brokers.Where(b => b.IsAvailable))
            await broker.RetryPendingAsync(cancellationToken);
    }

    public string GetStatus()
    {
        var lines = new List<string>
        {
            $"Last used: {LastUsedBroker}",
            "Subscribers:"
        };

        foreach (EventSubscriberBase subscriber in _subscribers.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            lines.Add($"- {subscriber.Name}: {(subscriber.IsAvailable ? "AVAILABLE" : "UNAVAILABLE")}; " +
                      $"handled={subscriber.HandledCount}; events=[{string.Join(", ", subscriber.Subscriptions)}]");
        }

        lines.Add("Brokers:");
        foreach (EventBroker broker in _brokers)
        {
            lines.Add($"- {broker.Name}: {(broker.IsAvailable ? "AVAILABLE" : "UNAVAILABLE")}; " +
                      $"published={broker.PublishedCount}; delivered={broker.DeliveredCount}; failed={broker.FailedCount}; pending={broker.PendingCount}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Publisher знает только кластер Broker и не обращается к Subscriber.</summary>
public sealed class EventPublisher
{
    private readonly PubSubCluster _cluster;
    private int _publishedCount;
    private int _errorCount;

    public EventPublisher(PubSubCluster cluster) => _cluster = cluster;

    public int PublishedCount => Volatile.Read(ref _publishedCount);
    public int ErrorCount => Volatile.Read(ref _errorCount);

    public async Task<bool> PublishAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Publisher → PUBLISH {domainEvent.EventType}, " +
                          $"EntityId={domainEvent.EntityId}, ClientId={domainEvent.ClientId}, EventId={domainEvent.EventId}, " +
                          $"Created={domainEvent.Timestamp:O}");
        bool success;
        try
        {
            success = await _cluster.PublishAsync(domainEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Publisher → ERROR: {ex.Message}");
            success = false;
        }

        stopwatch.Stop();
        if (success)
        {
            Interlocked.Increment(ref _publishedCount);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Publisher → SUCCESS via {_cluster.LastUsedBroker}; " +
                              $"elapsed={stopwatch.Elapsed.TotalSeconds:F3}s");
        }
        else
        {
            Interlocked.Increment(ref _errorCount);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Publisher → FAIL: все Broker недоступны; " +
                              $"elapsed={stopwatch.Elapsed.TotalSeconds:F3}s");
        }

        return success;
    }
}

/// <summary>Единая конфигурация Pub/Sub для приложения фитнес-центра.</summary>
public static class FitnessPubSub
{
    public static PubSubCluster Cluster { get; } = CreateCluster();
    public static EventPublisher Publisher { get; } = new(Cluster);

    private static PubSubCluster CreateCluster()
    {
        var cluster = new PubSubCluster();
        cluster.RegisterSubscriber(new LoggerSubscriber());
        cluster.RegisterSubscriber(new ScheduleServiceSubscriber());
        cluster.RegisterSubscriber(new NotificationServiceSubscriber());
        return cluster;
    }

    public static void Initialize()
    {
        Console.WriteLine("Pub/Sub инициализирован: Publisher → Broker-1 / Broker-2 → Subscribers.");
        Console.WriteLine("События: TrainingBooked, TrainingCancelled, MembershipPurchased.");
    }
}
