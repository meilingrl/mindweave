using Mindweave.Application.Languages;

namespace Mindweave.Application.Conversations;

/// <summary>
/// Storage became unavailable after a task started and before one of its requests was sent: the request is not sent and
/// the task fails visibly (architecture review D2). Requests already in flight finish. <see cref="Message"/> reads
/// <see cref="Text"/> in the language current when it is read; what is written into the database is that string.
/// </summary>
internal sealed class StorageAdmissionRefusedException : Exception
{
    public StorageAdmissionRefusedException()
        : base(Detail.Chinese)
    {
        Text = Detail;
    }

    public StorageAdmissionRefusedException(string message)
        : base(message)
    {
        Text = new BilingualText(message, message);
    }

    public StorageAdmissionRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Text = new BilingualText(message, message);
    }

    public static BilingualText Detail { get; } = new(
        "Local data can’t be saved right now, so the request wasn’t sent.",
        "本地数据暂时无法保存，请求未发送。");

    /// <summary>Why, in both languages.</summary>
    public BilingualText Text { get; }

    public override string Message => Text.ToString();
}
