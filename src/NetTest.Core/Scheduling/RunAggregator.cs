using NetTest.Core.Enums;

namespace NetTest.Core.Scheduling;

/// <summary>Run 终态聚合（TechSpec 4.4）。</summary>
public static class RunAggregator
{
    public static (ExecutionStatus Status, CancellationReason Reason) Aggregate(
        IEnumerable<(ExecutionStatus Status, CancellationReason Reason)> executions)
    {
        (ExecutionStatus Status, CancellationReason Reason)[] list = executions.ToArray();
        if (list.Length == 0)
        {
            // 地址展开后没有任何适用 Execution：调度正常完成但没有该地址族的数据。
            return (ExecutionStatus.Completed, CancellationReason.None);
        }

        bool allCompleted = list.All(e => e.Status == ExecutionStatus.Completed);
        if (allCompleted)
        {
            return (ExecutionStatus.Completed, CancellationReason.None);
        }

        bool anyIncomplete = list.Any(e => e.Status == ExecutionStatus.Incomplete);
        if (anyIncomplete)
        {
            return (ExecutionStatus.Incomplete, UnifiedReason(list));
        }

        bool anyCompleted = list.Any(e => e.Status == ExecutionStatus.Completed);
        bool anyCancelled = list.Any(e => e.Status == ExecutionStatus.Cancelled);
        if (anyCompleted && anyCancelled)
        {
            return (ExecutionStatus.Incomplete, UnifiedReason(list));
        }

        bool allCancelled = list.All(e => e.Status == ExecutionStatus.Cancelled);
        if (allCancelled)
        {
            return (ExecutionStatus.Cancelled, UnifiedReason(list));
        }

        // 仍有未终态项（Pending/Running）：调用方不应在此时聚合。
        throw new InvalidOperationException("Run 尚未全部进入终态，不能聚合。");
    }

    private static CancellationReason UnifiedReason(IEnumerable<(ExecutionStatus Status, CancellationReason Reason)> executions)
    {
        foreach ((_, CancellationReason reason) in executions)
        {
            if (reason != CancellationReason.None)
            {
                return reason;
            }
        }

        return CancellationReason.None;
    }
}
