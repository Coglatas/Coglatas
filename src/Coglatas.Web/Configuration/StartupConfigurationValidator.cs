using System.Data.Common;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Files;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace Coglatas.Web.Configuration;

public sealed class StartupConfigurationValidator(
    IOptions<TenancyOptions> tenancyOptions,
    IOptions<FileStorageOptions> fileStorageOptions,
    IOptions<SecurityOptions> securityOptions,
    IOptions<PlatformOptions> platformOptions,
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    CsrfProtectionState csrfProtectionState,
    IWebHostEnvironment environment,
    ILogger<StartupConfigurationValidator> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                logger.LogError("Configuration validation failed: {Error}", error);
            }

            throw new InvalidOperationException("Coglatas Portal configuration validation failed: " + string.Join(" | ", errors));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var tenancy = tenancyOptions.Value;
        var fileStorage = fileStorageOptions.Value;
        var security = securityOptions.Value;
        var platform = platformOptions.Value;
        var isProduction = environment.IsProduction();

        if (!Enum.IsDefined(tenancy.AppMode))
        {
            errors.Add("Tenancy:AppMode must be one of SaaS, OnPremSingleTenant, or OnPremMultiTenant.");
        }

        if (!Enum.IsDefined(tenancy.TenantResolutionStrategy))
        {
            errors.Add("Tenancy:TenantResolutionStrategy is not valid.");
        }

        if (tenancy.AppMode == AppMode.OnPremSingleTenant && string.IsNullOrWhiteSpace(tenancy.DefaultTenantSlug))
        {
            errors.Add("Tenancy:DefaultTenantSlug is required for OnPremSingleTenant.");
        }

        if (isProduction &&
            tenancy.TenantResolutionStrategy == TenantResolutionStrategy.HeaderForDevelopmentOnly &&
            !tenancy.AllowDevelopmentHeaderInProduction)
        {
            errors.Add("Development header tenant resolution is disabled in production unless explicitly allowed.");
        }

        if (tenancy.TenantResolutionStrategy == TenantResolutionStrategy.HeaderForDevelopmentOnly &&
            !tenancy.AllowDevelopmentHeaderTenantResolution)
        {
            errors.Add("Tenancy:AllowDevelopmentHeaderTenantResolution must be true when using HeaderForDevelopmentOnly.");
        }

        if (isProduction && tenancy.AllowDevelopmentHeaderInProduction)
        {
            errors.Add("Tenancy:AllowDevelopmentHeaderInProduction must be false for production deployments.");
        }

        if (fileStorage.MaxFileSizeBytes <= 0)
        {
            errors.Add("FileStorage:MaxFileSizeBytes must be positive.");
        }

        if (fileStorage.AllowedExtensions.Length == 0)
        {
            errors.Add("FileStorage:AllowedExtensions must contain at least one extension.");
        }

        if (fileStorage.AllowedContentTypes.Length == 0)
        {
            errors.Add("FileStorage:AllowedContentTypes must contain at least one MIME type.");
        }

        foreach (var extension in fileStorage.AllowedExtensions)
        {
            if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith(".", StringComparison.Ordinal))
            {
                errors.Add($"FileStorage:AllowedExtensions contains invalid extension '{extension}'.");
            }
        }

        foreach (var contentType in fileStorage.AllowedContentTypes)
        {
            if (string.IsNullOrWhiteSpace(contentType) || !contentType.Contains('/', StringComparison.Ordinal))
            {
                errors.Add($"FileStorage:AllowedContentTypes contains invalid MIME type '{contentType}'.");
            }
        }

        switch (fileStorage.Provider)
        {
            case "LocalFileSystem":
                ValidateLocalFileSystem(errors, fileStorage);
                break;
            case "ObjectStorage":
            case "S3Compatible":
            case "OCIObjectStorage":
                ValidateObjectStorage(errors, fileStorage);
                break;
            default:
                errors.Add($"FileStorage:Provider '{fileStorage.Provider}' is not supported.");
                break;
        }

        if (security.EnableCsrfProtection)
        {
            if (!csrfProtectionState.IsMiddlewareActive)
            {
                errors.Add("Security:EnableCsrfProtection is true, but CSRF middleware is not active.");
            }

            if (serviceProvider.GetService<IAntiforgery>() is null)
            {
                errors.Add("Security:EnableCsrfProtection is true, but antiforgery services are not registered.");
            }
        }

        foreach (var error in ForwardedHeadersConfiguration.GetConfigurationErrors(configuration))
        {
            errors.Add(error);
        }

        if (isProduction)
        {
            if (security.CookieSecurePolicy != CookieSecurePolicy.Always)
            {
                errors.Add("Security:CookieSecurePolicy must be Always in production.");
            }

            if (!security.RequireHttps)
            {
                errors.Add("Security:RequireHttps must be true in production.");
            }

            if (!security.EnableHsts)
            {
                errors.Add("Security:EnableHsts must be true in production.");
            }

            if (platform.PlatformAdminSetupMode)
            {
                errors.Add("Platform:PlatformAdminSetupMode must not be enabled in production.");
            }

            if (string.IsNullOrWhiteSpace(configuration["DataProtection:KeysPath"]))
            {
                errors.Add("DataProtection:KeysPath must be configured in production so auth cookies survive restarts and multi-instance deployments.");
            }

            ValidateProductionConnectionString(errors, configuration.GetConnectionString("DefaultConnection"));
            ValidateProductionSecret(errors, "FileStorage:SecretKey", fileStorage.SecretKey, required: fileStorage.Provider is "ObjectStorage" or "S3Compatible" or "OCIObjectStorage");
        }

        if (security.LoginLockoutEnabled && security.MaxFailedLoginAttempts <= 0)
        {
            errors.Add("Security:MaxFailedLoginAttempts must be positive when lockout is enabled.");
        }

        if (security.LoginLockoutEnabled && security.LoginLockoutDurationMinutes <= 0)
        {
            errors.Add("Security:LoginLockoutDurationMinutes must be positive when lockout is enabled.");
        }

        return errors;
    }

    private static void ValidateLocalFileSystem(ICollection<string> errors, FileStorageOptions fileStorage)
    {
        if (string.IsNullOrWhiteSpace(fileStorage.RootPath))
        {
            errors.Add("FileStorage:RootPath is required for LocalFileSystem.");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetFullPath(fileStorage.RootPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            errors.Add($"FileStorage:RootPath could not be created: {ex.Message}");
        }
    }

    private static void ValidateObjectStorage(ICollection<string> errors, FileStorageOptions fileStorage)
    {
        if (string.IsNullOrWhiteSpace(fileStorage.BucketName))
        {
            errors.Add("FileStorage:BucketName is required for object storage providers.");
        }

        if (string.IsNullOrWhiteSpace(fileStorage.Region) &&
            string.Equals(fileStorage.Provider, "ObjectStorage", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("FileStorage:Region is required for ObjectStorage.");
        }

        if (string.IsNullOrWhiteSpace(fileStorage.Endpoint) &&
            string.Equals(fileStorage.Provider, "S3Compatible", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("FileStorage:Endpoint is required for S3Compatible storage.");
        }
    }

    private static void ValidateProductionConnectionString(ICollection<string> errors, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add("ConnectionStrings:DefaultConnection is required in production.");
            return;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var password = TryGetValue(builder, "Password") ?? TryGetValue(builder, "Pwd");
            if (string.IsNullOrWhiteSpace(password))
            {
                errors.Add("ConnectionStrings:DefaultConnection must include a database password or equivalent secret in production.");
                return;
            }

            ValidateProductionSecret(errors, "ConnectionStrings:DefaultConnection:Password", password, required: true);
        }
        catch (ArgumentException)
        {
            errors.Add("ConnectionStrings:DefaultConnection is not a valid connection string.");
        }
    }

    private static void ValidateProductionSecret(ICollection<string> errors, string name, string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                errors.Add($"{name} is required in production.");
            }

            return;
        }

        var normalized = value.Trim();
        if (normalized.Length < 12 ||
            normalized.Contains('<', StringComparison.Ordinal) ||
            normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("changeme", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("default", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("example", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{name} appears to contain a weak or placeholder secret.");
        }
    }

    private static string? TryGetValue(DbConnectionStringBuilder builder, string key)
    {
        return builder.TryGetValue(key, out var value) ? value?.ToString() : null;
    }
}
