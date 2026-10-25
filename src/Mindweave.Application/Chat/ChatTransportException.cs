namespace Mindweave.Application.Chat;

public sealed class ChatTransportException(string errorCode) : Exception($"聊天服务请求未完成（{errorCode}）。")
{
    public string ErrorCode { get; } = errorCode;
}
