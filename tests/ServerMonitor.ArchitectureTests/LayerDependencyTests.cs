namespace ServerMonitor.ArchitectureTests;

public sealed class LayerDependencyTests
{
    [Fact]
    public void Domain_does_not_depend_on_other_projects() =>
        Assemblies.Domain.ReferencedSolutionNames().ShouldBeEmpty();

    [Fact]
    public void Contracts_do_not_depend_on_other_projects() =>
        Assemblies.Contracts.ReferencedSolutionNames().ShouldBeEmpty();

    [Fact]
    public void Protocol_does_not_depend_on_other_projects() =>
        Assemblies.Protocol.ReferencedSolutionNames().ShouldBeEmpty();

    [Fact]
    public void Dashboard_depends_only_on_the_contracts() =>
        Assemblies.Dashboard.ReferencedSolutionNames().ShouldBe([Assemblies.Contracts.Name()]);

    [Fact]
    public void Agent_depends_only_on_the_protocol() =>
        Assemblies.Agent.ReferencedSolutionNames().ShouldBe([Assemblies.Protocol.Name()]);

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Npgsql")]
    [InlineData("Confluent.Kafka")]
    public void Domain_does_not_depend_on_frameworks(string forbiddenPrefix) =>
        Assemblies.Domain.ReferencedNames()
            .ShouldNotContain(n => n.StartsWith(forbiddenPrefix, StringComparison.Ordinal));

    [Fact]
    public void Application_depends_only_on_domain_and_contracts() =>
        Assemblies.Application.ReferencedSolutionNames()
            .ShouldBeSubsetOf([Assemblies.Domain.Name(), Assemblies.Contracts.Name()]);

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Npgsql")]
    [InlineData("Confluent.Kafka")]
    public void Application_does_not_depend_on_infrastructure_frameworks(string forbiddenPrefix) =>
        Assemblies.Application.ReferencedNames()
            .ShouldNotContain(n => n.StartsWith(forbiddenPrefix, StringComparison.Ordinal));

    [Fact]
    public void Infrastructure_does_not_depend_on_hosts()
    {
        var hostNames = Assemblies.Hosts.Select(h => h.Name()).ToArray();

        Assemblies.Infrastructure.ReferencedSolutionNames().ShouldNotContain(n => hostNames.Contains(n));
    }

    [Fact]
    public void Hosts_do_not_depend_on_each_other()
    {
        foreach (var host in Assemblies.Hosts)
        {
            var otherHosts = Assemblies.Hosts.Where(h => h != host).Select(h => h.Name()).ToArray();

            host.ReferencedSolutionNames().ShouldNotContain(
                n => otherHosts.Contains(n),
                $"{host.Name()} must not reference other hosts");
        }
    }
}
