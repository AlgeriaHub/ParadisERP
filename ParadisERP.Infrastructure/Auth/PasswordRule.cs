namespace ParadisERP.Infrastructure.Auth;

/// <summary>
/// What a sign-in password has to be, wherever one is typed: by the administrator who creates an
/// account or resets it, and by the person changing their own (owner, 2026-10-05: "keep the password
/// created from admin side, and the user after login can change password").
///
/// <para>
/// One rule, not two copies of it: the users routes used to spell the six out inline, and a person
/// choosing their own password is held to exactly what the administrator was — a stricter minimum on
/// one side would only teach people to ask the administrator instead. Not blank, and at least
/// <see cref="MinLength"/> characters; nothing about which characters, which the owner has not asked for.
/// </para>
/// </summary>
public static class PasswordRule
{
    public const int MinLength = 6;

    /// <summary>The sentence a password below the minimum is refused with, on every route.</summary>
    public const string TooShortMessage = "Password must be at least 6 characters.";

    public static bool IsAcceptable(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length >= MinLength;
}
