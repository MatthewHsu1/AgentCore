using System.Text.Json.Nodes;
using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// Section 8.3: a <c>writer: tool</c> path resolves against a tool result two ways - the compact
/// dotted form (<c>lookup_order.status</c>) and the RFC 6901 pointer form (<c>/lookup_order/status</c>).
/// <see cref="ToolResultPath"/> is <see langword="internal"/>, reachable here only because this test
/// assembly holds an <c>InternalsVisibleTo</c>.
/// </summary>
/// <remarks>
/// These tests pin every edge case the hand-rolled resolver handled, before and after it is backed by
/// <c>Json.Pointer.JsonPointer</c>: the empty path, the leading slash, array indexing, a segment
/// containing <c>~</c> or <c>/</c>, a missing path, and a path that walks into a non-object. A few
/// extra cases (multi-digit leading-zero array indices, the RFC 6901 <c>-</c> token, and a malformed
/// escape sequence) are pinned too, because they surfaced while reading the replacement library's
/// source and are exactly the kind of thing a naive dotted-to-pointer translation gets wrong.
/// </remarks>
public sealed class ToolResultPathTests
{
    private static readonly JsonNode Result = JsonNode.Parse(
        """
        {
            "status": "shipped",
            "totals": { "grand": 129.5 },
            "items": ["a", "b", "c"],
            "a~b": "tilde-key",
            "a/b": "slash-key",
            "": "empty-key",
            "a~2b": "malformed-escape-key"
        }
        """)!;

    // ---------------------------------------------------------------------------------------------
    // The empty path: resolves to the whole result, unchanged.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void EmptyPath_ResolvesToTheWholeResult()
        => Assert.Same(Result, ToolResultPath.Resolve(Result, ""));

    // ---------------------------------------------------------------------------------------------
    // The dotted compact form.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void DottedPath_ResolvesATopLevelProperty()
        => Assert.Equal("shipped", ToolResultPath.Resolve(Result, "status")!.GetValue<string>());

    [Fact]
    public void DottedPath_ResolvesANestedProperty()
        => Assert.Equal(129.5, ToolResultPath.Resolve(Result, "totals.grand")!.GetValue<double>());

    [Fact]
    public void DottedPath_IndexesAnArray()
        => Assert.Equal("b", ToolResultPath.Resolve(Result, "items.1")!.GetValue<string>());

    [Fact]
    public void DottedPath_ASegmentContainingATilde_IsTakenLiterally()
        => Assert.Equal("tilde-key", ToolResultPath.Resolve(Result, "a~b")!.GetValue<string>());

    [Fact]
    public void DottedPath_ASegmentContainingASlash_IsTakenLiterally()
        => Assert.Equal("slash-key", ToolResultPath.Resolve(Result, "a/b")!.GetValue<string>());

    // ---------------------------------------------------------------------------------------------
    // The RFC 6901 pointer form: a leading slash.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void PointerPath_ResolvesATopLevelProperty()
        => Assert.Equal("shipped", ToolResultPath.Resolve(Result, "/status")!.GetValue<string>());

    [Fact]
    public void PointerPath_ResolvesANestedProperty()
        => Assert.Equal(129.5, ToolResultPath.Resolve(Result, "/totals/grand")!.GetValue<double>());

    [Fact]
    public void PointerPath_IndexesAnArray()
        => Assert.Equal("c", ToolResultPath.Resolve(Result, "/items/2")!.GetValue<string>());

    [Fact]
    public void PointerPath_UnescapesTilde0ToALiteralTilde()
        => Assert.Equal("tilde-key", ToolResultPath.Resolve(Result, "/a~0b")!.GetValue<string>());

    [Fact]
    public void PointerPath_UnescapesTilde1ToALiteralSlash()
        => Assert.Equal("slash-key", ToolResultPath.Resolve(Result, "/a~1b")!.GetValue<string>());

    [Fact]
    public void PointerPath_ASingleSlash_ResolvesTheEmptyStringKey()
        => Assert.Equal("empty-key", ToolResultPath.Resolve(Result, "/")!.GetValue<string>());

    // ---------------------------------------------------------------------------------------------
    // What reaches nothing.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void DottedPath_AMissingProperty_ResolvesToNull()
        => Assert.Null(ToolResultPath.Resolve(Result, "nope"));

    [Fact]
    public void PointerPath_AMissingProperty_ResolvesToNull()
        => Assert.Null(ToolResultPath.Resolve(Result, "/nope"));

    [Fact]
    public void APathIntoANonObject_ResolvesToNull()
        // "status" is a string; stepping past it reaches nothing.
        => Assert.Null(ToolResultPath.Resolve(Result, "status.nested"));

    [Fact]
    public void ANonNumericSegmentAgainstAnArray_ResolvesToNull()
        => Assert.Null(ToolResultPath.Resolve(Result, "items.notanumber"));

    [Fact]
    public void AnOutOfRangeArrayIndex_ResolvesToNull()
        => Assert.Null(ToolResultPath.Resolve(Result, "items.99"));

    [Fact]
    public void ANullResult_ResolvesToNullWhateverThePathIs()
        => Assert.Null(ToolResultPath.Resolve(null, "status"));

    // ---------------------------------------------------------------------------------------------
    // Extra edge cases found while reading the replacement library's own evaluator: RFC 6901 is
    // stricter about array indices than the hand-rolled int.TryParse ever was.
    //
    // Fix round 1 (owner ruling): three divergences from the pre-swap resolver surfaced here. Two
    // were accepted as a deliberate tightening - invalid input now fails instead of silently
    // resolving - and are pinned below to the *new* behaviour, with the old behaviour named in the
    // comment. The third (the '-' token) was rejected, because it was the one case where invalid
    // input started silently *succeeding*, which could put a new, undecided value into a state
    // slot; ToolResultPath now guards it explicitly, so that test still pins the original `null`.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void PointerPath_ALeadingZeroMultiDigitArrayIndex_ResolvesToNull()
    {
        // Owner ruling, fix round 1: ACCEPTED as a deliberate tightening. The pre-swap hand-rolled
        // resolver used int.TryParse("01"), which happily parses to 1, so "/items/01" used to
        // resolve to the element at index 1 - a document relying on that was relying on a bug.
        // Json.Pointer.JsonPointer enforces RFC 6901's canonical-index rule (no leading zero except
        // the literal index "0") and rejects the whole pointer, so this now resolves to nothing.
        Assert.Null(ToolResultPath.Resolve(Result, "/items/01"));
    }

    [Fact]
    public void PointerPath_TheDashToken_IsRejectedAndResolvesToNull()
    {
        // Owner ruling, fix round 1: REJECTED - the old behaviour is preserved deliberately, not
        // merely pinned. RFC 6901 defines '-' as "the (nonexistent) member after the last array
        // element", and Json.Pointer.JsonPointer's TryEvaluate implements that by resolving it to
        // the *existing* last element. Unlike the other two divergences, that would make a
        // previously-invalid path start silently succeeding - turning "no state write" into "write
        // the array's last element" as a side effect of the library swap rather than a document
        // decision. ToolResultPath now rejects a '-' segment before evaluating, so this still
        // resolves to null exactly as the pre-swap resolver did (int.TryParse("-") failed there).
        Assert.Null(ToolResultPath.Resolve(Result, "/items/-"));
    }

    [Fact]
    public void PointerPath_ANegativeArrayIndex_ResolvesToNull()
        => Assert.Null(ToolResultPath.Resolve(Result, "/items/-1"));

    [Fact]
    public void PointerPath_AMalformedEscapeSequence_ResolvesToNull()
    {
        // Owner ruling, fix round 1: ACCEPTED as a deliberate tightening. "~2" is not a valid RFC
        // 6901 escape (only ~0 and ~1 are defined). The pre-swap hand-rolled resolver's Replace
        // calls simply didn't match "~2", so the segment survived as the literal text "a~2b" and
        // resolved whatever property happened to have that exact (unusual) name. Json.Pointer.
        // JsonPointer validates every escape up front and rejects the whole pointer as malformed, so
        // this now resolves to nothing even though the document does hold a property named "a~2b".
        Assert.Null(ToolResultPath.Resolve(Result, "/a~2b"));
    }
}
