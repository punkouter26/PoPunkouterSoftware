using PoPunkouterSoftware.Domain.Azure;

namespace PoPunkouterSoftware.Tests.Unit;

// SOLID: Single Responsibility — each test class focuses on one behaviour of Result<T>.
// Target: Domain layer logic — no I/O, no external dependencies.

public class Result_SuccessTests
{
    [Fact]
    public void Success_SetsIsSuccessTrue()
    {
        var result = Result<string>.Success("hello");
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Success_SetsValue()
    {
        var result = Result<int>.Success(42);
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Success_ErrorIsNull()
    {
        var result = Result<string>.Success("ok");
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Success_ExceptionIsNull()
    {
        var result = Result<string>.Success("ok");
        result.Exception.Should().BeNull();
    }

    [Fact]
    public void Success_AcceptsNullValue()
    {
        var result = Result<string?>.Success(null);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }
}

public class Result_FailureTests
{
    [Fact]
    public void Failure_SetsIsSuccessFalse()
    {
        var result = Result<string>.Failure("something went wrong");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Failure_SetsErrorMessage()
    {
        const string errorMsg = "storage unavailable";
        var result = Result<string>.Failure(errorMsg);
        result.Error.Should().Be(errorMsg);
    }

    [Fact]
    public void Failure_ValueIsDefault()
    {
        var result = Result<int>.Failure("err");
        result.Value.Should().Be(default);
    }

    [Fact]
    public void Failure_WithException_SetsException()
    {
        var ex = new InvalidOperationException("boom");
        var result = Result<string>.Failure("err", ex);
        result.Exception.Should().BeSameAs(ex);
    }

    [Fact]
    public void Failure_WithoutException_ExceptionIsNull()
    {
        var result = Result<string>.Failure("err");
        result.Exception.Should().BeNull();
    }
}

public class Result_BehaviourTests
{
    [Fact]
    public void Success_And_Failure_AreDistinct()
    {
        var ok = Result<bool>.Success(true);
        var err = Result<bool>.Failure("nope");

        ok.IsSuccess.Should().BeTrue();
        err.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Success_WithReferenceType_ValueIsSameInstance()
    {
        var list = new List<string> { "a", "b" };
        var result = Result<List<string>>.Success(list);
        result.Value.Should().BeSameAs(list);
    }

    [Theory]
    [InlineData("short error")]
    [InlineData("a very long error message describing a complex failure scenario")]
    [InlineData("")]
    public void Failure_PreservesErrorMessageVerbatim(string error)
    {
        var result = Result<object>.Failure(error);
        result.Error.Should().Be(error);
    }
}
