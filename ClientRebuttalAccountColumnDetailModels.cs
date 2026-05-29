namespace AdroitQMS.CA.Application.RebuttalWorkflowAction.Queries.GetClientRebuttalAccountColumnDetail;

// Request parameters — mirror the @p* arguments of spNGQMS_GetClientRebuttalAccountColumnDetail.
public sealed record GetClientRebuttalAccountColumnDetailQuery(
    int AuditFormID,
    int AuditUploadAccountID,
    string QMSSchemaName);

// Top-level payload — mirrors the JSON structure returned by the SP.
public sealed class ClientRebuttalAccountColumnDetailResult
{
    public IReadOnlyList<AccountColumn> AccountColumns { get; init; } = [];

    public string QualityScore { get; init; } = string.Empty;

    // "1" = has error (QualityScore < 100), "2" = no error (QualityScore = 100).
    public string IsError { get; init; } = "1";

    public string TLComment { get; init; } = string.Empty;

    public string AgentComment { get; init; } = string.Empty;
}

// One entry in AccountColumns — the display label and the actual value from tblAuditUploadAccounts.
public sealed class AccountColumn
{
    public string UploadColumnDisplayName { get; init; } = string.Empty;

    public object? UploadColumnValue { get; init; }
}

// Projection over tblClientFeedbackColumns + tblUploadColumns.
// Ordered by DisplayOrder; filtered to ShowInFeedbackScreen = 1.
internal sealed record FeedbackColumnInfo(
    string UploadColumnDisplayName,
    string UploadColumnName);
