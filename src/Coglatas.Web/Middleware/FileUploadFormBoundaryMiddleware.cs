using Coglatas.Web.Models;

namespace Coglatas.Web.Middleware;

/// <summary>
/// Rejects malformed or ambiguous multipart shapes for canonical file-upload
/// endpoints before MVC model binding can interpret case-insensitive duplicate fields.
/// </summary>
public sealed class FileUploadFormBoundaryMiddleware(RequestDelegate next)
{
    private const string FileFieldName = "File";
    private static readonly HashSet<string> AttachmentScalarFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "OwnerType",
            "OwnerId"
        };
    private static readonly HashSet<string> ArtifactVersionScalarFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ChangeNote"
        };

    public async Task InvokeAsync(HttpContext context)
    {
        var allowedScalarFields = GetAllowedScalarFields(context.Request);
        if (allowedScalarFields is null)
        {
            await next(context);
            return;
        }

        if (!context.Request.HasFormContentType ||
            context.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
        {
            await next(context);
            return;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (BadHttpRequestException exception) when (
            exception.StatusCode is >= StatusCodes.Status400BadRequest and < StatusCodes.Status500InternalServerError)
        {
            await WriteInvalidRequestAsync(context);
            return;
        }
        catch (InvalidDataException)
        {
            await WriteInvalidRequestAsync(context);
            return;
        }

        if (form.Any(field => !allowedScalarFields.Contains(field.Key) || field.Value.Count != 1) ||
            form.Files.Count != 1 ||
            form.Files.Any(file => !string.Equals(file.Name, FileFieldName, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteInvalidRequestAsync(context);
            return;
        }

        await next(context);
    }

    private static HashSet<string>? GetAllowedScalarFields(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return null;
        }

        if (string.Equals(request.Path.Value, "/api/files", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Path.Value, "/api/attachments", StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentScalarFields;
        }

        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: 4 } &&
               string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[1], "artifacts", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(segments[2], out _) &&
               string.Equals(segments[3], "versions", StringComparison.OrdinalIgnoreCase)
            ? ArtifactVersionScalarFields
            : null;
    }

    private static Task WriteInvalidRequestAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new ErrorResponse(
            "InvalidRequest",
            "The multipart form body is invalid.",
            context.TraceIdentifier));
    }
}
