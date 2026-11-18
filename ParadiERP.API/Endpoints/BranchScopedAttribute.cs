namespace ParadiERP.API.Endpoints;

/// <summary>
/// Marks a route any signed-in user may call as one whose reads are confined to the caller's
/// branch (BranchScope, docs/07 §2.3). Attached with <c>.WithMetadata(...)</c> so the endpoint
/// inventory test can assert which any-authenticated routes are scoped instead of trusting a
/// comment; <see cref="Reason"/> is the sentence that would otherwise have been the comment.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false)]
public sealed class BranchScopedAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
