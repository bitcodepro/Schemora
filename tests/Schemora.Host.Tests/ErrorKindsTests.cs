using System.Text.Json;
using Schemora.Host;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class ErrorKindsTests
{
    [Fact]
    public void FileAndFormatProblemsAreInputFailuresAndDefectsAreNot()
    {
        Assert.True(ErrorKinds.IsInputFailure(new FileNotFoundException("missing.dacpac")));
        Assert.True(ErrorKinds.IsInputFailure(new UnauthorizedAccessException()));
        Assert.True(ErrorKinds.IsInputFailure(new InvalidDataException("not a zip")));
        Assert.True(ErrorKinds.IsInputFailure(new JsonException()));
        Assert.True(ErrorKinds.IsInputFailure(new InvalidOperationException("wrapper", new IOException("locked"))));
        Assert.False(ErrorKinds.IsInputFailure(new NullReferenceException()));
        Assert.False(ErrorKinds.IsInputFailure(new InvalidOperationException("bug")));
        Assert.False(ErrorKinds.IsInputFailure(new AggregateException(new IOException(), new KeyNotFoundException())));
    }
}
