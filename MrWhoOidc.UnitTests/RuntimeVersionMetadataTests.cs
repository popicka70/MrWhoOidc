using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RuntimeVersionMetadataTests
{
    [TestMethod]
    public void FromMetadata_UsesEmbeddedCommit_WhenPresent()
    {
        var info = RuntimeVersionMetadata.RuntimeVersionInfo.FromMetadata(
            service: "MrWhoOidc.WebAuth",
            assemblyVersion: "1.0.0.0",
            fileVersion: "1.0.0.0",
            informationalVersion: "1.2.3+embeddedsha",
            environmentVariableReader: key => key switch
            {
                "RENDER_GIT_COMMIT" => "rendersha",
                "RENDER_GIT_BRANCH" => "master",
                _ => null
            });

        Assert.AreEqual("1.2.3", info.Version);
        Assert.AreEqual("1.2.3+embeddedsha", info.InformationalVersion);
        Assert.AreEqual("embeddedsha", info.Commit);
        Assert.AreEqual("master", info.Branch);
    }

    [TestMethod]
    public void FromMetadata_FallsBack_To_Render_Git_Metadata()
    {
        var info = RuntimeVersionMetadata.RuntimeVersionInfo.FromMetadata(
            service: "MrWhoOidc.WebAuth",
            assemblyVersion: "1.0.0.0",
            fileVersion: "1.0.0.0",
            informationalVersion: "1.0.0",
            environmentVariableReader: key => key switch
            {
                "RENDER_GIT_COMMIT" => "abc123",
                "RENDER_GIT_BRANCH" => "master",
                "RENDER_GIT_REPO_SLUG" => "MrWhoProjects/MrWhoOidc",
                "RENDER_SERVICE_NAME" => "mrwho-oidc-prod",
                _ => null
            });

        Assert.AreEqual("1.0.0", info.Version);
        Assert.AreEqual("1.0.0+abc123", info.InformationalVersion);
        Assert.AreEqual("abc123", info.Commit);
        Assert.AreEqual("master", info.Branch);
        Assert.AreEqual("MrWhoProjects/MrWhoOidc", info.RepoSlug);
        Assert.AreEqual("mrwho-oidc-prod", info.ServiceName);
    }
}