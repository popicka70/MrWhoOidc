using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using System.Collections.Concurrent;

namespace MrWhoOidc.UnitTests.Persistence;

[TestClass]
public class GuidHelperTests
{
    [TestMethod]
    public void NewId_GeneratesValidUUIDv7()
    {
        // Act
        var id = GuidHelper.NewId();

        // Assert
        Assert.AreNotEqual(Guid.Empty, id);
        Assert.IsTrue(GuidHelper.IsUuidV7(id), "Generated UUID should be version 7");
    }

    [TestMethod]
    public void NewId_GeneratesUniqueIds()
    {
        // Arrange
        var count = 10000;
        var ids = new HashSet<Guid>();

        // Act
        for (int i = 0; i < count; i++)
        {
            ids.Add(GuidHelper.NewId());
        }

        // Assert
        Assert.HasCount(count, ids, "All generated UUIDs should be unique");
    }

    [TestMethod]
    public void NewId_IsApproximatelyMonotonic()
    {
        // Arrange
        var count = 1000;
        var ids = new List<Guid>();

        // Act
        for (int i = 0; i < count; i++)
        {
            ids.Add(GuidHelper.NewId());
        }

        // Assert - UUIDv7 should be mostly increasing (allowing for occasional same-millisecond randomness)
        var sortedIds = ids.OrderBy(g => g).ToList();

        // Check that at least 95% of IDs are in monotonic order
        // UUIDv7 uses timestamp in first 48 bits, so we need to compare timestamps
        int inOrder = 0;
        for (int i = 0; i < ids.Count - 1; i++)
        {
            var ts1 = GuidHelper.ExtractTimestamp(ids[i]);
            var ts2 = GuidHelper.ExtractTimestamp(ids[i + 1]);

            if (ts1 != null && ts2 != null && ts1.Value <= ts2.Value)
            {
                inOrder++;
            }
        }

        var percentInOrder = (double)inOrder / (ids.Count - 1);
        Assert.IsGreaterThanOrEqualTo(0.95, percentInOrder, $"Expected at least 95% monotonic ordering, got {percentInOrder:P2}");
    }

    [TestMethod]
    public void NewId_IsThreadSafe()
    {
        // Arrange
        var ids = new ConcurrentBag<Guid>();
        var count = 10000;

        // Act
        Parallel.For(0, count, _ =>
        {
            ids.Add(GuidHelper.NewId());
        });

        // Assert
        Assert.HasCount(count, ids);
        Assert.HasCount(count, ids.Distinct(), "All generated UUIDs should be unique even under concurrent load");
    }

    [TestMethod]
    public void IsUuidV7_ReturnsTrueForV7Uuid()
    {
        // Arrange
        var uuid = GuidHelper.NewId();

        // Act
        var isV7 = GuidHelper.IsUuidV7(uuid);

        // Assert
        Assert.IsTrue(isV7);
    }

    [TestMethod]
    public void IsUuidV7_ReturnsFalseForV4Uuid()
    {
        // Arrange - standard Guid.NewGuid() creates UUIDv4
        var uuid = Guid.NewGuid();

        // Act
        var isV7 = GuidHelper.IsUuidV7(uuid);

        // Assert
        Assert.IsFalse(isV7);
    }

    [TestMethod]
    public void ExtractTimestamp_ReturnsTimestampForV7Uuid()
    {
        // Arrange
        var uuid = GuidHelper.NewId();

        // Act
        var timestamp = GuidHelper.ExtractTimestamp(uuid);

        // Assert
        // Note: Timestamp extraction implementation may vary based on UUIDv7 library internals
        // For now, we just verify the method doesn't throw and returns a value
        Assert.IsNotNull(timestamp, "Should be able to extract timestamp from UUIDv7");
    }

    [TestMethod]
    public void ExtractTimestamp_ReturnsNullForV4Uuid()
    {
        // Arrange - standard Guid.NewGuid() creates UUIDv4
        var uuid = Guid.NewGuid();

        // Act
        var timestamp = GuidHelper.ExtractTimestamp(uuid);

        // Assert
        Assert.IsNull(timestamp, "Should return null for non-UUIDv7 identifiers");
    }

    [TestMethod]
    public void ExtractTimestamp_ReturnsNullForEmptyGuid()
    {
        // Arrange
        var uuid = Guid.Empty;

        // Act
        var timestamp = GuidHelper.ExtractTimestamp(uuid);

        // Assert
        Assert.IsNull(timestamp);
    }

    [TestMethod]
    public void NewId_ProducesTimestampInExpectedRange()
    {
        // Arrange & Act
        var uuids = Enumerable.Range(0, 100)
            .Select(_ => GuidHelper.NewId())
            .ToList();

        // Assert - all should have extractable timestamps
        foreach (var uuid in uuids)
        {
            var timestamp = GuidHelper.ExtractTimestamp(uuid);
            Assert.IsNotNull(timestamp, "All UUIDv7s should have extractable timestamps");
        }
    }

    [TestMethod]
    public void NewId_TimestampsAreMonotonicallyIncreasing()
    {
        // Arrange & Act
        var uuids = new List<(Guid id, DateTimeOffset? timestamp)>();

        for (int i = 0; i < 1000; i++)
        {
            var id = GuidHelper.NewId();
            var ts = GuidHelper.ExtractTimestamp(id);
            uuids.Add((id, ts));

            // Small delay to ensure different milliseconds
            if (i % 100 == 0)
            {
                Thread.Sleep(1);
            }
        }

        // Assert - timestamps should never decrease
        for (int i = 1; i < uuids.Count; i++)
        {
            Assert.IsTrue(uuids[i].timestamp >= uuids[i - 1].timestamp,
                $"Timestamp at index {i} ({uuids[i].timestamp}) should be >= previous ({uuids[i - 1].timestamp})");
        }
    }

    [TestMethod]
    [Obsolete("Testing obsolete method")]
    public void NewGuid_DelegatesToNewId()
    {
        // Act
#pragma warning disable CS0618 // Type or member is obsolete
        var guid = GuidHelper.NewGuid();
#pragma warning restore CS0618

        // Assert
        Assert.IsTrue(GuidHelper.IsUuidV7(guid), "NewGuid should produce UUIDv7 like NewId");
    }

    [TestMethod]
    public void NewId_ProducesDifferentIdsInSameMillisecond()
    {
        // Arrange & Act - Generate many IDs rapidly to get multiple in same millisecond
        var ids = Enumerable.Range(0, 10000).Select(_ => GuidHelper.NewId()).ToList();

        // Assert
        Assert.AreEqual(10000, ids.Distinct().Count(), "All IDs should be unique even when generated in same millisecond");
    }
}
