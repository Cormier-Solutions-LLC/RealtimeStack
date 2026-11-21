using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace RealtimeStack.Accounts;

internal enum AccessAuthenticationStage { Login, TokenValidated, AuthenticationFailed, RemoteFailure, TicketReceived, SignedIn, CookieValidation, Session }
internal enum AccessAuthenticationReason { Accepted, Challenged, IdentityRejected, SavedStampRejected, AuthorityRejected, AuthorityUnavailable, Anonymous, ProtocolRejected, TokenRejected }

internal static partial class AccountsAuthenticationDiagnostics
{
    // Fixed stages and booleans only. Never log claim values, properties, headers,
    // authorization codes, exception messages, cookie contents or ticket handles.
    internal static void Record(HttpContext context, AccessAuthenticationStage stage,
        AccessAuthenticationReason reason, string authority, ClaimsPrincipal? principal,
        AuthenticationProperties? properties = null)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RealtimeStack.Accounts.Authentication");
        if (!logger.IsEnabled(LogLevel.Information)) return;
        var stamp = principal is null ? null : AccountsAccess.CredentialStamp(principal);
        var savedMatches = stamp is not null && properties is not null &&
            properties.Items.TryGetValue(AccountsAccess.CredentialStampKey, out var saved) && stamp == saved;
        Write(logger,
            stage, reason, principal?.Identity?.IsAuthenticated == true,
            principal is not null && principal.FindAll("iss").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).SequenceEqual([authority]),
            principal?.FindAll("email_verified").Any(claim => claim.Value == "true" && claim.ValueType == ClaimValueTypes.Boolean) == true,
            stamp is not null, savedMatches, context.Request.Cookies.ContainsKey("__Host-RealtimeStack.Accounts"));
    }

    [LoggerMessage(7101, LogLevel.Information, "Access authentication stage={Stage} reason={Reason} authenticated={Authenticated} exactIssuer={ExactIssuer} verifiedBoolean={VerifiedBoolean} validAcr={ValidAcr} savedStampEqual={SavedStampEqual} cookiePresent={CookiePresent}")]
    private static partial void Write(ILogger logger, AccessAuthenticationStage stage, AccessAuthenticationReason reason,
        bool authenticated, bool exactIssuer, bool verifiedBoolean, bool validAcr, bool savedStampEqual, bool cookiePresent);

    [LoggerMessage(7102, LogLevel.Information, "Access ticket stage=store succeeded=true")]
    internal static partial void Stored(ILogger logger);
    [LoggerMessage(7103, LogLevel.Information, "Access ticket stage=renew succeeded={Succeeded}")]
    internal static partial void Renewed(ILogger logger, bool succeeded);
    [LoggerMessage(7104, LogLevel.Information, "Access ticket stage=retrieve found={Found} decrypted={Decrypted} parsed={Parsed}")]
    internal static partial void Retrieved(ILogger logger, bool found, bool decrypted, bool parsed);
    [LoggerMessage(7105, LogLevel.Information, "Access ticket stage=remove removed={Removed}")]
    internal static partial void Removed(ILogger logger, bool removed);
}
