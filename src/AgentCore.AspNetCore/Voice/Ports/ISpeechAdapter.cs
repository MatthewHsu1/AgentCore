using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>
    /// Names the vendor behind one speech role's <c>kind</c>, <c>stt</c> or <c>tts</c>.
    /// </summary>
    public interface ISpeechAdapter : IVendorAdapter
    {
    }
}
