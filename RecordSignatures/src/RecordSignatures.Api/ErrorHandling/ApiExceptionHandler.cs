using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RecordSignatures.Core.Exceptions;

namespace RecordSignatures.Api.ErrorHandling;

/// <summary>Maps expected business failures to RFC 7807 problem responses; anything else is a generic 500 with no internals leaked.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            SignatureValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            SignatureAuthenticationException => (StatusCodes.Status401Unauthorized, "Signature authentication failed"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden"),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            SignerLockedException => (StatusCodes.Status423Locked, "Signing locked"),
            DependencyUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Dependency unavailable"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
        };

        var problem = new ProblemDetails { Status = status, Title = title };
        if (exception is SignatureException)
        {
            problem.Detail = exception.Message;
            if (exception is DependencyUnavailableException)
            {
                logger.LogError(exception, "Dependency failure for {Path}", httpContext.Request.Path);
            }
        }
        else
        {
            problem.Detail = "An unexpected error occurred.";
            logger.LogError(exception, "Unhandled exception for {Path}", httpContext.Request.Path);
        }
        if (exception is SignerLockedException locked)
        {
            problem.Extensions["lockedUntilUtc"] = locked.LockedUntilUtc;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }
}
