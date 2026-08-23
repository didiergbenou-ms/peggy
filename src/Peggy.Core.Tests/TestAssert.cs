namespace Peggy.Core.Tests;

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected: {expected}. Actual: {actual}.");
        }
    }

    public static void Contains(
        string expected,
        string actual,
        string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message} Missing: {expected}.");
        }
    }

    public static T Throws<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"{message} Expected {typeof(T).Name}.");
    }

    public static async Task<T> ThrowsAsync<T>(
        Func<Task> action,
        string message)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"{message} Expected {typeof(T).Name}.");
    }
}
