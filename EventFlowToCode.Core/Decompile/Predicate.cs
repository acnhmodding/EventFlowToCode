using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Decompile;

public abstract record Predicate
{
    public virtual IEnumerable<(QuerySignature Query, IReadOnlyDictionary<string, object?> Parameters)> GetQueries() => [];

    public virtual Predicate Invert() => new NotPredicate(this);

    public virtual Predicate And(Predicate other) =>
        other is AndPredicate and ? new AndPredicate([this]).And(and) : new AndPredicate([this, other]);

    public virtual Predicate Or(Predicate other) =>
        other is OrPredicate or ? new OrPredicate([this]).Or(or) : new OrPredicate([this, other]);
}

public sealed record ConstPredicate(bool Value) : Predicate;

public sealed record QueryPredicate(QuerySignature Query, IReadOnlyDictionary<string, object?> Parameters, IReadOnlyList<object?> Values, bool Negated = false) : Predicate
{
    public static QueryPredicate Create(QuerySignature query, IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<object?> values)
    {
        var negated = false;

        if (query.ReturnType == FlowType.Bool && values.Count == 1 && IsFalse(values[0]))
            negated = true;

        if (query.Inverted) negated = !negated;
 
        return new QueryPredicate(query, parameters, values, negated);
    }

    public override IEnumerable<(QuerySignature Query, IReadOnlyDictionary<string, object?> Parameters)> GetQueries()
    {
        yield return (Query, Parameters);
    }

    public override Predicate Invert() => this with { Negated = !Negated };

    private static bool IsFalse(object? value) =>
        value switch
        {
            false => true,
            IConvertible convertible when IsNumeric(value) =>
                convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture) == 0,
            _ => false,
        };

    private static bool IsNumeric(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}

public sealed record NotPredicate(Predicate Inner) : Predicate
{
    public override IEnumerable<(QuerySignature Query, IReadOnlyDictionary<string, object?> Parameters)> GetQueries() =>
        Inner.GetQueries();

    public override Predicate Invert() => Inner;
}

public sealed record AndPredicate(IReadOnlyList<Predicate> Inners) : Predicate
{
    public override IEnumerable<(QuerySignature Query, IReadOnlyDictionary<string, object?> Parameters)> GetQueries() =>
        Inners.SelectMany(inner => inner.GetQueries());

    public override Predicate Invert()
    {
        var negatedCount = Inners.Count(PredicateHelpers.IsNegated);
        return negatedCount * 2 >= Inners.Count
            ? new OrPredicate([.. Inners.Select(PredicateHelpers.InvertForDeMorgan)])
            : base.Invert();
    }

    public override Predicate And(Predicate other) =>
        other is AndPredicate andPredicate ? new AndPredicate([.. Inners, .. andPredicate.Inners]) : new AndPredicate([.. Inners, other]);
}

public sealed record OrPredicate(IReadOnlyList<Predicate> Inners) : Predicate
{
    public override IEnumerable<(QuerySignature Query, IReadOnlyDictionary<string, object?> Parameters)> GetQueries() =>
        Inners.SelectMany(inner => inner.GetQueries());

    public override Predicate Invert()
    {
        var negatedCount = Inners.Count(PredicateHelpers.IsNegated);
        return negatedCount * 2 >= Inners.Count
            ? new AndPredicate([.. Inners.Select(PredicateHelpers.InvertForDeMorgan)])
            : base.Invert();
    }

    public override Predicate Or(Predicate other) =>
        other is OrPredicate orPredicate ? new OrPredicate([.. Inners, .. orPredicate.Inners]) : new OrPredicate([.. Inners, other]);
}

internal static class PredicateHelpers
{
    public static bool IsNegated(Predicate predicate) => predicate is NotPredicate or QueryPredicate { Negated: true };
    public static Predicate InvertForDeMorgan(Predicate predicate) => predicate.Invert();
}