namespace AgentCore.Application.Conversation.Actions
{
    /// <summary>
    /// The base of every action a conversation can be asked for. Its constructor is closed to other assemblies: every
    /// channel must know what each action means, and an action a host made up would reach channels that cannot read it.
    /// </summary>
    public abstract record ConversationAction
    {
        private protected ConversationAction()
        {
        }
    }
}
