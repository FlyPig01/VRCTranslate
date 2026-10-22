namespace VrcTranslate.Core.Speech;

/// <summary>Tencent Cloud ASR error with a stable code for user-facing mapping.</summary>
public sealed class TencentAsrException : Exception
{
    public TencentAsrException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public bool IsAuthenticationFailure =>
        Code.StartsWith("AuthFailure", StringComparison.OrdinalIgnoreCase) ||
        Code is "HTTP401" or "HTTP403" or "NotConfigured" or "InvalidEngine";
}
