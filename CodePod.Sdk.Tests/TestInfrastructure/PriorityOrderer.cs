using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}

public sealed class PriorityOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        return testCases
            .OrderBy(tc => GetPriority(tc))
            .ThenBy(tc => tc.TestMethod.Method.Name, StringComparer.Ordinal);
    }

    private static int GetPriority(ITestCase testCase)
    {
        var priority = testCase.TestMethod.Method
            .GetCustomAttributes(typeof(TestPriorityAttribute).AssemblyQualifiedName!)
            .FirstOrDefault();

        if (priority == null)
        {
            return 1;
        }

        return priority.GetNamedArgument<int>(nameof(TestPriorityAttribute.Priority));
    }
}
