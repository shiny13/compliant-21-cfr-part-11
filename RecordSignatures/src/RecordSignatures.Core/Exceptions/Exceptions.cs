namespace RecordSignatures.Core.Exceptions;

/// <summary>Base type for expected, business-rule failures. The API maps each subtype to an HTTP status.</summary>
public abstract class SignatureException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class SignatureValidationException(string message) : SignatureException(message);

public sealed class NotFoundException(string message) : SignatureException(message);

/// <summary>The request conflicts with current state (duplicate enrollment, stale record version, concurrent writer...).</summary>
public sealed class ConflictException(string message, Exception? inner = null) : SignatureException(message, inner);

/// <summary>The caller is authenticated but not permitted to do this (role, self-administration, signer not authorized...).</summary>
public sealed class ForbiddenException(string message) : SignatureException(message);

/// <summary>The identification code / password presented for a signing did not verify. Deliberately generic.</summary>
public sealed class SignatureAuthenticationException(string message) : SignatureException(message);

/// <summary>Signing is temporarily blocked for this individual after repeated failed attempts (11.300(d)).</summary>
public sealed class SignerLockedException(DateTime lockedUntilUtc)
    : SignatureException($"Signing is locked until {lockedUntilUtc:yyyy-MM-dd HH:mm:ss} UTC after repeated failed attempts.")
{
    public DateTime LockedUntilUtc { get; } = lockedUntilUtc;
}

/// <summary>A required upstream system (AuthenticationCore or RecordAuditTrails) failed. Signing fails closed.</summary>
public sealed class DependencyUnavailableException(string message, Exception? inner = null) : SignatureException(message, inner);
