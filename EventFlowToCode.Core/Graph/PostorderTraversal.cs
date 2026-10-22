namespace EventFlowToCode.Core.Graph;

internal static class PostorderTraversal
{
    public static List<T> Find<T, TKey>(T root, Func<T, IReadOnlyList<T>> edges, Func<T, TKey> key,
        Func<T, bool>? predicate = null) where TKey : notnull
    {
        List<T> result = [];
        HashSet<TKey> visited = [];
        Stack<(T Node, bool Finish)> pending = new([(root, false)]);
        while (pending.TryPop(out var item))
        {
            if (item.Finish)
            {
                result.Add(item.Node);
                continue;
            }

            if (!visited.Add(key(item.Node)) || predicate is not null && !predicate(item.Node))
                continue;
            
            pending.Push((item.Node, true));
            var children = edges(item.Node);
            for (var index = children.Count - 1; index >= 0; index--)
                pending.Push((children[index], false));
            
        }
        return result;
    }
}