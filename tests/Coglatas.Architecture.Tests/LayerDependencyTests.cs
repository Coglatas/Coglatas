using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Coglatas.Architecture.Tests;

public sealed class LayerDependencyTests
{
    private static readonly ArchUnitNET.Domain.Architecture LoadedArchitecture = new ArchLoader()
        .LoadAssemblies(
            System.Reflection.Assembly.Load("Coglatas.Domain"),
            System.Reflection.Assembly.Load("Coglatas.Application"),
            System.Reflection.Assembly.Load("Coglatas.Infrastructure"),
            System.Reflection.Assembly.Load("Coglatas.Web"))
        .Build();

    private static readonly IObjectProvider<IType> DomainLayer =
        Types().That().ResideInAssembly("Coglatas.Domain").As("Domain");

    private static readonly IObjectProvider<IType> ApplicationLayer =
        Types().That().ResideInAssembly("Coglatas.Application").As("Application");

    private static readonly IObjectProvider<IType> InfrastructureLayer =
        Types().That().ResideInAssembly("Coglatas.Infrastructure").As("Infrastructure");

    private static readonly IObjectProvider<IType> WebLayer =
        Types().That().ResideInAssembly("Coglatas.Web").As("Web");

    [Fact]
    public void Domain_DoesNotDependOnApplication()
    {
        AssertNoDependency(DomainLayer, ApplicationLayer, "Domain must remain independent of Application.");
    }

    [Fact]
    public void Domain_DoesNotDependOnInfrastructure()
    {
        AssertNoDependency(DomainLayer, InfrastructureLayer, "Domain must remain independent of Infrastructure.");
    }

    [Fact]
    public void Domain_DoesNotDependOnWeb()
    {
        AssertNoDependency(DomainLayer, WebLayer, "Domain must remain independent of the web host.");
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructure()
    {
        AssertNoDependency(
            ApplicationLayer,
            InfrastructureLayer,
            "Application may depend on Domain, but not on Infrastructure.");
    }

    [Fact]
    public void Application_DoesNotDependOnWeb()
    {
        AssertNoDependency(ApplicationLayer, WebLayer, "Application must remain independent of the web host.");
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnWeb()
    {
        AssertNoDependency(InfrastructureLayer, WebLayer, "Infrastructure must remain independent of the web host.");
    }

    private static void AssertNoDependency(
        IObjectProvider<IType> source,
        IObjectProvider<IType> forbidden,
        string reason)
    {
        IArchRule rule = Types()
            .That()
            .Are(source)
            .Should()
            .NotDependOnAny(forbidden)
            .Because(reason);

        rule.Check(LoadedArchitecture);
    }
}
