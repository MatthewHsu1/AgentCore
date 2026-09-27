namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>The entry this request runs, once it is known.</summary>
    public interface IEntryFeature
    {
        /// <summary>Gets the entry key.</summary>
        string Entry { get; }
    }
}
