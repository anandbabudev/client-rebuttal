using AdroitQMS.CA.Application.Extensions;
using AdroitQMS.CA.Core.AuditAggregate;
using AdroitQMS.CA.Core.AuditAggregate.Specifications;
using AdroitQMS.CA.SharedKernel.Interfaces;
using AdroitQMS.SharedKernel.Interfaces;
using Ardalis.Result;
using Immediate.Handlers.Shared;
using OpenTelemetry.Trace;

namespace AdroitQMS.CA.Application.RebuttalWorkflowAction.Queries.GetClientRebuttalAccountColumnDetail;

// C# port of spNGQMS_GetClientRebuttalAccountColumnDetail.
//
// The SP builds dynamic SQL to assemble a JSON response for a single audit account:
//   * per-column display names and values (configured in tblClientFeedbackColumns),
//   * QualityScore and the derived IsError flag from tblAuditUploadAccounts,
//   * AgentComment (tblClientAuditRebuttalHistories, StatusFromID = 5), and
//   * TLComment (tblClientAuditRebuttals).
// Here those become explicit, sequential async fetches assembled in memory via
// ClientRebuttalAccountColumnDetailCommon.
[Handler]
public static partial class GetClientRebuttalAccountColumnDetailHandler
{
    private static async ValueTask<Result<ClientRebuttalAccountColumnDetailResult>> HandleAsync(
        GetClientRebuttalAccountColumnDetailQuery request,
        Tracer trace,
        INexgenQmsReadRepository nexgenQmsReadRepository,
        IQMSClientDbRepositoryFactory qmsClientDbRepositoryFactory,
        CancellationToken cancellationToken)
    {
        using var span = trace.StartActiveSpan(nameof(GetClientRebuttalAccountColumnDetailHandler));

        try
        {
            // --- feedback column definitions (ShowInFeedbackScreen = 1, ordered by DisplayOrder) --
            var feedbackColumns = await FetchFeedbackColumnsAsync(
                request.AuditFormID, nexgenQmsReadRepository, cancellationToken);

            // --- per-account column values + QualityScore from <schema>.tblAuditUploadAccounts ----
            var auaValues = await FetchAuditUploadAccountValuesAsync(
                request.QMSSchemaName, request.AuditUploadAccountID,
                feedbackColumns, qmsClientDbRepositoryFactory, cancellationToken);

            var qualityScore = auaValues.TryGetValue("QualityScore", out var qs) ? qs as int? : null;

            // --- rebuttal comments from the client QMS schema ------------------------------------
            var agentComment = await FetchAgentCommentAsync(
                request.QMSSchemaName, request.AuditUploadAccountID,
                qmsClientDbRepositoryFactory, cancellationToken);

            var tlComment = await FetchTLCommentAsync(
                request.QMSSchemaName, request.AuditUploadAccountID,
                qmsClientDbRepositoryFactory, cancellationToken);

            var result = new ClientRebuttalAccountColumnDetailResult
            {
                AccountColumns = ClientRebuttalAccountColumnDetailCommon.BuildAccountColumns(
                    feedbackColumns, auaValues),
                QualityScore = qualityScore?.ToString() ?? string.Empty,
                IsError = ClientRebuttalAccountColumnDetailCommon.ResolveIsError(qualityScore),
                TLComment = tlComment,
                AgentComment = agentComment,
            };

            return Result.Success(result);
        }
        catch (Exception ex)
        {
            span.SetStatus(Status.Error.WithDescription(ex.Message));
            return Result.Error(ex.Message);
        }
    }

    // SELECT CFC.UploadColumnDisplayName, UC.UploadColumnName
    // FROM tblPGAuditForms -> tblPGUploadTemplates -> tblClientFeedbackColumns -> tblUploadColumns
    // WHERE AuditFormID = @pAuditFormID AND CFC.ShowInFeedbackScreen = 1
    // ORDER BY CFC.DisplayOrder
    //
    // NOTE: requires GetClientFeedbackColumnsWithDisplayByAuditFormSpec — extends the existing
    // GetClientFeedbackColumnsByAuditFormSpec by also selecting UploadColumnDisplayName,
    // adding the ShowInFeedbackScreen = 1 predicate, and ordering by DisplayOrder.
    private static async Task<IReadOnlyList<FeedbackColumnInfo>> FetchFeedbackColumnsAsync(
        int auditFormID,
        INexgenQmsReadRepository nexgenQmsReadRepository,
        CancellationToken cancellationToken)
    {
        var rows = await nexgenQmsReadRepository.ListAsync(
            new GetClientFeedbackColumnsWithDisplayByAuditFormSpec(auditFormID), cancellationToken);
        return rows
            .Select(r => new FeedbackColumnInfo(r.UploadColumnDisplayName, r.UploadColumnName))
            .ToList();
    }

    // SELECT QualityScore, <feedbackColumnNames>
    // FROM <schema>.tblAuditUploadAccounts
    // WHERE AuditUploadAccountID = @pAuditUploadAccountID
    //
    // Returns a column-name → value map used both to populate AccountColumns and to
    // derive QualityScore / IsError (same GetProperty reflection approach as
    // GetClientRebuttalAccountDetailHandler.FetchUploadAccountsAsync).
    //
    // NOTE: requires GetAuditUploadAccountByIdSpec(int auditUploadAccountID,
    // IEnumerable<string> selectColumns).
    private static async Task<IReadOnlyDictionary<string, object?>> FetchAuditUploadAccountValuesAsync(
        string qmsSchemaName,
        int auditUploadAccountID,
        IReadOnlyList<FeedbackColumnInfo> feedbackColumns,
        IQMSClientDbRepositoryFactory qmsClientDbRepositoryFactory,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> selectColumns = feedbackColumns
            .Select(c => c.UploadColumnName)
            .Append("QualityScore")
            .ToList();

        using var repo = await qmsClientDbRepositoryFactory
            .CreateReadAsync<AuditUploadAccounts>(qmsSchemaName);
        var rows = await repo.ListAsync(
            new GetAuditUploadAccountByIdSpec(auditUploadAccountID, selectColumns), cancellationToken);

        var row = rows.FirstOrDefault();
        if (row is null)
            return new Dictionary<string, object?>();

        return selectColumns.ToDictionary(
            name => name,
            name => GetProperty.GetPropertyValue(row, name));
    }

    // SELECT ISNULL(RebuttalComments, '') FROM tblClientAuditRebuttalHistories
    // WHERE AuditUploadAccountID = @pAuditUploadAccountID
    //   AND ISNULL(ClientAuditRebuttalStatusFromID, 0) = 5
    //
    // NOTE: requires GetAgentRebuttalCommentSpec(int auditUploadAccountID).
    // The ClientAuditRebuttalHistories entity must expose RebuttalComments and
    // ClientAuditRebuttalStatusFromID (distinct from ClientAuditRebuttalStatusID already
    // mapped in GetClientRebuttalAccountDetailHandler).
    private static async Task<string> FetchAgentCommentAsync(
        string qmsSchemaName,
        int auditUploadAccountID,
        IQMSClientDbRepositoryFactory qmsClientDbRepositoryFactory,
        CancellationToken cancellationToken)
    {
        using var repo = await qmsClientDbRepositoryFactory
            .CreateReadAsync<ClientAuditRebuttalHistories>(qmsSchemaName);
        var rows = await repo.ListAsync(
            new GetAgentRebuttalCommentSpec(auditUploadAccountID), cancellationToken);
        return rows.FirstOrDefault()?.RebuttalComments ?? string.Empty;
    }

    // SELECT ISNULL(RebuttalComments, '') FROM tblClientAuditRebuttals
    // WHERE AuditUploadAccountID = @pAuditUploadAccountID
    //
    // NOTE: requires GetTLRebuttalCommentSpec(int auditUploadAccountID).
    // The ClientAuditRebuttals entity must expose RebuttalComments (in addition to the
    // 4 columns already mapped in GetClientRebuttalAccountDetailHandler).
    private static async Task<string> FetchTLCommentAsync(
        string qmsSchemaName,
        int auditUploadAccountID,
        IQMSClientDbRepositoryFactory qmsClientDbRepositoryFactory,
        CancellationToken cancellationToken)
    {
        using var repo = await qmsClientDbRepositoryFactory
            .CreateReadAsync<ClientAuditRebuttals>(qmsSchemaName);
        var rows = await repo.ListAsync(
            new GetTLRebuttalCommentSpec(auditUploadAccountID), cancellationToken);
        return rows.FirstOrDefault()?.RebuttalComments ?? string.Empty;
    }
}
