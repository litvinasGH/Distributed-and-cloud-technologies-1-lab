using System.Text;

namespace Server.Consensus;

public enum NodeStatus
{
    Available,
    Failed
}

public enum NodeRole
{
    Leader,
    Follower
}

/// <summary>Учебная модель участника распределённой системы.</summary>
public sealed class DistributedNode
{
    internal DistributedNode(int id)
    {
        Id = id;
        Status = NodeStatus.Available;
        Role = NodeRole.Follower;
        VoteYes = true;
    }

    public int Id { get; }
    public NodeStatus Status { get; internal set; }
    public NodeRole Role { get; internal set; }
    /// <summary>Настройка голоса для демонстрации YES/NO; по умолчанию узел голосует YES.</summary>
    public bool VoteYes { get; internal set; }
    public bool IsAvailable => Status == NodeStatus.Available;
}

public sealed record ConsensusResult(
    bool Achieved,
    string ProposedValue,
    int YesVotes,
    int NoVotes,
    int AvailableNodes,
    int Majority,
    string Message);

/// <summary>
/// Учебная модель выбора лидера и консенсуса на пяти логических узлах.
/// Узлы являются объектами внутри процесса Server; сетевой транспорт между ними не моделируется.
/// </summary>
public sealed class ConsensusCluster
{
    private readonly object _sync = new();
    private readonly DistributedNode[] _nodes;
    private int? _leaderId;
    private string? _lastAgreedValue;

    public ConsensusCluster(int nodeCount = 5)
    {
        if (nodeCount < 1) throw new ArgumentOutOfRangeException(nameof(nodeCount));
        _nodes = Enumerable.Range(1, nodeCount).Select(id => new DistributedNode(id)).ToArray();
    }

    public IReadOnlyList<DistributedNode> Nodes => _nodes;
    public int Majority => _nodes.Length / 2 + 1;
    public int? LeaderId { get { lock (_sync) return _leaderId; } }
    public string? LastAgreedValue { get { lock (_sync) return _lastAgreedValue; } }

    public string ElectLeader()
    {
        lock (_sync)
        {
            Log("Election started");
            foreach (var node in _nodes)
            {
                Log($"Node {node.Id} {(node.IsAvailable ? "AVAILABLE" : "FAILED")}");
                node.Role = NodeRole.Follower;
            }

            var available = _nodes.Where(n => n.IsAvailable).ToArray();
            if (available.Length == 0)
            {
                _leaderId = null;
                Log("Election failed: no available nodes");
                return "Невозможно выбрать лидера: нет доступных узлов.";
            }

            DistributedNode leader = available.MaxBy(n => n.Id)!;
            leader.Role = NodeRole.Leader;
            _leaderId = leader.Id;
            Log($"Node {leader.Id} elected as LEADER");
            return $"Новый лидер: Node {leader.Id}. Доступно узлов: {available.Length}/{_nodes.Length}; большинство: {Majority}.";
        }
    }

    public string SetNodeAvailability(int nodeId, bool available)
    {
        lock (_sync)
        {
            DistributedNode? node = FindNode(nodeId);
            if (node is null) return $"Узел Node {nodeId} не существует. Допустимые ID: 1–{_nodes.Length}.";

            node.Status = available ? NodeStatus.Available : NodeStatus.Failed;
            if (!available && _leaderId == nodeId)
            {
                _leaderId = null;
                node.Role = NodeRole.Follower;
            }
            Log($"Node {nodeId} → {(available ? "AVAILABLE" : "FAILED")}");
        }

        string heartbeat = CheckHeartbeats();
        if (!available || LeaderId is null)
        {
            string election = ElectLeader();
            return $"{heartbeat}{Environment.NewLine}{election}";
        }
        return heartbeat;
    }

    public string CheckHeartbeats()
    {
        lock (_sync)
        {
            var lines = new List<string> { "Проверка heartbeat между доступными узлами:" };
            var observers = _nodes.Where(n => n.IsAvailable).ToArray();
            foreach (var observer in observers)
            {
                foreach (var target in _nodes.Where(n => n.Id != observer.Id))
                {
                    Log($"Node {observer.Id} → HEARTBEAT? → Node {target.Id}");
                    if (target.IsAvailable)
                    {
                        Log($"Node {target.Id} → ALIVE (ответ для Node {observer.Id})");
                        lines.Add($"Node {observer.Id} → Node {target.Id}: ALIVE");
                    }
                    else
                    {
                        Log($"Node {observer.Id} → Node {target.Id}: HEARTBEAT TIMEOUT / NO RESPONSE");
                        lines.Add($"Node {observer.Id} → Node {target.Id}: NO RESPONSE");
                    }
                }
            }

            if (observers.Length == 0)
                lines.Add("Нет доступных узлов, heartbeat отправить некому.");

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string SetNodeVote(int nodeId, bool voteYes)
    {
        lock (_sync)
        {
            DistributedNode? node = FindNode(nodeId);
            if (node is null) return $"Узел Node {nodeId} не существует.";
            node.VoteYes = voteYes;
            Log($"Node {nodeId} vote preference → {(voteYes ? "YES" : "NO")}");
            return $"Для Node {nodeId} установлен голос {(voteYes ? "YES" : "NO")}.";
        }
    }

    public ConsensusResult Propose(string proposedValue)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(proposedValue))
                return new(false, "", 0, 0, 0, Majority, "Предлагаемое значение не может быть пустым.");

            DistributedNode? leader = _leaderId is int id ? FindNode(id) : null;
            if (leader is null || !leader.IsAvailable || leader.Role != NodeRole.Leader)
                return new(false, proposedValue, 0, 0, _nodes.Count(n => n.IsAvailable), Majority,
                    "Голосование невозможно: действующий лидер отсутствует. Сначала выполните выбор лидера.");

            var available = _nodes.Where(n => n.IsAvailable).ToArray();
            Log($"Leader Node {leader.Id} → PROPOSE {proposedValue}");
            if (available.Length < Majority)
            {
                Log($"CONSENSUS NOT ACHIEVED: available={available.Length}; majority={Majority}");
                return new(false, proposedValue, 0, 0, available.Length, Majority,
                    $"CONSENSUS NOT ACHIEVED: доступно {available.Length}/{_nodes.Length}, большинство {Majority}.");
            }

            int yes = 0;
            int no = 0;
            foreach (var node in available)
            {
                if (node.VoteYes) yes++; else no++;
                Log($"Node {node.Id} → {(node.VoteYes ? "YES" : "NO")}");
            }

            bool achieved = yes >= Majority;
            Log($"YES = {yes}; NO = {no}; Majority = {Majority}");
            if (achieved)
            {
                _lastAgreedValue = proposedValue;
                Log($"CONSENSUS ACHIEVED: {proposedValue}");
            }
            else
            {
                Log($"CONSENSUS NOT ACHIEVED: {proposedValue}");
            }

            return new ConsensusResult(achieved, proposedValue, yes, no, available.Length, Majority,
                achieved
                    ? $"CONSENSUS ACHIEVED: YES={yes}, NO={no}, Majority={Majority}. Принято значение: {proposedValue}."
                    : $"CONSENSUS NOT ACHIEVED: YES={yes}, NO={no}, Majority={Majority}. Значение не принято.");
        }
    }

    public string Reset()
    {
        lock (_sync)
        {
            foreach (var node in _nodes)
            {
                node.Status = NodeStatus.Available;
                node.Role = NodeRole.Follower;
                node.VoteYes = true;
            }
            _leaderId = null;
            _lastAgreedValue = null;
            Log("Cluster reset: all nodes AVAILABLE; all votes YES");
        }
        return "Все узлы восстановлены, голоса сброшены на YES." + Environment.NewLine + ElectLeader();
    }

    public string GetStatus()
    {
        lock (_sync)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Consensus cluster: nodes={_nodes.Length}; majority={Majority}; available={_nodes.Count(n => n.IsAvailable)}");
            foreach (var node in _nodes)
                sb.AppendLine($"Node {node.Id}: {node.Status.ToString().ToUpperInvariant()}; {node.Role.ToString().ToUpperInvariant()}; vote={(node.VoteYes ? "YES" : "NO")}");
            sb.AppendLine($"Leader: {(_leaderId is null ? "NONE" : $"Node {_leaderId}")}");
            sb.AppendLine($"Last agreed value: {_lastAgreedValue ?? "(нет)"}");
            return sb.ToString();
        }
    }

    private DistributedNode? FindNode(int id) => _nodes.FirstOrDefault(n => n.Id == id);

    private static void Log(string message)
        => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CONSENSUS] {message}");
}

public static class ConsensusService
{
    public static ConsensusCluster Cluster { get; } = new(5);

    public static void Initialize()
    {
        // Первоначальный выбор: при доступности всех пяти узлов лидером становится Node 5.
        Cluster.ElectLeader();
    }
}
