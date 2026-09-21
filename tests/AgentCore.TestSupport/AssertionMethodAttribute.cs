namespace AgentCore.TestSupport
{
    /// <summary>
    /// Marks a method that asserts on its own, so a test whose only check is a call to it is not
    /// reported as a test without assertions (SonarQube S2699 honours any attribute of this name).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class AssertionMethodAttribute : Attribute;
}
