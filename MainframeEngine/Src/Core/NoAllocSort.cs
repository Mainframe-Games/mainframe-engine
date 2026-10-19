using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// A stable insertion sort that allocates nothing, for short per-frame lists that are usually already in order.
/// <see cref="List{T}.Sort(IComparer{T})"/> wraps the comparer's <c>Compare</c> in a new delegate on every call with two
/// or more items, which per-frame code cannot afford.
/// </summary>
internal static class NoAllocSort
{
    public static void Sort<T, TComparer>(List<T> list, TComparer comparer)
        where TComparer : IComparer<T>
    {
        var items = CollectionsMarshal.AsSpan(list);
        for (var i = 1; i < items.Length; i++)
        {
            var item = items[i];
            var j = i - 1;
            while (j >= 0 && comparer.Compare(items[j], item) > 0)
            {
                items[j + 1] = items[j];
                j--;
            }

            items[j + 1] = item;
        }
    }
}
