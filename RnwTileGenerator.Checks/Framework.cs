using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RnwTileGenerator.Checks;

/// <summary>A check without parameters (same meaning as xUnit's [Fact]).</summary>
[AttributeUsage(AttributeTargets.Method)]
public class FactAttribute : Attribute;

/// <summary>A check run once per [InlineData] row (same meaning as xUnit's [Theory]).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TheoryAttribute : FactAttribute;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineDataAttribute(params object?[]? data) : Attribute
{
    /// <summary>[InlineData(null)] binds null to the params array itself; like xUnit it means one null argument.</summary>
    public object?[] Data { get; } = data ?? [null];
}

public sealed class CheckFailedException(string message) : Exception(message);

/// <summary>The subset of xUnit's Assert that the ported checks use, with the same argument order (expected first).</summary>
public static class Assert
{
    public static void True([DoesNotReturnIf(false)] bool condition, string? message = null)
    {
        if (!condition) Fail(message ?? "Expected true, got false.");
    }

    public static void False([DoesNotReturnIf(true)] bool condition, string? message = null)
    {
        if (condition) Fail(message ?? "Expected false, got true.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!AreEqual(expected, actual)) Fail($"Expected: {Show(expected)}\n  Actual:   {Show(actual)}");
    }

    /// <summary>Sequence overload, like xUnit: lets an array be compared with a List or IReadOnlyList.</summary>
    public static void Equal<T>(IEnumerable<T>? expected, IEnumerable<T>? actual)
    {
        if (!AreEqual(expected, actual)) Fail($"Expected: {Show(expected)}\n  Actual:   {Show(actual)}");
    }

    public static void NotEqual<T>(T expected, T actual)
    {
        if (AreEqual(expected, actual)) Fail($"Expected a value different from {Show(expected)}.");
    }

    public static void Null(object? value)
    {
        if (value is not null) Fail($"Expected null, got {Show(value)}.");
    }

    public static void NotNull([NotNull] object? value)
    {
        if (value is null) Fail("Expected a value, got null.");
    }

    public static void Empty(IEnumerable collection)
    {
        if (collection.Cast<object?>().Any()) Fail($"Expected an empty collection, got {Show(collection)}.");
    }

    public static void NotEmpty(IEnumerable collection)
    {
        if (!collection.Cast<object?>().Any()) Fail("Expected a non-empty collection.");
    }

    public static T Single<T>(IEnumerable<T> collection)
    {
        var list = collection.ToList();
        if (list.Count != 1) Fail($"Expected exactly one element, got {list.Count}: {Show(list)}.");
        return list[0];
    }

    public static T Single<T>(IEnumerable<T> collection, Func<T, bool> predicate) => Single(collection.Where(predicate));

    public static void Contains(string expectedSubstring, string? actual)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
            Fail($"Expected text to contain {Show(expectedSubstring)}.\n  Text: {Show(actual)}");
    }

    public static void DoesNotContain(string unexpectedSubstring, string? actual)
    {
        if (actual is not null && actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
            Fail($"Expected text not to contain {Show(unexpectedSubstring)}.\n  Text: {Show(actual)}");
    }

    public static void Contains<T>(T expected, IEnumerable<T> collection)
    {
        if (!collection.Any(item => AreEqual(expected, item))) Fail($"Expected collection to contain {Show(expected)}: {Show(collection)}.");
    }

    public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        if (!collection.Any(item => filter(item))) Fail($"No element matched the filter: {Show(collection)}.");
    }

    public static void DoesNotContain<T>(T unexpected, IEnumerable<T> collection)
    {
        if (collection.Any(item => AreEqual(unexpected, item))) Fail($"Expected collection not to contain {Show(unexpected)}.");
    }

    public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        if (collection.Any(item => filter(item))) Fail($"An element matched the filter: {Show(collection)}.");
    }

    public static void StartsWith(string expectedStart, string? actual)
    {
        if (actual is null || !actual.StartsWith(expectedStart, StringComparison.Ordinal))
            Fail($"Expected text to start with {Show(expectedStart)}.\n  Text: {Show(actual)}");
    }

    public static void EndsWith(string expectedEnd, string? actual)
    {
        if (actual is null || !actual.EndsWith(expectedEnd, StringComparison.Ordinal))
            Fail($"Expected text to end with {Show(expectedEnd)}.\n  Text: {Show(actual)}");
    }

    public static void Matches(string pattern, string? actual)
    {
        if (actual is null || !System.Text.RegularExpressions.Regex.IsMatch(actual, pattern))
            Fail($"Expected text to match /{pattern}/.\n  Text: {Show(actual)}");
    }

    public static void DoesNotMatch(string pattern, string? actual)
    {
        if (actual is not null && System.Text.RegularExpressions.Regex.IsMatch(actual, pattern))
            Fail($"Expected text not to match /{pattern}/.\n  Text: {Show(actual)}");
    }

    public static void All<T>(IEnumerable<T> collection, Action<T> check)
    {
        foreach (var item in collection) check(item);
    }

    public static void InRange<T>(T actual, T low, T high) where T : IComparable<T>
    {
        if (actual.CompareTo(low) < 0 || actual.CompareTo(high) > 0) Fail($"Expected {Show(actual)} in [{Show(low)}, {Show(high)}].");
    }

    /// <summary>Exactly this exception type.</summary>
    public static T Throws<T>(Action action) where T : Exception
    {
        var ex = Catch(action);
        if (ex is null || ex.GetType() != typeof(T)) Fail($"Expected {typeof(T).Name}, got {ex?.GetType().Name ?? "no exception"}.");
        return (T)ex;
    }

    /// <summary>This exception type or a subclass.</summary>
    public static T ThrowsAny<T>(Action action) where T : Exception
    {
        var ex = Catch(action);
        if (ex is not T typed) Fail($"Expected {typeof(T).Name} or subclass, got {ex?.GetType().Name ?? "no exception"}.");
        return (T)ex;
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        var ex = await CatchAsync(action);
        if (ex is null || ex.GetType() != typeof(T)) Fail($"Expected {typeof(T).Name}, got {ex?.GetType().Name ?? "no exception"}.");
        return (T)ex;
    }

    public static async Task<T> ThrowsAnyAsync<T>(Func<Task> action) where T : Exception
    {
        var ex = await CatchAsync(action);
        if (ex is not T) Fail($"Expected {typeof(T).Name} or subclass, got {ex?.GetType().Name ?? "no exception"}.");
        return (T)ex;
    }

    [DoesNotReturn]
    public static void Fail(string message) => throw new CheckFailedException(message);

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static bool AreEqual(object? expected, object? actual)
    {
        // Dictionaries compare like xUnit: same keys, equal values, order ignored.
        if (expected is IDictionary left0 && actual is IDictionary right0)
        {
            return left0.Count == right0.Count
                && left0.Keys.Cast<object>().All(key => right0.Contains(key) && AreEqual(left0[key], right0[key]));
        }

        if (expected is IEnumerable e && actual is IEnumerable a && expected is not string && actual is not string)
        {
            var left = e.Cast<object?>().ToList();
            var right = a.Cast<object?>().ToList();
            return left.Count == right.Count && left.Zip(right).All(pair => AreEqual(pair.First, pair.Second));
        }

        return Equals(expected, actual);
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Show)) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
