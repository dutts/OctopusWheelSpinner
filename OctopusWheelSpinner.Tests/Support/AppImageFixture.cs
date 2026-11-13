using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Xunit;

[assembly: AssemblyFixture(typeof(OctopusWheelSpinner.Tests.Support.AppImageFixture))]

namespace OctopusWheelSpinner.Tests.Support;

/// <summary>Builds the app image once per test run (each test then gets its own network, so the shared `octopus` alias can't resolve to another test's mock).</summary>
public sealed class AppImageFixture : IAsyncLifetime
{
    public IImage Image { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // The build context is the repo root, as for the real Dockerfile; .slnx isn't recognised by GetSolutionDirectory
        var repoRoot = CommonDirectoryPath.GetGitDirectory();

        // A fixed name with WithDeleteIfExists(false) lets later runs reuse Docker's layer cache
        var image = new ImageFromDockerfileBuilder()
            .WithName("octopus-wheel-spinner-tests")
            .WithDockerfileDirectory(repoRoot, string.Empty)
            .WithDockerfile("OctopusWheelSpinner/Dockerfile")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(TestContext.Current.CancellationToken);
        Image = image;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
