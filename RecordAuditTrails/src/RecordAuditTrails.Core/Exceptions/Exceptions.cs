namespace RecordAuditTrails.Core.Exceptions;

/// <summary>Base type for expected, client-correctable failures. The API maps each subtype to an HTTP status.</summary>
public abstract class AuditTrailException(string message) : Exception(message);

public sealed class AuditValidationException(string message) : AuditTrailException(message);

public sealed class NotFoundException(string message) : AuditTrailException(message);

public sealed class ConcurrencyConflictException(string message) : AuditTrailException(message);

public sealed class AccessDeniedException(string message) : AuditTrailException(message);

/// <summary>Raised when something tries to alter or remove data that is append-only by design.</summary>
public sealed class ImmutableDataException(string message) : AuditTrailException(message);
