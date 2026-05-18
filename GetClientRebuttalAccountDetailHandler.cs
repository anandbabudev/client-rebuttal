using AdroitQMS.CA.Core.AuditAggregate;
using AdroitQMS.CA.Core.AuditAggregate.Specifications;
using AdroitQMS.CA.SharedKernel.Interfaces;
using AdroitQMS.SharedKernel.Interfaces;
using Ardalis.Result;
using Immediate.Handlers.Shared;
using OpenTelemetry.Trace;

namespace AdroitQMS.CA.Application.RebuttalWorkflowAction.Queries.GetClientRebuttalAccountDetail;

// C# port of spNGQMS_GetClientRebuttalAccountDetail.
//
// The SP builds and EXECUTEs a dynamic SQL string whose only moving parts are:
//   * the client-feedback column list (configured per audit form),
//   * the rebuttal table CRH is joined to (current vs. history),
//   * the AUA.IsError filter, and
//   * the @pFlag-driven WHERE conditions.
// Here those become explicit, testable steps: fetch the inputs, project
// #tblOpenAccountDetail in memory, then count / filter via ClientRebuttalAccountDetailCommon.
[Handler]
public static partial class GetClientRebuttalAccountDetailHandler
{
    private static async ValueTask<Result<ClientRebuttalAccountDetailResult>> HandleAsync(
        GetClientRebuttalAccountDetailQuery request,
        Tracer trace,
        IHrmsReadRepository hrmsReadRepository,
        INexgenReadRepository nexgenReadRepository,
        INexgenQmsReadRepository nexgenQmsReadRepository,
        IQMSClientDbRepositoryFactory qmsClientDbRepositoryFactory,
        CancellationToken cancellationToken)
    {
        using var span = trace.StartActiveSpan(nameof(GetClientRebuttalAccountDetailHandler));

        var mode = ClientRebuttalAccountDetailCommon.ResolveMode(request.Flag);

        try
        {
            // --- @UploadTypeID guard ---------------------------------------------------
            // The SP emits an error row when the form is not a Client Feedback form, but
            // then carries on with a NULL @Columns — which makes the whole dynamic query
            // NULL and effectively returns nothing. Failing fast is the honest port.
            var uploadTypeID = await FetchUploadTypeIdAsync(
                request.AuditFormID, nexgenQmsReadRepository, cancellationToken);
            if (uploadTypeID != ClientRebuttalAccountDetailCommon.ClientFeedbackUploadTypeID)
            {
                return Result.Error("Given Audit Form does not belongs to Client Feedback");
            }

            // --- inputs that drive the (formerly dynamic) query ------------------------
            var reportingAuthorityID = await FetchReportingAuthorityIdAsync(
                request.WorkedByID, hrmsReadRepository, cancellationToken);

            var vertical = await FetchVerticalAsync(
                request.AuditFormID, nexgenReadRepository, cancellationToken);

            var sourcePlan = ClientRebuttalAccountDetailCommon.ResolveSource(vertical, mode);

            var feedbackColumns = await FetchFeedbackColumnNamesAsync(
                request.AuditFormID, nexgenQmsReadRepository, cancellationToken);

            // --- #tblOpenAccountDetail population --------------------------------------
            var auditUploads = await FetchAuditUploadsAsync(
                request.AuditFormID, nexgenQmsReadRepository, cancellationToken);

            var uploadAccounts = await FetchUploadAccountsAsync(
                request.QMSSchemaName, auditUploads.Select(x => x.AuditUploadID),
                feedbackColumns, sourcePlan.ErrorValues, qmsClientDbRepositoryFactory, cancellationToken);

            var accountIds = uploadAccounts
                .Select(x => (long)(x.AuditUploadAccountID ?? 0))
                .ToList();

            var rebuttals = await FetchRebuttalsAsync(
                request.QMSSchemaName, accountIds, sourcePlan.UseHistoriesTable,
                qmsClientDbRepositoryFactory, cancellationToken);

            var feedbacks = await FetchRebuttalFeedbacksAsync(
                request.QMSSchemaName, accountIds, qmsClientDbRepositoryFactory, cancellationToken);

            var openAccounts = ClientRebuttalAccountDetailCommon.BuildOpenAccountDetail(
                auditUploads, uploadAccounts, rebuttals, feedbacks, feedbackColumns, mode);

            // --- counts + detail (the final FOR JSON SELECT) ---------------------------
            var result = new ClientRebuttalAccountDetailResult
            {
                AccountCountDetail = ClientRebuttalAccountDetailCommon.BuildCounts(
                    openAccounts, mode, request.WorkedByID, reportingAuthorityID),
                AccountDetail = ClientRebuttalAccountDetailCommon.BuildAccountDetail(
                    openAccounts, mode, request.WorkedByID),
            };

            return Result.Success(result);
        }
        catch (Exception ex)
        {
            span.SetStatus(Status.Error.WithDescription(ex.Message));
            return Result.Error(ex.Message);
        }
    }

    // SELECT @ReportingAuthorityID = ISNULL(ReportingAuthorityID, 0)
    // FROM HRMS.dbo.tblEmployees WHERE EmployeeID = @pWorkedByID
    //
    // NOTE: the SP also computes @IsReportingAuthorityID right after this, but never reads
    // it — dead code, so it is intentionally not ported.
    private static async Task<int> FetchReportingAuthorityIdAsync(
        int workedByID,
        IHrmsReadRepository hrmsReadRepository,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> selectColumns = ["EmployeeID", "ReportingAuthorityID"];
        var employees = await hrmsReadRepository.ListAsync(
            new GetEmployeesBYIDSpec([workedByID], selectColumns), cancellationToken);
        return employees.FirstOrDefault()?.ReportingAuthorityID ?? 0;
    }

    // SELECT @UploadTypeID = UT.UploadTypeID
    // FROM tblPGAuditForms -> tblPGUploadTemplates -> tblUploadTypes
    // WHERE PGAF.AuditFormID = @pAuditFormID
    //
    // NOTE: requires GetUploadTypeByAuditFormSpec.
    private static async Task<int?> FetchUploadTypeIdAsync(
        int auditFormID,
        INexgenQmsReadRepository nexgenQmsReadRepository,
        CancellationToken cancellationToken)
    {
        var rows = await nexgenQmsReadRepository.ListAsync(
            new GetUploadTypeByAuditFormSpec(auditFormID), cancellationToken);
        return rows.FirstOrDefault()?.UploadTypeID;
    }

    // SELECT @Vertical = cd.vertical
    // FROM Nexgen.dbo.tblcodingdatabases -> tblprojectgroups -> nexgenqms.dbo.tblpgauditforms
    // WHERE cd.vertical IN ('FC','PC') AND af.AuditFormID = @pAuditFormID
    //
    // NOTE: requires GetVerticalByAuditFormSpec.
    private static async Task<string?> FetchVerticalAsync(
        int auditFormID,
        INexgenReadRepository nexgenReadRepository,
        CancellationToken cancellationToken)
    {
        var rows = await nexgenReadRepository.ListAsync(
            new GetVerticalByAuditFormSpec(auditFormID), cancellationToken);
        return rows.FirstOrDefault()?.Vertical;
    }

    // SELECT @Columns = ... QUOTENAME(UC.UploadColumnName) ...
    // FROM tblPGAuditForms -> tblPGUploadTemplates -> tblClientFeedbackColumns -> tblUploadColumns
    // WHERE PGAF.AuditFormID = @pAuditFormID
    //
    // NOTE: requires GetClientFeedbackColumnsByAuditFormSpec.
    private static async Task<IReadOnlyList<string>> FetchFeedbackColumnNamesAsync(
        int auditFormID,
        INexgenQmsReadRepository nexgenQmsReadRepository,
        CancellationToken cancellationToken)
    {
        var rows = await nexgenQmsReadRepository.ListAsync(
            new GetClientFeedbackColumnsByAuditFormSpec(auditFormID), cancellationToken);
        return rows.Select(x => x.UploadColumnName).ToList();
    }

    // FROM NexgenQMS.dbo.tblAuditUploads UP WHERE UP.AuditFormID = @pAuditFormID
    //
    // NOTE: requires GetAuditUploadsByAuditFormSpec + AuditUploads entity.
    private static async Task<IReadOnlyList<AuditUploads>> FetchAuditUploadsAsync(
        int auditFormID,
        INexgenQmsReadRepository nexgenQmsReadRepository,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> selectColumns = ["AuditUploadID", "AuditFormID"];
        var rows = await nexgenQmsReadRepository.ListAsync(
            new GetAuditUploadsByAuditFormSpec(auditFormID, selectColumns), cancellationToken);
        return rows.ToList();
    }

    // INNER JOIN <schema>.tblAuditUploadAccounts AUA ON UP.AuditUploadID = AUA.AuditUploadID
    // WHERE AUA.IsError IN (<errorValues>)
    // The client-feedback columns are selected dynamically per audit form.
    //
    // NOTE: requires GetClientFeedbackAuditUploadAccountsSpec, plus an
    // IQMSClientDbRepositoryFactory overload that resolves a repository from the QMS schema
    // name (the SP's @pQMSSchemaName) rather than from a ProjectGroupID.
    private static async Task<IReadOnlyList<AuditUploadAccounts>> FetchUploadAccountsAsync(
        string qmsSchemaName,
        IEnumerable<int> auditUploadIds,
        IReadOnlyList<string> feedbackColumns,
        IReadOnlyList<int> errorValues,
        IQMSClientDbRepositoryFactory qmsClientDbRepositoryFactory,
        CancellationToken cancellationToken)
    {
        using var repo = await qmsClientDbRepositoryFactory.CreateReadAsync<AuditUploadAccounts>(qmsSchemaName);
        var spec = new GetClientFeedbackAuditUploadAccountsSpec(auditUploadIds, errorValues, feedbackColumns);
        var rows = await repo.ListAsync(spec, cancellationToken);
        return rows.ToList();
    }

    // INNER JOIN <schema>.tblClientAuditRebuttals / tblClientAuditRebuttalHistories CRH
    //   ON CRH.AuditUploadAccountID = AUA.AuditUploadAccountID
    // The table is chosen by RebuttalSourcePlan.UseHistoriesTable (see ResolveSource).
    //
    // NOTE: requires GetClientAuditRebuttalsSpec / GetClientAuditRebuttalHistoriesSpec
    // plus ClientAuditRebuttals / ClientAuditRebuttalHistories entities.
    private static async Task<IReadOnlyList<ClientAuditRebuttalRow>> FetchRebuttalsAsync(
        string qmsSchemaName,
        IReadOnlyList<long> auditUploadAccountIds,
        bool useHistoriesTable,
        IQMSClientDbRepositoryFactory qmsClientDbRepositoryFactory,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> selectColumns =
            ["AuditUploadAccountID", "AssignedFrom", "AssignedTo", "ClientAuditRebuttalStatusID"];

        if (useHistoriesTable)
        {
            using var repo = await qmsClientDbRepositoryFactory.CreateReadAsync<ClientAuditRebuttalHistories>(qmsSchemaName);
            var rows = await repo.ListAsync(
                new GetClientAuditRebuttalHistoriesSpec(auditUploadAccountIds, selectColumns), cancellationToken);
            return rows.Select(MapRebuttalHistory).ToList();
        }

        using var currentRepo = await qmsClientDbRepositoryFactory.CreateReadAsync<ClientAuditRebuttals>(qmsSchemaName);
        var currentRows = await currentRepo.ListAsync(
            new GetClientAuditRebuttalsSpec(auditUploadAccountIds, selectColumns), cancellationToken);
        return currentRows.Select(MapRebuttal).ToList();
    }

    // LEFT JOIN <schema>.tblClientAuditRebuttalFeedbacks CRF
    //   ON CRF.AuditUploadAccountID = AUA.AuditUploadAccountID
    //
    // NOTE: requires GetClientAuditRebuttalFeedbacksSpec + ClientAuditRebuttalFeedbacks entity.
    private static async Task<IReadOnlyList<ClientAuditRebuttalFeedbackRow>> FetchRebuttalFeedbacksAsync(
        string qmsSchemaName,
        IReadOnlyList<long> auditUploadAccountIds,
        IQMSClientDbRepositoryFactory qmsClientDbRepositoryFactory,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> selectColumns = ["AuditUploadAccountID", "AssignedTo", "HasRead"];
        using var repo = await qmsClientDbRepositoryFactory.CreateReadAsync<ClientAuditRebuttalFeedbacks>(qmsSchemaName);
        var rows = await repo.ListAsync(
            new GetClientAuditRebuttalFeedbacksSpec(auditUploadAccountIds, selectColumns), cancellationToken);
        return rows.Select(MapRebuttalFeedback).ToList();
    }

    private static ClientAuditRebuttalRow MapRebuttal(ClientAuditRebuttals r) => new(
        AuditUploadAccountID: r.AuditUploadAccountID,
        AssignedFrom: r.AssignedFrom,
        AssignedTo: r.AssignedTo,
        ClientAuditRebuttalStatusID: r.ClientAuditRebuttalStatusID);

    private static ClientAuditRebuttalRow MapRebuttalHistory(ClientAuditRebuttalHistories r) => new(
        AuditUploadAccountID: r.AuditUploadAccountID,
        AssignedFrom: r.AssignedFrom,
        AssignedTo: r.AssignedTo,
        ClientAuditRebuttalStatusID: r.ClientAuditRebuttalStatusID);

    private static ClientAuditRebuttalFeedbackRow MapRebuttalFeedback(ClientAuditRebuttalFeedbacks r) => new(
        AuditUploadAccountID: r.AuditUploadAccountID,
        AssignedTo: r.AssignedTo,
        HasRead: r.HasRead ?? false);
}
