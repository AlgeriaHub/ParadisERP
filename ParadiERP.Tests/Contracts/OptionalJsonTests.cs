using System.Text.Json;
using ParadiERP.Application.Contracts;

namespace ParadiERP.Tests.Contracts;

/// <summary>
/// The three states of <c>UpdateUserRequest.BranchId</c> as a body actually arrives: absent,
/// null, and a value. The whole point of <see cref="Optional{T}"/> is that the first two are
/// different, and System.Text.Json only tells them apart when the converter opts into null —
/// a regression there would silently turn every role change on the users tab into an unbinding.
/// </summary>
public class OptionalJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_body_that_omits_the_field_leaves_it_unset()
    {
        var r = JsonSerializer.Deserialize<UpdateUserRequest>(
            """{"fullName":"Amine","role":"Cashier","isActive":true}""", Web)!;
        Assert.False(r.BranchId.IsSet);
    }

    [Fact]
    public void A_body_that_sends_null_sets_it_to_any_terminal()
    {
        var r = JsonSerializer.Deserialize<UpdateUserRequest>(
            """{"fullName":"Amine","role":"Cashier","isActive":true,"branchId":null}""", Web)!;
        Assert.True(r.BranchId.IsSet);
        Assert.Null(r.BranchId.Value);
    }

    [Fact]
    public void A_body_that_sends_a_guid_binds()
    {
        var id = Guid.NewGuid();
        var r = JsonSerializer.Deserialize<UpdateUserRequest>(
            $$"""{"fullName":"Amine","role":"Cashier","isActive":true,"branchId":"{{id}}"}""", Web)!;
        Assert.True(r.BranchId.IsSet);
        Assert.Equal(id, r.BranchId.Value);
    }

    [Fact]
    public void It_serialises_as_the_plain_value_so_the_wire_never_sees_the_wrapper()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"\"{id}\"", JsonSerializer.Serialize(Optional<Guid?>.Of(id), Web));
        Assert.Equal("null", JsonSerializer.Serialize(Optional<Guid?>.Of(null), Web));
        Assert.Equal("null", JsonSerializer.Serialize(Optional<Guid?>.Unset, Web));
    }
}
