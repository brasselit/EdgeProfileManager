using System.Reflection;

namespace EdgeProfileManager.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute : Attribute;

internal sealed class AssertionException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message = "Bedingung nicht erfüllt")
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string? context = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{context}{(context is null ? "" : ": ")}erwartet <{expected}>, erhalten <{actual}>");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a)) throw new AssertionException($"erwartet [{string.Join(", ", e)}], erhalten [{string.Join(", ", a)}]");
    }

    public static T IsType<T>(object? value)
    {
        if (value is T typed) return typed;
        throw new AssertionException($"erwartet Typ {typeof(T).Name}, erhalten {value?.GetType().Name ?? "null"}");
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new AssertionException($"{typeof(TException).Name} erwartet");
    }
}

/// <summary>Minimaler Testrunner: führt alle [Test]-Methoden aus; Filter optional als Argument.</summary>
internal static class TestRunner
{
    private static int Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(TestRunner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
                .Select(m => (Type: t, Method: m)))
            .Where(t => filter is null || $"{t.Type.Name}.{t.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Type.Name).ThenBy(t => t.Method.Name)
            .ToList();

        var failed = 0;
        foreach (var (type, method) in tests)
        {
            var name = $"{type.Name}.{method.Name}";
            var instance = method.IsStatic ? null : Activator.CreateInstance(type);
            try
            {
                method.Invoke(instance, null);
                Console.WriteLine($"  ok    {name}");
            }
            catch (TargetInvocationException ex)
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAIL  {name}: {ex.InnerException?.Message}");
                Console.ResetColor();
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} Tests erfolgreich.");
        return failed == 0 ? 0 : 1;
    }
}
