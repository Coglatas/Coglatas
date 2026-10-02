using Coglatas.Application.Notifications;
using Coglatas.Application.Realtime;

namespace Coglatas.Tests.Quality;

public sealed class QodanaFixedExceptionContractTests
{
    [Theory]
    [InlineData(typeof(TaskDeadlineDigestRetryablePersistenceConflictException),
        "Task deadline digest persistence conflicted with concurrent state.")]
    [InlineData(typeof(RequiredOutboxStagingException),
        "A required transactional Outbox event could not be staged.")]
    public void PublicParameterlessConstructionPreservesExceptionContract(
        Type exceptionType,
        string expectedMessage)
    {
        var constructor = Assert.Single(exceptionType.GetConstructors());
        Assert.Empty(constructor.GetParameters());
        Assert.Equal(typeof(Exception), exceptionType.BaseType);
        var exception = Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType));
        Assert.Equal(exceptionType, exception.GetType());
        Assert.Equal(expectedMessage, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Equal(new Exception(expectedMessage).HResult, exception.HResult);
    }
}
