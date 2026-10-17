using LiveAuction.Application.Realtime;

namespace LiveAuction.UnitTests.Realtime;

public class ConnectionTrackerTests
{
    [Fact]
    public void Join_counts_each_connection_once_per_auction()
    {
        var tracker = new ConnectionTracker();

        Assert.Equal(1, tracker.Join("c1", 10));
        Assert.Equal(1, tracker.Join("c1", 10)); // unirse dos veces no duplica
        Assert.Equal(2, tracker.Join("c2", 10));
        Assert.Equal(1, tracker.Join("c2", 20));
    }

    [Fact]
    public void Leave_decrements_and_ignores_unknown()
    {
        var tracker = new ConnectionTracker();
        tracker.Join("c1", 10);
        tracker.Join("c2", 10);

        Assert.Equal(1, tracker.Leave("c1", 10));
        Assert.Equal(1, tracker.Leave("c1", 10));      // ya había salido
        Assert.Equal(1, tracker.Leave("nadie", 10));
        Assert.Equal(0, tracker.Leave("c2", 10));
    }

    [Fact]
    public void RemoveConnection_leaves_every_auction_and_reports_new_counts()
    {
        var tracker = new ConnectionTracker();
        tracker.Join("c1", 10);
        tracker.Join("c1", 20);
        tracker.Join("c2", 10);

        var changes = tracker.RemoveConnection("c1").OrderBy(c => c.AuctionId).ToList();

        Assert.Equal([(10, 1), (20, 0)], changes);
        Assert.Equal(1, tracker.ViewerCount(10));
        Assert.Equal(0, tracker.ViewerCount(20));
        Assert.Empty(tracker.RemoveConnection("c1")); // segunda vez: nada
    }

    [Fact]
    public async Task Is_consistent_under_concurrent_joins_and_disconnects()
    {
        var tracker = new ConnectionTracker();

        await Task.WhenAll(Enumerable.Range(0, 500).Select(i => Task.Run(() =>
        {
            tracker.Join($"c{i}", 1);
            if (i % 2 == 0)
                tracker.RemoveConnection($"c{i}");
        })));

        Assert.Equal(250, tracker.ViewerCount(1));
    }
}
