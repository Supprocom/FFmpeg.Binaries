namespace Supprocom.FFmpeg.Release;

internal sealed class ReleaseFailureException : Exception
{
    public ReleaseFailureException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public ReleaseFailureException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
