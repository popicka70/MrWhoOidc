using System;
using System.Threading;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.TestDoubles;

internal sealed class FakeEmailConfirmationWorkflow : IEmailConfirmationWorkflow
{
    public Task<EmailConfirmationCreateResult> SendPrimaryAsync(User user, CancellationToken cancellationToken = default)
        => Task.FromResult(CreateSuccessResult());

    public Task<EmailConfirmationCreateResult> SendAlternativeAsync(User user, UserAlternativeEmail alternative, CancellationToken cancellationToken = default)
        => Task.FromResult(CreateSuccessResult());

    private static EmailConfirmationCreateResult CreateSuccessResult()
        => new(EmailConfirmationCreateStatus.Created, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(10));
}
