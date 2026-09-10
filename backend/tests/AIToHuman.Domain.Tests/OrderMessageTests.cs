using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Domain.Tests;

public sealed class OrderMessageTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
    private readonly Guid _order = Guid.NewGuid();
    private readonly Guid _sender = Guid.NewGuid();

    [Fact]
    public void Message_trims_content_and_starts_unread()
    {
        var message = new OrderMessage(_order, _sender, "  下午三点前送到  ", _now);

        Assert.Equal("下午三点前送到", message.Content);
        Assert.Equal(_order, message.OrderId);
        Assert.Equal(_sender, message.SenderId);
        Assert.Null(message.ReadAt);
        Assert.False(message.IsRead);
    }

    [Fact]
    public void Unread_is_relative_to_the_viewer()
    {
        var message = new OrderMessage(_order, _sender, "好的", _now);
        var other = Guid.NewGuid();

        Assert.False(message.IsUnreadFor(_sender));
        Assert.True(message.IsUnreadFor(other));

        message.MarkRead(_now);

        Assert.False(message.IsUnreadFor(other));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Message_rejects_empty_content(string content)
    {
        Assert.Throws<DomainException>(() => new OrderMessage(_order, _sender, content, _now));
    }

    [Fact]
    public void Message_rejects_over_long_content()
    {
        var content = new string('好', OrderMessage.MaxContentLength + 1);

        Assert.Throws<DomainException>(() => new OrderMessage(_order, _sender, content, _now));
    }

    [Fact]
    public void Message_rejects_missing_order_or_sender()
    {
        Assert.Throws<DomainException>(() => new OrderMessage(Guid.Empty, _sender, "好的", _now));
        Assert.Throws<DomainException>(() => new OrderMessage(_order, Guid.Empty, "好的", _now));
    }

    [Fact]
    public void Mark_read_is_idempotent()
    {
        var message = new OrderMessage(_order, _sender, "好的", _now);

        message.MarkRead(_now.AddMinutes(1));
        message.MarkRead(_now.AddMinutes(9));

        Assert.Equal(_now.AddMinutes(1), message.ReadAt);
    }

    [Fact]
    public void Rehydrate_normalizes_offsets()
    {
        var localOffset = TimeSpan.FromHours(8);
        var createdAt = new DateTimeOffset(2026, 9, 10, 16, 0, 0, localOffset);

        var message = OrderMessage.Rehydrate(Guid.NewGuid(), _order, _sender, "好的", createdAt, null);

        Assert.Equal(TimeSpan.Zero, message.CreatedAt.Offset);
        Assert.Equal(createdAt.ToUniversalTime(), message.CreatedAt);
    }
}
