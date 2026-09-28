using Coglatas.Web.Models;

namespace Coglatas.Web.Middleware;

/// <summary>
/// Rejects malformed or ambiguous multipart shapes for the two canonical file-upload
/// endpoints before MVC model binding can interpret case-insensitive duplicate fields.
/// </summary>
public sealed class FileUploadFormBoundaryMiddleware(RequestDelegate next)
{
    private const string FileFieldName = "File";
    private static readonly HashSet<string> AllowedScalarFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "OwnerType",
            "OwnerId"
        };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsFileUploadRequest(context.Request))
        {
            await next(context);
            return;
        }

        if (!context.Request.HasFormContentType)
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

        if (form.Keys.Any(field => !AllowedScalarFields.Contains(field)) ||
            form.Files.Count != 1 ||
            form.Files.Any(file => !string.Equals(file.Name, FileFieldName, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteInvalidRequestAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsFileUploadRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (string.Equals(request.Path.Value, "/api/files", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(request.Path.Value, "/api/attachments", StringComparison.OrdinalIgnoreCase));

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
