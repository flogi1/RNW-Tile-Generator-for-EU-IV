using System.Globalization;
using System.Reflection;

namespace RnwTileGenerator.Checks;

/// <summary>
/// Runs every [Fact]/[Theory] method in this assembly. Optional first argument: only checks whose name
/// ("Class.Method") contains that text. Each check gets a fresh instance of its class (disposed afterwards
/// when IDisposable), like xUnit. Exit code = number of failed checks, capped at 100.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;
        int passed = 0, failed = 0;

        var methods = typeof(Program).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .OrderBy(method => method.DeclaringType!.FullName, StringComparer.Ordinal)
            .ThenBy(method => method.Name, StringComparer.Ordinal);

        foreach (var method in methods)
        {
            var name = method.DeclaringType!.Name + "." + method.Name;
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            var rows = method.GetCustomAttribute<TheoryAttribute>() is not null
                ? method.GetCustomAttributes<InlineDataAttribute>().Select(row => row.Data).ToList()
                : [Array.Empty<object?>()];

            foreach (var row in rows)
            {
                var label = row.Length == 0 ? name : $"{name}({string.Join(", ", row.Select(v => v?.ToString() ?? "null"))})";
                var error = Run(method, row);
                if (error is null)
                {
                    passed++;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"FAIL {label}: {error}");
                }
            }
        }

        Console.WriteLine($"{passed + failed} checks, {failed} failed");
        return Math.Min(failed, 100);
    }

    private static string? Run(MethodInfo method, object?[] row)
    {
        object? instance = null;
        try
        {
            if (!method.IsStatic) instance = Activator.CreateInstance(method.DeclaringType!);
            var parameters = method.GetParameters();
            var values = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                values[i] = Convert(i < row.Length ? row[i] : null, parameters[i].ParameterType);

            var result = method.Invoke(instance, values);
            if (result is Task task) task.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            return inner is CheckFailedException ? inner.Message : inner.ToString();
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    private static object? Convert(object? value, Type target)
    {
        if (value is null) return null;
        var type = Nullable.GetUnderlyingType(target) ?? target;
        if (type.IsInstanceOfType(value)) return value;
        if (type.IsEnum) return Enum.ToObject(type, value);
        return System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }
}
