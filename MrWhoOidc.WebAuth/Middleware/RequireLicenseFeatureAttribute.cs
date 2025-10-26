using System;

namespace MrWhoOidc.WebAuth.Middleware;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = true, Inherited = true)]
public sealed class RequireLicenseFeatureAttribute : Attribute
{
    public RequireLicenseFeatureAttribute(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        FeatureName = featureName;
    }

    public string FeatureName { get; }
}
