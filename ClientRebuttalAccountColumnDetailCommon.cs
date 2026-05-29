namespace AdroitQMS.CA.Application.RebuttalWorkflowAction.Queries.GetClientRebuttalAccountColumnDetail;

// Pure, side-effect-free building blocks for GetClientRebuttalAccountColumnDetailHandler
// (mirrors spNGQMS_GetClientRebuttalAccountColumnDetail). Kept separate from the handler
// so the derivation rules can be unit-tested without a database.
internal static class ClientRebuttalAccountColumnDetailCommon
{
    // CASE QualityScore WHEN 100 THEN '2' ELSE '1' END
    public static string ResolveIsError(int? qualityScore) =>
        qualityScore == 100 ? "2" : "1";

    // Maps each FeedbackColumnInfo to an AccountColumn by looking up the column value
    // in the AUA dictionary (keyed by UploadColumnName).
    public static IReadOnlyList<AccountColumn> BuildAccountColumns(
        IEnumerable<FeedbackColumnInfo> feedbackColumns,
        IReadOnlyDictionary<string, object?> auaValues) =>
        feedbackColumns
            .Select(col => new AccountColumn
            {
                UploadColumnDisplayName = col.UploadColumnDisplayName,
                UploadColumnValue = auaValues.TryGetValue(col.UploadColumnName, out var v) ? v : null,
            })
            .ToList();
}
