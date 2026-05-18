using AdroitQMS.CA.Application.Extensions;
using AdroitQMS.CA.Core.AuditAggregate;

namespace AdroitQMS.CA.Application.RebuttalWorkflowAction.Queries.GetClientRebuttalAccountDetail;

// Pure, side-effect-free building blocks for GetClientRebuttalAccountDetailHandler
// (mirrors spNGQMS_GetClientRebuttalAccountDetail). Kept separate from the handler so the
// filtering / counting rules — the part the SP buried in dynamic SQL strings — can be
// unit-tested without a database.
internal static class ClientRebuttalAccountDetailCommon
{
    // tblUploadTypes.UploadTypeID for a Client Feedback upload (the SP's "@UploadTypeID != 2" guard).
    public const int ClientFeedbackUploadTypeID = 2;

    // tblClientAuditRebuttals.ClientAuditRebuttalStatusID for a closed rebuttal.
    public const int RebuttalClosedStatusID = 6;

    // Verticals that take the "current rebuttals" path: IF (@Vertical IN ('FC','PC')).
    private static readonly string[] FcPcVerticals = ["FC", "PC"];

    // Mirrors @pFlag.
    public enum RebuttalQueryMode
    {
        OpenRebuttal = 0,     // default branch of @RebuttalWhereCondition
        UnassignedClosed = 1, // @pFlag = 1
        AssignedClosed = 2,   // @pFlag = 2
        Feedback = 3,         // @pFlag = 3
    }

    public static RebuttalQueryMode ResolveMode(int flag) => flag switch
    {
        1 => RebuttalQueryMode.UnassignedClosed,
        2 => RebuttalQueryMode.AssignedClosed,
        3 => RebuttalQueryMode.Feedback,
        _ => RebuttalQueryMode.OpenRebuttal,
    };

    // Resolves the SP's IF (@Vertical IN ('FC','PC')) ... ELSE branch into a concrete plan:
    //   * which rebuttal table CRH is joined to (current vs. history), and
    //   * which AUA.IsError values are in scope.
    public static RebuttalSourcePlan ResolveSource(string? vertical, RebuttalQueryMode mode)
    {
        var isFcPc = vertical is not null
            && FcPcVerticals.Contains(vertical.Trim(), StringComparer.OrdinalIgnoreCase);

        // ELSE branch: always the history table, and only hard errors (AUA.IsError = 1).
        if (!isFcPc)
        {
            return new RebuttalSourcePlan(UseHistoriesTable: true, ErrorValues: [1]);
        }

        // IF branch: @Feedback picks the table by @pFlag; AUA.IsError IN (0, 1).
        return new RebuttalSourcePlan(
            UseHistoriesTable: mode == RebuttalQueryMode.Feedback,
            ErrorValues: [0, 1]);
    }

    // UP INNER JOIN AUA INNER JOIN CRH, LEFT JOIN CRF — i.e. the population of #tblOpenAccountDetail.
    public static IReadOnlyList<OpenAccountDetailRow> BuildOpenAccountDetail(
        IEnumerable<AuditUploads> auditUploads,
        IEnumerable<AuditUploadAccounts> uploadAccounts,
        IEnumerable<ClientAuditRebuttalRow> rebuttals,
        IEnumerable<ClientAuditRebuttalFeedbackRow> feedbacks,
        IReadOnlyList<string> feedbackColumnNames,
        RebuttalQueryMode mode)
    {
        // CRF is LEFT JOIN-ed on AuditUploadAccountID; index it for O(1) lookups.
        var feedbackByAccount = feedbacks
            .GroupBy(f => f.AuditUploadAccountID)
            .ToDictionary(g => g.Key, g => g.First());

        return (
            from up in auditUploads
            join aua in uploadAccounts
                on up.AuditUploadID equals aua.AuditUploadID
            join crh in rebuttals
                on (long)(aua.AuditUploadAccountID ?? 0) equals crh.AuditUploadAccountID
            select MapOpenAccountDetail(up, aua, crh, feedbackByAccount, feedbackColumnNames, mode)
        ).ToList();
    }

    private static OpenAccountDetailRow MapOpenAccountDetail(
        AuditUploads up,
        AuditUploadAccounts aua,
        ClientAuditRebuttalRow crh,
        IReadOnlyDictionary<long, ClientAuditRebuttalFeedbackRow> feedbackByAccount,
        IReadOnlyList<string> feedbackColumnNames,
        RebuttalQueryMode mode)
    {
        var auditUploadAccountID = (long)(aua.AuditUploadAccountID ?? 0);
        feedbackByAccount.TryGetValue(auditUploadAccountID, out var crf);

        // The client-feedback columns are configured per audit form, so they are read off
        // the AUA row by name (same dynamic-column approach as RebuttalAccountsCommon).
        var columns = feedbackColumnNames.ToDictionary(
            name => name,
            name => GetProperty.GetPropertyValue(aua, name));

        return new OpenAccountDetailRow(
            AuditUploadAccountID: auditUploadAccountID,
            AuditUploadID: up.AuditUploadID,
            AuditFormID: up.AuditFormID,
            Columns: columns,
            AssignedFrom: crh.AssignedFrom,
            AssignedTo: crh.AssignedTo,
            ClientAuditRebuttalStatusID: crh.ClientAuditRebuttalStatusID,
            IsSelected: 0,
            // IsViewed = CASE WHEN @pFlag = 3 THEN ISNULL(CRF.HasRead, 0) ELSE 0 END
            IsViewed: mode == RebuttalQueryMode.Feedback && (crf?.HasRead ?? false) ? 1 : 0,
            FeedbackAssignedTo: crf?.AssignedTo);
    }

    // Mirrors @RebuttalWhereCondition.
    public static bool MatchesRebuttalCondition(OpenAccountDetailRow row, RebuttalQueryMode mode) => mode switch
    {
        RebuttalQueryMode.UnassignedClosed =>
            (row.AssignedFrom ?? 0) == 0
            && (row.ClientAuditRebuttalStatusID ?? 0) == RebuttalClosedStatusID,

        RebuttalQueryMode.AssignedClosed =>
            (row.AssignedFrom ?? 0) > 0
            && (row.ClientAuditRebuttalStatusID ?? 0) == RebuttalClosedStatusID,

        _ =>
            (row.ClientAuditRebuttalStatusID ?? 0) == RebuttalClosedStatusID,
    };

    // Mirrors @RebuttalFeedbackWhereCondition.
    public static bool MatchesFeedbackCondition(OpenAccountDetailRow row, int workedByID) =>
        row.FeedbackAssignedTo == workedByID
        && (row.FeedbackAssignedTo ?? 0) > 0
        && (row.ClientAuditRebuttalStatusID ?? 0) != RebuttalClosedStatusID;

    // Mirrors the four COUNT(...) SELECTs over #tblOpenAccountDetail.
    public static AccountCountDetail BuildCounts(
        IReadOnlyList<OpenAccountDetailRow> rows,
        RebuttalQueryMode mode,
        int workedByID,
        int reportingAuthorityID)
    {
        var agentOpen = rows.Count(r =>
            (r.AssignedFrom ?? 0) == 0
            && (r.AssignedTo ?? 0) == workedByID
            && MatchesRebuttalCondition(r, mode));

        var leadOpen = rows.Count(r =>
            (r.AssignedFrom ?? 0) > 0
            && (r.AssignedTo ?? 0) == workedByID
            && MatchesRebuttalCondition(r, mode));

        var agentPending = rows.Count(r =>
            (r.AssignedFrom ?? 0) == workedByID
            && (r.AssignedTo ?? 0) == reportingAuthorityID);

        var feedback = rows.Count(r =>
            r.AssignedTo == workedByID
            && MatchesFeedbackCondition(r, workedByID));

        return new AccountCountDetail
        {
            AgentOpenAccountCount = agentOpen,
            LeadOpenAccountCount = leadOpen,
            AgentPendingAccountCount = agentPending,
            // CASE @pFlag WHEN 2 THEN 0 ELSE ISNULL(@FeedbackAccountCount, 0) END
            FeedbackAccountCount = mode == RebuttalQueryMode.AssignedClosed ? 0 : feedback,
        };
    }

    // Mirrors the [AccountDetail] sub-select:
    //   WHERE AssignedTo = @pWorkedByID
    //     AND (CASE @pFlag WHEN 3 THEN @RebuttalFeedbackWhereCondition
    //                      ELSE @RebuttalWhereCondition END)
    public static IReadOnlyList<AccountDetailRow> BuildAccountDetail(
        IReadOnlyList<OpenAccountDetailRow> rows,
        RebuttalQueryMode mode,
        int workedByID) =>
        rows
            .Where(r => r.AssignedTo == workedByID
                && (mode == RebuttalQueryMode.Feedback
                    ? MatchesFeedbackCondition(r, workedByID)
                    : MatchesRebuttalCondition(r, mode)))
            .Select(r => new AccountDetailRow
            {
                AuditUploadAccountID = r.AuditUploadAccountID,
                AuditUploadID = r.AuditUploadID,
                AuditFormID = r.AuditFormID,
                Columns = r.Columns,
                IsSelected = r.IsSelected,
                IsViewed = r.IsViewed,
            })
            .ToList();
}

// Resolved form of the SP's IF (@Vertical IN ('FC','PC')) ... ELSE branch.
internal sealed record RebuttalSourcePlan(
    bool UseHistoriesTable,
    IReadOnlyList<int> ErrorValues);
