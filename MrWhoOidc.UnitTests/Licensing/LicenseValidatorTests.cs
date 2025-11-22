using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Licensing.Options;
using MrWhoOidc.Auth.Licensing.Validators;

namespace MrWhoOidc.UnitTests.Licensing;

[TestClass]
public sealed class LicenseValidatorTests
{
    [TestMethod]
    public void SupportedIssuers_ExposeLegacyAndKeyGenValues()
    {
        var supportedIssuers = LicenseValidator.SupportedIssuers.ToList();

        CollectionAssert.Contains(supportedIssuers, "MrWhoOidc-KeyGen");
        CollectionAssert.Contains(supportedIssuers, "MrWhoOidc-License-Authority");
    }

    [TestMethod]
    public void CreateValidationParameters_RegistersAllSupportedIssuers()
    {
        var validator = new LicenseValidator(
            Options.Create(new LicensingOptions()),
            NullLogger<LicenseValidator>.Instance);

        var parameters = InvokeCreateValidationParameters(validator);

        Assert.AreEqual("MrWhoOidc-KeyGen", parameters.ValidIssuer);
        CollectionAssert.AreEquivalent(
            LicenseValidator.SupportedIssuers.ToList(),
            parameters.ValidIssuers?.ToList() ?? new List<string>());
    }

    private static TokenValidationParameters InvokeCreateValidationParameters(LicenseValidator validator)
    {
        var method = typeof(LicenseValidator)
            .GetMethod("CreateValidationParameters", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(method, "LicenseValidator.CreateValidationParameters should exist.");

        var result = method.Invoke(validator, Array.Empty<object?>());
        Assert.IsNotNull(result);

        return (TokenValidationParameters)result;
    }
}
