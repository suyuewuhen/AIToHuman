using AIToHuman.Domain.Common;
using AIToHuman.Domain.Notifications;

namespace AIToHuman.Domain.Tests;

public sealed class NotificationTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _event = Guid.NewGuid();

    [Fact]
    public void Notification_starts_unread_and_undispatched()
    {
        var notification = CreateNotification();

        Assert.Equal(_user, notification.UserId);
        Assert.Equal(_event, notification.EventId);
        Assert.Equal("order.created", notification.Type);
        Assert.Equal(1, notification.Version);
        Assert.Null(notification.ReadAt);
        Assert.Null(notification.DispatchedAt);
        Assert.False(notification.IsRead);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("event")]
    [InlineData("type")]
    [InlineData("empty-type")]
    [InlineData("long-type")]
    [InlineData("version")]
    [InlineData("payload")]
    [InlineData("long-payload")]
    public void Notification_validates_its_identity_and_payload(string scenario)
    {
        var userId = scenario == "user" ? Guid.Empty : _user;
        var eventId = scenario == "event" ? Guid.Empty : _event;
        var type = scenario switch
        {
            "type" => " ",
            "empty-type" => string.Empty,
            "long-type" => new string('t', 81),
            _ => "order.created"
        };
        var version = scenario == "version" ? 0 : 1;
        var payload = scenario switch
        {
            "payload" => " ",
            "long-payload" => new string('p', 8001),
            _ => "{}"
        };

        Assert.Throws<DomainException>(() => new Notification(userId, eventId, type, version, payload, _now));
    }

    [Fact]
    public void Mark_dispatched_and_mark_read_are_idempotent()
    {
        var notification = CreateNotification();

        notification.MarkDispatched(_now);
        notification.MarkRead(_now.AddMinutes(1));
        notification.MarkDispatched(_now.AddMinutes(5));
        notification.MarkRead(_now.AddMinutes(9));

        Assert.Equal(_now, notification.DispatchedAt);
        Assert.Equal(_now.AddMinutes(1), notification.ReadAt);
    }

    [Fact]
    public void Rehydrate_restores_timestamps_and_normalizes_offsets()
    {
        var localOffset = TimeSpan.FromHours(8);
        var createdAt = new DateTimeOffset(2026, 9, 10, 16, 0, 0, localOffset);
        var readAt = createdAt.AddMinutes(10);

        var notification = Notification.Rehydrate(Guid.NewGuid(), _user, _event, "order.created", 1, "{}", createdAt, null, readAt);

        Assert.Equal(TimeSpan.Zero, notification.CreatedAt.Offset);
        Assert.Equal(createdAt.ToUniversalTime(), notification.CreatedAt);
        Assert.Equal(readAt.ToUniversalTime(), notification.ReadAt);
        Assert.Null(notification.DispatchedAt);
    }

    private Notification CreateNotification() => new(_user, _event, "order.created", 1, "{}", _now);
}
