namespace AdroitQMS.CA.Application.RebuttalWorkflowAction.Queries.GetClientRebuttalAccountDetail;

// Request parameters — mirror the @p* arguments of spNGQMS_GetClientRebuttalAccountDetail.
public sealed record GetClientRebuttalAccountDetailQuery(
    int AuditFormID,
    int WorkedByID,
    string QMSSchemaName,
    int Flag);

// Top-level payload — mirrors the [AuditAccountDetails] JSON node returned by the SP.
public sealed class ClientRebuttalAccountDetailResult
{
    public AccountCountDetail AccountCountDetail { get; init; } = new();

    public IReadOnlyList<AccountDetailRow> AccountDetail { get; init; } = [];
}

// Mirrors the [AccountCountDetail] JSON node.
public sealed class AccountCountDetail
{
    public int AgentOpenAccountCount { get; init; }

    public int LeadOpenAccountCount { get; init; }

    public int AgentPendingAccountCount { get; init; }

    public int FeedbackAccountCount { get; init; }

    // Static routes — identical to the string literals in the SP.
    public string AgentURL { get; init; } = "/rebut/clientfeedbackrebuttalagent";

    public string LeadURL { get; init; } = "/rebut/clientfeedbackrebuttalteamlead";
}

// Mirrors one row of the [AccountDetail] JSON node.
// The client-feedback columns are configured per audit form (tblClientFeedbackColumns),
// so they are carried as a name/value map instead of fixed properties. A JsonConverter
// should flatten Columns onto the row to reproduce the SP's exact (flat) output shape.
public sealed class AccountDetailRow
{
    public long AuditUploadAccountID { get; init; }

    public int AuditUploadID { get; init; }

    public int AuditFormID { get; init; }

    public IReadOnlyDictionary<string, object?> Columns { get; init; } =
        new Dictionary<string, object?>();

    public int IsSelected { get; init; }

    public int IsViewed { get; init; }
}

// In-memory equivalent of the #tblOpenAccountDetail temp table.
internal sealed record OpenAccountDetailRow(
    long AuditUploadAccountID,
    int AuditUploadID,
    int AuditFormID,
    IReadOnlyDictionary<string, object?> Columns,
    int? AssignedFrom,
    int? AssignedTo,
    int? ClientAuditRebuttalStatusID,
    int IsSelected,
    int IsViewed,
    int? FeedbackAssignedTo);

// Unified shape over tblClientAuditRebuttals and tblClientAuditRebuttalHistories
// (both aliased CRH in the SP's dynamic SQL).
internal sealed record ClientAuditRebuttalRow(
    long AuditUploadAccountID,
    int? AssignedFrom,
    int? AssignedTo,
    int? ClientAuditRebuttalStatusID);

// Projection over tblClientAuditRebuttalFeedbacks (aliased CRF in the SP).
internal sealed record ClientAuditRebuttalFeedbackRow(
    long AuditUploadAccountID,
    int? AssignedTo,
    bool HasRead);
