namespace ParadiERP.Infrastructure.Manufacturing;

/// <summary>
/// What a manufacturing write or read answered: the value, or a refusal with an HTTP status, a stable
/// code the screens translate (<c>mfg.error.&lt;code&gt;</c>) and an English sentence for anyone else.
/// </summary>
public sealed record MfgResult<T>(T? Value, int Status, string? Code, string? Error)
{
    public bool Ok => Code is null;

    public static MfgResult<T> Done(T value) => new(value, 200, null, null);

    public static MfgResult<T> Fail(int status, string code, string error) => new(default, status, code, error);

    /// <summary>The same refusal, carried as another result type.</summary>
    public MfgResult<TOther> As<TOther>() => new(default, Status, Code, Error);
}

/// <summary>The refusals every manufacturing service shares.</summary>
public static class MfgFail
{
    public static MfgResult<T> NotFound<T>(string what) => MfgResult<T>.Fail(404, "not_found", $"{what} not found.");

    public static MfgResult<T> Forbidden<T>(string why, string code = "forbidden") => MfgResult<T>.Fail(403, code, why);

    public static MfgResult<T> Invalid<T>(string code, string why) => MfgResult<T>.Fail(400, code, why);

    public static MfgResult<T> Conflict<T>(string code, string why) => MfgResult<T>.Fail(409, code, why);
}
