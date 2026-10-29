using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Hibira.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    sealed class TestAttribute : Attribute { }

    sealed class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }

    static class Assert
    {
        public static void True(bool condition, string message = "expected true")
        {
            if (!condition) throw new AssertException(message);
        }

        public static void Equal<T>(T expected, T actual, string what = "value")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertException($"{what}: expected <{expected}>, got <{actual}>");
        }

        public static void Throws<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new AssertException($"expected {typeof(TException).Name}");
        }
    }

    static class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "e2e") return await EndToEnd.Run();
            var filter = args.Length > 0 ? args[0] : null;
            var tests = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Where(m => filter == null || $"{m.DeclaringType.Name}.{m.Name}".Contains(filter))
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.MetadataToken)
                .ToList();
            var failed = 0;
            foreach (var test in tests)
            {
                var name = $"{test.DeclaringType.Name}.{test.Name}";
                try
                {
                    var result = test.Invoke(null, null);
                    if (result is Task task) await task;
                    Console.WriteLine($"ok   {name}");
                }
                catch (Exception e)
                {
                    failed++;
                    var inner = e is TargetInvocationException tie ? tie.InnerException : e;
                    Console.WriteLine($"FAIL {name}: {inner}");
                }
            }
            Console.WriteLine($"{tests.Count - failed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
