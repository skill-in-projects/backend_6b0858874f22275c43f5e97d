using Npgsql;

namespace Backend.Middleware;

/// <summary>
/// When the database refuses because the project is over its monthly limit, answer every endpoint with a calm 503
/// instead of an error. The PostgreSQL message contains "exceeded" and "quota" in that case.
/// </summary>
public class DatabaseRestingMiddleware
{
    private readonly RequestDelegate _next;

    public DatabaseRestingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (IsQuotaError(ex) && !context.Response.HasStarted)
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"code\":\"database_resting\",\"error\":\"This app is resting and will be back soon.\"}");
        }
    }

    private static bool IsQuotaError(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is NpgsqlException
                && current.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)
                && current.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
