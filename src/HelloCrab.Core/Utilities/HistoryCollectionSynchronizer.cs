using System.Collections.ObjectModel;
using HelloCrab.Core.Models;

namespace HelloCrab.Core.Utilities;

/// <summary>
/// 按稳定的历史 ID 增量同步可见列表。
/// 未变化的项目不会被移除再添加，从而保留 ListBox 容器与自然滚动状态。
/// </summary>
public static class HistoryCollectionSynchronizer
{
    public static void Sync(
        ObservableCollection<DownloadHistoryItem> target,
        IReadOnlyList<DownloadHistoryItem> desired)
    {
        if (target.Count == desired.Count)
        {
            var unchanged = true;
            for (var index = 0; index < desired.Count; index++)
            {
                if (target[index].Id == desired[index].Id
                    && ReferenceEquals(target[index], desired[index]))
                {
                    continue;
                }

                unchanged = false;
                break;
            }

            if (unchanged)
                return;
        }

        var desiredIds = desired.Select(item => item.Id).ToHashSet();

        // 先移除已经不属于当前筛选结果的项目。
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(target[index].Id))
                target.RemoveAt(index);
        }

        // 再按目标顺序只执行必要的 Insert / Move。
        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var desiredItem = desired[targetIndex];
            var currentIndex = IndexOfId(target, desiredItem.Id);
            if (currentIndex < 0)
            {
                target.Insert(Math.Min(targetIndex, target.Count), desiredItem);
                continue;
            }

            // 正常情况下主集合会保留同一个对象引用；这里只为异常/初始化场景兜底。
            if (!ReferenceEquals(target[currentIndex], desiredItem))
                target[currentIndex] = desiredItem;

            if (currentIndex != targetIndex)
                target.Move(currentIndex, targetIndex);
        }
    }

    private static int IndexOfId(
        IReadOnlyList<DownloadHistoryItem> items,
        int id)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Id == id)
                return index;
        }

        return -1;
    }
}
