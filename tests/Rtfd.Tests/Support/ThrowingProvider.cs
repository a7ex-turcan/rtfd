using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Providers;

namespace Rtfd.Tests.Support;

/// <summary>A provider whose every call fails, for tests that need only one or two of them: override those.</summary>
public abstract class ThrowingProvider : IProvider
{
    public virtual ReviewPosting Posting => ReviewPosting.Atomic;

    public virtual Task<string> WhoAmIAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<CiState> GetCiStateAsync(int number, string sha, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<FetchSource> FetchSourceAsync(PullRequest pullRequest, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<PostedReview> PostReviewAsync(ReviewPost post, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<PostedComment> StartThreadAsync(ThreadPost post, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task SetVoteAsync(int number, Verdict verdict, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<PostedReview?> GetReviewAsync(int number, string reviewId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<PostedReview>> ListReviewsAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<ReviewComment> ReplyAsync(int number, string commentId, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task ResolveThreadAsync(int number, string threadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
