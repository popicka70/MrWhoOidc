using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.KeyGen.Configuration;
using MrWhoOidc.KeyGen.Domain.Services;
using MrWhoOidc.KeyGen.Persistence;
using System.Diagnostics;
using System.Security.Cryptography;

namespace MrWhoOidc.KeyGen.Tests;

[TestClass]
public class LicenseGenerationServicePerformanceTests
{
    private string? _tempKeyPath;
    private DbContextOptions<KeyGenDbContext>? _dbOptions;
    private ILogger<LicenseGenerationService>? _logger;
    private IOptions<KeyGenOptions>? _options;

    [TestInitialize]
    public void Setup()
    {
        // 1. Create a temporary ECDSA key
        _tempKeyPath = Path.GetTempFileName();
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var privateKeyPem = ecdsa.ExportECPrivateKeyPem();
            File.WriteAllText(_tempKeyPath, privateKeyPem);
        }

        // 2. Setup DbContext Options
        _dbOptions = new DbContextOptionsBuilder<KeyGenDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        // 3. Setup Options
        _options = Options.Create(new KeyGenOptions
        {
            LicensingPrivateKeyPath = _tempKeyPath
        });

        // 4. Setup Logger
        _logger = new LoggerFactory().CreateLogger<LicenseGenerationService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempKeyPath != null && File.Exists(_tempKeyPath))
        {
            File.Delete(_tempKeyPath);
        }
    }

    [TestMethod]
    public async Task MeasureLicenseGenerationPerformance()
    {
        int iterations = 100; // Run enough iterations to get a stable average

        // Warm up
        using (var dbContext = new KeyGenDbContext(_dbOptions!))
        {
            var service = new LicenseGenerationService(dbContext, _options!, _logger!);
            await service.GenerateLicenseTokenAsync(
                tier: "professional",
                organization: "WarmupOrg",
                notBefore: DateTimeOffset.UtcNow,
                expiresAt: DateTimeOffset.UtcNow.AddDays(30),
                scope: "platform"
            );
        }

        var stopwatch = Stopwatch.StartNew();

        var tasks = new List<Task>();
        for (int i = 0; i < iterations; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var dbContext = new KeyGenDbContext(_dbOptions!);
                var service = new LicenseGenerationService(dbContext, _options!, _logger!);
                await service.GenerateLicenseTokenAsync(
                    tier: "professional",
                    organization: $"TestOrg{i}",
                    notBefore: DateTimeOffset.UtcNow,
                    expiresAt: DateTimeOffset.UtcNow.AddDays(30),
                    scope: "platform"
                );
            }));
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        Console.WriteLine($"Generated {iterations} licenses in {stopwatch.ElapsedMilliseconds} ms");
    }
}
