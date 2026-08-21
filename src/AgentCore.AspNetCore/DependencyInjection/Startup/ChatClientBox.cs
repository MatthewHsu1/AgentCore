using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// Holds the chat client factory for the one tool that is built before it exists.
/// </summary>
/// <remarks>
/// Step 4 builds the tools and step 8 compiles the agents, and the factory comes out of step 8.
/// Every tool has to exist by step 4, because agents compile against them. <c>ui.draw</c> is the
/// only tool that needs a model of its own, and it needs it when it is called rather than when it
/// is built, so it reads this box instead of holding the factory. Startup is a single flow of
/// execution and the box is filled before any call can run, so the field needs no lock.
/// </remarks>
internal sealed class ChatClientBox
{
    /// <summary>The factory, or <see langword="null"/> before the compile step fills it.</summary>
    internal IChatClientFactory? Value { get; set; }
}
