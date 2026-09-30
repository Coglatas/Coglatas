using Coglatas.Application;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Infrastructure;
using Coglatas.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.Projects;

public sealed class ResearchPlanDependencyInjectionTests
{
    [Fact]
    [Trait("Scope", "Issue364")]
    public async Task ApplicationOnlyCompositionFailsClosedForResearchPlanPersistence()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IResearchPlanRepository>();

        Assert.Null(await repository.GetForTaskAsync(Guid.NewGuid()));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddPlanAsync(new ResearchPlan()));
        Assert.Equal("Research Plan persistence is unavailable.", exception.Message);
    }

    [Fact]
    [Trait("Scope", "Issue364")]
    public void InfrastructureCompositionOverridesTheApplicationOnlyFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=127.0.0.1;Port=5432;Database=coglatas_tests;Username=test;Password=test"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddScoped<ICurrentUser, UnauthenticatedCurrentUser>();
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<ResearchPlanRepository>(
            scope.ServiceProvider.GetRequiredService<IResearchPlanRepository>());
    }

    private sealed class UnauthenticatedCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
        public Guid? SessionId => null;
        public string? Email => null;
        public Domain.Enums.SystemRole? SystemRole => null;
        public bool IsAuthenticated => false;
    }
}
