using FluentAssertions;
using TodoSync.Api.Sync.Contracts;
using TodoSync.Api.Sync.Core;
using Xunit;

namespace TodoSync.Tests.Sync;

public sealed class SyncCursorCodecTests
{
    private readonly SyncCursorCodec _codec = new();

    [Fact]
    public void EncodeDecode_ShouldPreserveStableWindow()
    {
        const long after = 120;
        const long upperBound = 300;
        var upperBoundChangeId = Guid.CreateVersion7();

        var encoded = _codec.Encode("default", ["todo"], after, upperBound, upperBoundChangeId);
        var decoded = _codec.Decode(encoded, "default", ["todo"]);

        decoded.AfterSequence.Should().Be(after);
        decoded.UpperBoundSequence.Should().Be(upperBound);
        decoded.UpperBoundChangeId.Should().Be(upperBoundChangeId);
        decoded.Scope.Should().Be("todo");
    }

    [Fact]
    public void Decode_WithDifferentScope_ShouldFail()
    {
        var encoded = _codec.Encode("default", ["todo"], 10, 20, Guid.CreateVersion7());

        var action = () => _codec.Decode(encoded, "default", ["note", "todo"]);

        action.Should().Throw<SyncValidationException>()
            .Which.Code.Should().Be("SYNC_CURSOR_INVALID");
    }

    [Fact]
    public void Decode_WithMalformedCursor_ShouldFail()
    {
        var action = () => _codec.Decode("not-a-cursor", "default", ["todo"]);

        action.Should().Throw<SyncValidationException>()
            .Which.Code.Should().Be("SYNC_CURSOR_INVALID");
    }
}
