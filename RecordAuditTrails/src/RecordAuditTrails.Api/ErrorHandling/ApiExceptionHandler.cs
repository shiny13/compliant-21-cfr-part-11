using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RecordAuditTrails.Core.Exceptions;

namespace RecordAuditTrails.Api.ErrorHandling;

/// <summary>Maps expected domain failures to RFC 7807 problem responses; anything else is a generic 500 with no internals leaked.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            AuditValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            AccessDeniedException => (StatusCodes.Status403Forbidden, "Forbidden"),
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            ImmutableDataException => (StatusCodes.Status409Conflict, "Immutable data"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
        };

        string? detail;
        if (exception is AuditTrailException)
        {
            detail = exception.Message;
        }
        else
        {
            detail = "An unexpected error occurred.";
            logger.LogError(exception, "Unhandled exception for {Path}", httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = status, Title = title, Detail = detail },
            options: null,
            contentType: "application/problem+json",
            cancellationToken);
        return true;
    }
}
