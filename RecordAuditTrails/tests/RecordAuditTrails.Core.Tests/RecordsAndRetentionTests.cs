using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Records;
using RecordAuditTrails.Core.Retention;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Core.Tests;

public class RecordFieldsTests
{
    [Fact]
    public void Serialize_SortsKeysSoEqualContentIsIdentical()
    {
        var a = RecordFields.Serialize(new Dictionary<string, string?> { ["b"] = "2", ["a"] = "1" });
        var b = RecordFields.Serialize(new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" });
        Assert.Equal(a, b);
        Assert.StartsWith("{\"a\"", a);
    }

    [Fact]
    public void Deserialize_RoundTripsIncludingNulls()
    {
        var fields = RecordFields.Deserialize(RecordFields.Serialize(new Dictionary<string, string?> { ["a"] = null, ["b"] = "x" }));
        Assert.Null(fields["a"]);
        Assert.Equal("x", fields["b"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("null")]
    public void Deserialize_ReturnsEmptyForBlankInput(string? json) => Assert.Empty(RecordFields.Deserialize(json));

    [Fact]
    public void Validate_AcceptsNormalFields() =>
        RecordFields.Validate(new Dictionary<string, string?> { ["pH"] = "7", ["note"] = null });

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Title")]
    [InlineData("RecordType")]
    [InlineData("IsDeleted")]
    public void Validate_RejectsBadOrReservedNames(string name) =>
        Assert.Throws<AuditValidationException>(() => RecordFields.Validate(new Dictionary<string, string?> { [name] = "x" }));

    [Fact]
    public void Validate_RejectsOverlongNameValueAndTooManyFields()
    {
        Assert.Throws<AuditValidationException>(() =>
            RecordFields.Validate(new Dictionary<string, string?> { [new string('n', RecordFields.MaxNameLength + 1)] = "x" }));
        Assert.Throws<AuditValidationException>(() =>
            RecordFields.Validate(new Dictionary<string, string?> { ["a"] = new string('v', RecordFields.MaxValueLength + 1) }));
        Assert.Throws<AuditValidationException>(() =>
            RecordFields.Validate(Enumerable.Range(0, RecordFields.MaxFields + 1).ToDictionary(i => $"f{i}", _ => (string?)"x")));
    }

    [Fact]
    public void RecordCopy_MapsAllEntityValues()
    {
        var entity = new ElectronicRecord
        {
            Id = Guid.NewGuid(), RecordType = "T", Title = "Ti", DataJson = "{\"k\":\"v\"}", Version = 4, IsDeleted = true,
            RetainUntilUtc = TestData.T0, CreatedUtc = TestData.T0, CreatedBy = "c", ModifiedUtc = TestData.T0, ModifiedBy = "m",
        };
        var copy = RecordCopy.From(entity);
        Assert.Equal(entity.Id, copy.Id);
        Assert.Equal("v", copy.Fields["k"]);
        Assert.Equal(4, copy.Version);
        Assert.True(copy.IsDeleted);
        Assert.Equal("m", copy.ModifiedBy);
    }
}

public class RetentionPolicyTests
{
    private static RetentionPolicy Policy() => new(new RetentionOptions
    {
        DefaultYears = 5,
        ByRecordType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Clinical"] = 15, ["Bad"] = 0 },
    });

    [Fact]
    public void YearsFor_UsesTypeRuleCaseInsensitively() => Assert.Equal(15, Policy().YearsFor("clinical"));

    [Fact]
    public void YearsFor_FallsBackToDefaultForUnknownOrInvalidRule()
    {
        Assert.Equal(5, Policy().YearsFor("Other"));
        Assert.Equal(5, Policy().YearsFor("Bad"));
    }

    [Fact]
    public void YearsFor_NeverReturnsLessThanOneYear() =>
        Assert.Equal(1, new RetentionPolicy(new RetentionOptions { DefaultYears = 0 }).YearsFor("x"));

    [Fact]
    public void RetainUntil_AddsConfiguredYears() =>
        Assert.Equal(TestData.T0.AddYears(15), Policy().RetainUntil("Clinical", TestData.T0));

    [Fact]
    public void IsExpired_IsInclusiveOfRetainUntilInstant()
    {
        Assert.False(RetentionPolicy.IsExpired(TestData.T0, TestData.T0.AddTicks(-1)));
        Assert.True(RetentionPolicy.IsExpired(TestData.T0, TestData.T0));
    }

    [Fact]
    public void DefaultOptions_AreSevenYears() => Assert.Equal(7, new RetentionOptions().DefaultYears);
}
