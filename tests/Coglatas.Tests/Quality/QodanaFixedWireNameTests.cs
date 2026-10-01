using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Quality;

public sealed class QodanaFixedWireNameTests
{
    [Theory]
    [InlineData(DataClassification.StudentRecordRestricted, "StudentRecordRestricted", 1)]
    [InlineData(TaskExecutionRunStatus.Succeeded, "Succeeded", 3)]
    [InlineData(TenantExportType.AuditPackage, "AuditPackage", 1)]
    public void ExistingWireNamesAndNumericValuesRemainUnambiguous(
        object rawValue,
        string expectedName,
        int expectedNumber)
    {
        var value = Assert.IsAssignableFrom<Enum>(rawValue);
        Assert.Equal(expectedName, value.ToString());
        Assert.Equal(expectedNumber, Convert.ToInt32(value));
        Assert.Single(
            Enum.GetValues(value.GetType()).Cast<object>(),
            candidate => Convert.ToInt32(candidate) == expectedNumber);
    }
}
