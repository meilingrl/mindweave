namespace Mindweave.Application.Conversations;

/// <summary>
/// Storage became unavailable after a task started and before one of its requests was sent: the request is not sent and
/// the task fails visibly (architecture review D2). Requests already in flight finish.
/// </summary>
internal sealed class StorageAdmissionRefusedException : Exception
{
    public const string Detail = "本地数据暂时无法保存，请求未发送。";

    public StorageAdmissionRefusedException()
        : base(Detail)
    {
    }

    public StorageAdmissionRefusedException(string message)
        : base(message)
    {
    }

    public StorageAdmissionRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
