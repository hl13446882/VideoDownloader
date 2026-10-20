using VideoDownloader.Core.Detection;

namespace VideoDownloader.Core.Tests;

public sealed class FeedDetectionDisplayStateTests
{
    private const string Page = "https://www.douyin.com/?recommend=1";

    [Fact]
    public void SwitchingToLiveWithoutNewAddress_BlocksAllPublicationsUntilANewSession()
    {
        var state = new FeedDetectionDisplayState();
        var oldSession = Guid.NewGuid();
        var queuedEpoch = state.Epoch;
        state.Suspend(Page, "content:old", "old");
        Assert.False(state.CanPublish(queuedEpoch, oldSession, oldSession));
        Assert.False(state.CanPublish(state.Epoch, oldSession, oldSession));
        Assert.False(state.CanBeginSession(Page, null, null));
    }

    [Fact]
    public void OldIdentityOrQueryJitter_CannotRestartTheRetiredWork()
    {
        var state = new FeedDetectionDisplayState();
        state.Suspend(Page, "content:old", "old");
        Assert.False(state.CanBeginSession(Page, "content:old", "old"));
        Assert.False(state.CanBeginSession(Page + "&modal_id=old", "content:old", "other-CDN-for-old"));
        Assert.True(state.CanBeginSession(Page, "content:new", "new"));
    }

    [Fact]
    public void NewSessionAcceptsItsResults_AndRejectsQueuedOrLateOldResults()
    {
        var state = new FeedDetectionDisplayState();
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        var oldQueuedEpoch = state.Epoch;
        state.Suspend(Page, "old", "old");
        var waitingEpoch = state.Epoch;
        state.BeginSession();
        Assert.False(state.CanPublish(oldQueuedEpoch, newSession, newSession));
        Assert.False(state.CanPublish(waitingEpoch, newSession, newSession));
        Assert.False(state.CanPublish(state.Epoch, oldSession, newSession));
        Assert.False(state.CanPublish(state.Epoch, Guid.Empty, newSession));
        Assert.True(state.CanPublish(state.Epoch, newSession, newSession));
    }

    [Fact]
    public void ManualProbeCanExplicitlyReenterTheSameWork()
    {
        var state = new FeedDetectionDisplayState();
        state.Suspend(Page, "old", "old");
        state.BeginSession();
        Assert.True(state.CanBeginSession(Page, "old", "old"));
    }

    [Fact]
    public void GenericSiteWithNoContentId_RequiresNewMediaOrNewDocument()
    {
        var state = new FeedDetectionDisplayState();
        state.Suspend("https://example.com/feed", null, "https://cdn.example.com/old.mp4");
        Assert.False(state.CanBeginSession("https://example.com/feed", null, null));
        Assert.False(state.CanBeginSession("https://example.com/feed", null, "https://cdn.example.com/old.mp4"));
        Assert.True(state.CanBeginSession("https://example.com/feed", null, "https://cdn.example.com/new.mp4"));
        Assert.True(state.CanBeginSession("https://example.com/new-page", null, null));
    }
}
