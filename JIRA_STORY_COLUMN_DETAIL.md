# Jira Story: Port `spNGQMS_GetClientRebuttalAccountColumnDetail` to C# CQRS Handler

**Type:** Story
**Epic:** Rebuttal Workflow Action — SP Migration

## Description

Replace the dynamic-SQL stored procedure `spNGQMS_GetClientRebuttalAccountColumnDetail` with a
fully testable C# handler following the existing CQRS/Ardalis pattern. The handler, models, and
pure logic layer are already written. Remaining work is the supporting infrastructure (specs,
entity column additions, endpoint wiring, and tests).

The SP takes a single audit account ID and returns a JSON object containing:
- `AccountColumns` — the configured feedback columns (display name + value from `tblAuditUploadAccounts`)
- `QualityScore` — raw score from `tblAuditUploadAccounts`
- `IsError` — `"2"` when `QualityScore = 100`, `"1"` otherwise
- `TLComment` — from `tblClientAuditRebuttals`
- `AgentComment` — from `tblClientAuditRebuttalHistories` where `ClientAuditRebuttalStatusFromID = 5`

---

## What Is Already Done

| Artifact | File | Status |
|---|---|---|
| Handler | `GetClientRebuttalAccountColumnDetailHandler.cs` | Done |
| Models + Query | `ClientRebuttalAccountColumnDetailModels.cs` | Done |
| Pure logic (IsError, column mapping) | `ClientRebuttalAccountColumnDetailCommon.cs` | Done |

---

## Sub-Tasks & Logic Breakdown

### Sub-task 1 — Ardalis Specifications (4 new specs)
**Estimate: 2h**

Each spec mirrors one of the handler's `FetchXxx` methods:

| Spec Class | Table(s) Queried | Key Filter | Notes |
|---|---|---|---|
| `GetClientFeedbackColumnsWithDisplayByAuditFormSpec` | `tblPGAuditForms → tblPGUploadTemplates → tblClientFeedbackColumns → tblUploadColumns` | `AuditFormID`, `ShowInFeedbackScreen = 1` | Extends `GetClientFeedbackColumnsByAuditFormSpec`: also selects `UploadColumnDisplayName`, adds the `ShowInFeedbackScreen` predicate, orders by `DisplayOrder` |
| `GetAuditUploadAccountByIdSpec` | `tblAuditUploadAccounts` | `AuditUploadAccountID` | Single-row lookup; accepts `IEnumerable<string> selectColumns` for dynamic column projection (same pattern as `GetClientFeedbackAuditUploadAccountsSpec`) |
| `GetAgentRebuttalCommentSpec` | `tblClientAuditRebuttalHistories` | `AuditUploadAccountID`, `ISNULL(ClientAuditRebuttalStatusFromID, 0) = 5` | Different status field from the existing `GetClientAuditRebuttalHistoriesSpec` — `StatusFromID` (the prior state), not `StatusID` |
| `GetTLRebuttalCommentSpec` | `tblClientAuditRebuttals` | `AuditUploadAccountID` | Simple single-row comment lookup; no status filter |

---

### Sub-task 2 — Entity Column Additions (2 entities, no new entities)
**Estimate: 1h**

No new entity classes are needed; two existing entities require additional mapped columns:

| Entity | Table | Columns to Add |
|---|---|---|
| `ClientAuditRebuttalHistories` | `tblClientAuditRebuttalHistories` | `RebuttalComments` (NVARCHAR), `ClientAuditRebuttalStatusFromID` (INT?) |
| `ClientAuditRebuttals` | `tblClientAuditRebuttals` | `RebuttalComments` (NVARCHAR) |

`AuditUploadAccounts` already has `QualityScore` mapped (used in the existing handler); verify the
dynamic feedback columns are accessible via `GetProperty.GetPropertyValue` reflection.

---

### Sub-task 3 — `IQMSClientDbRepositoryFactory` Schema-Name Overload (shared with sibling story)
**Estimate: 0h (already required by `spNGQMS_GetClientRebuttalAccountDetail` story)**

The handler calls `qmsClientDbRepositoryFactory.CreateReadAsync<T>(qmsSchemaName)` with a raw
schema name string. If the sibling story has already added this overload, no further work is needed.
If not, add it here (see the sibling story for the full spec — estimated 1h).

---

### Sub-task 4 — HTTP Endpoint Wiring
**Estimate: 0.5h**

Register a GET/POST endpoint (matching the pattern of other rebuttal handlers) that maps request
parameters to `GetClientRebuttalAccountColumnDetailQuery` and invokes the handler.

Parameters to map:

| Query Param | Type | Maps To |
|---|---|---|
| `auditFormID` | `int` | `AuditFormID` |
| `auditUploadAccountID` | `int` | `AuditUploadAccountID` |
| `qmsSchemaName` | `string` | `QMSSchemaName` |

---

### Sub-task 5 — Unit Tests for `ClientRebuttalAccountColumnDetailCommon`
**Estimate: 1h**

The pure-logic class is fully side-effect-free. Cover:

- `ResolveIsError(int? qualityScore)`:
  - `qualityScore = 100` → `"2"`
  - `qualityScore = 99` → `"1"`
  - `qualityScore = 0` → `"1"`
  - `qualityScore = null` → `"1"`
- `BuildAccountColumns(feedbackColumns, auaValues)`:
  - All columns present in the AUA dictionary — values populated correctly
  - Column name missing from dictionary — `UploadColumnValue` is `null`
  - Empty `feedbackColumns` → empty list
  - `UploadColumnDisplayName` preserved exactly from `FeedbackColumnInfo`

---

### Sub-task 6 — Integration / Smoke Test
**Estimate: 1h**

Wire a test against a real (dev) database using the reference inputs from the SP file:

```
AuditFormID           = 1152
AuditUploadAccountID  = 7
QMSSchemaName         = 'NexGenQMSParallon.ParallonSDS'
```

Verify that `AccountColumns`, `QualityScore`, `IsError`, `TLComment`, and `AgentComment` match
the SP output to confirm the port is faithful.

---

## Total Estimation

| Sub-task | Estimate |
|---|---|
| Specifications (4 specs) | 2h |
| Entity column additions | 1h |
| Factory overload (if not done) | 0–1h |
| Endpoint wiring | 0.5h |
| Unit tests | 1h |
| Integration smoke test | 1h |
| **Total** | **5.5–6.5h** |

---

## Acceptance Criteria

- [ ] All 4 specs created and registered with their repositories
- [ ] `ClientAuditRebuttalHistories` exposes `RebuttalComments` and `ClientAuditRebuttalStatusFromID`
- [ ] `ClientAuditRebuttals` exposes `RebuttalComments`
- [ ] `IQMSClientDbRepositoryFactory.CreateReadAsync<T>(string schemaName)` overload available
- [ ] Handler builds and resolves via DI with no runtime errors
- [ ] Unit tests for `ClientRebuttalAccountColumnDetailCommon` pass (all `ResolveIsError` cases + `BuildAccountColumns` edge cases)
- [ ] Integration test: output matches SP output for reference inputs (`AuditFormID=1152`, `AuditUploadAccountID=7`)
- [ ] SP is retired / marked deprecated after sign-off

---

## Implementation Order (Critical Path)

```
Entity column additions  ──►  Specifications  ──►  Factory overload  ──►  Handler runs end-to-end
                                                                              │
                                                                              ├──►  Endpoint wiring
                                                                              └──►  Tests (unit + integration)
```

Unit tests can be written immediately — `ClientRebuttalAccountColumnDetailCommon` has no
infrastructure dependencies.

---

## Key Differences from Sibling Story (`spNGQMS_GetClientRebuttalAccountDetail`)

| Aspect | `AccountDetail` SP | `AccountColumnDetail` SP |
|---|---|---|
| Scope | List of accounts for a user | Single account's column values |
| Dynamic columns | Used for AUA projection + filtering | Used for AUA projection + display |
| Comment source | N/A | `RebuttalComments` from both tables |
| Status field for agent comment | N/A | `ClientAuditRebuttalStatusFromID = 5` (not `StatusID`) |
| New entities required | 3 | 0 (column additions only) |
| New specs required | 8 | 4 |

---

## Reference

- SP source: `spNGQMS_GetClientRebuttalAccountColumnDetail.sql`
- Handler: `GetClientRebuttalAccountColumnDetailHandler.cs`
- Models: `ClientRebuttalAccountColumnDetailModels.cs`
- Business logic: `ClientRebuttalAccountColumnDetailCommon.cs`
- Sibling story: `JIRA_STORY.md` (`spNGQMS_GetClientRebuttalAccountDetail`)
