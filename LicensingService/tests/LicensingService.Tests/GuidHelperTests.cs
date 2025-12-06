using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LicensingService.Tests;

[TestClass]
public class GuidHelperTests
{
    [TestMethod]
    public void NewId_GeneratesValidUuidV7()
    {
        // Act
        var id = Core.GuidHelper.NewId();
        
        // Assert
        Assert.AreNotEqual(Guid.Empty, id);
        
        // Verify it's a valid GUID
        Assert.IsTrue(Guid.TryParse(id.ToString(), out _));
    }

    [TestMethod]
    public void NewId_GeneratesUniqueIds()
    {
        // Act
        var ids = Enumerable.Range(0, 1000)
            .Select(_ => Core.GuidHelper.NewId())
            .ToList();
        
        // Assert - all should be unique
        Assert.AreEqual(1000, ids.Distinct().Count());
    }

    [TestMethod]
    public void NewId_GeneratesTimeOrderedIds()
    {
        // Act
        var id1 = Core.GuidHelper.NewId();
        Thread.Sleep(10); // Small delay to ensure different timestamp
        var id2 = Core.GuidHelper.NewId();
        
        // Assert - id2 should be "greater" than id1 due to time ordering
        // Compare the string representation (UUIDv7 is lexicographically sortable)
        Assert.IsTrue(string.Compare(id1.ToString(), id2.ToString(), StringComparison.Ordinal) < 0,
            "UUIDv7 should be time-ordered (id2 should be greater than id1)");
    }

    [TestMethod]
    public void NewId_HasCorrectVersionBits()
    {
        // Act
        var id = Core.GuidHelper.NewId();
        var bytes = id.ToByteArray();
        
        // Note: .NET uses a different byte order for GUIDs
        // The version is in the high nibble of byte 7 in the internal representation
        // For UUIDv7, version should be 7 (0111)
        
        // Convert to big-endian representation for verification
        var hex = id.ToString("N");
        var versionChar = hex[12]; // Position of version nibble in string representation
        
        // Assert - version nibble should be 7
        Assert.AreEqual('7', versionChar, "UUIDv7 version nibble should be 7");
    }
}
