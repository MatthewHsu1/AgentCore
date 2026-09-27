using System.Data.Common;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// A database fault that says whether it is transient, the way a provider such as Npgsql does.
    /// </summary>
    internal sealed class FakeDbException(bool transient) : DbException(transient ? "the store blinked." : "the store refused.")
    {
        public override bool IsTransient => transient;
    }
}
